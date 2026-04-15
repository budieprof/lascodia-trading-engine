using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Centralises all audit trail logging for the strategy generation pipeline.
/// Formats <see cref="LogDecisionCommand"/> payloads consistently across screening failures,
/// candidate creation, and pruning events. Structured metrics are stored in
/// <see cref="LogDecisionCommand.ContextJson"/> so they are queryable without string parsing.
///
/// <b>Thread safety:</b> Every Send creates a fresh DI scope so the underlying
/// IWriteApplicationDbContext is never shared across the planner's parallel
/// screening tasks — a single shared context would throw
/// <c>InvalidOperationException: A second operation was started on this context instance</c>.
/// </summary>
public class ScreeningAuditLogger
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly IServiceScopeFactory _scopeFactory;

    public ScreeningAuditLogger(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    private async Task SendScopedAsync(LogDecisionCommand command, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(command, ct);
    }

    /// <summary>
    /// Dual-writes a screening failure: one row to the AuditTrail (via LogDecisionCommand)
    /// and one row to StrategyGenerationFailure (via the failure store). Prior to this helper,
    /// only the audit trail path existed, which made the failure table empty despite hundreds
    /// of rejections per cycle and left operators unable to query "which gate fires most."
    /// </summary>
    private async Task SendFailureScopedAsync(
        LogDecisionCommand command,
        ScreeningOutcome result,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(command, ct);

        if (result.Strategy is null)
            return;

        try
        {
            var failureStore = scope.ServiceProvider.GetService<IStrategyGenerationFailureStore>();
            var writeCtx = scope.ServiceProvider.GetService<IWriteApplicationDbContext>();
            if (failureStore is null || writeCtx is null)
                return;

            var candidateHash = ComputeCandidateHash(result.Strategy);
            var record = new StrategyGenerationFailureRecord(
                CandidateId: candidateHash,
                CycleId: null,
                CandidateHash: candidateHash,
                StrategyType: result.Strategy.StrategyType,
                Symbol: result.Strategy.Symbol,
                Timeframe: result.Strategy.Timeframe,
                ParametersJson: result.Strategy.ParametersJson ?? "",
                FailureStage: result.Failure.ToString(),
                FailureReason: result.FailureReason ?? result.FailureOutcome ?? "Unknown",
                DetailsJson: command.ContextJson);

            await failureStore.RecordFailuresAsync(writeCtx.GetDbContext(), new[] { record }, ct);
            await writeCtx.GetDbContext().SaveChangesAsync(ct);
        }
        catch
        {
            // Best effort — the audit trail row is the authoritative record; the failure
            // store is an observability aid. A failure here must not block the cycle.
        }
    }

    private static string ComputeCandidateHash(Domain.Entities.Strategy strategy)
    {
        var payload = string.Concat(
            strategy.Symbol, "|",
            strategy.Timeframe, "|",
            strategy.StrategyType, "|",
            strategy.ParametersJson ?? "");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", Inv));
        return sb.ToString();
    }

    /// <summary>Logs a screening failure with structured failure reason and metrics context.</summary>
    public async Task LogFailureAsync(ScreeningOutcome result, CancellationToken ct)
    {
        if (result.FailureOutcome == null) return;
        string? reserveTargetRegime = string.Equals(result.GenerationSource, "Reserve", StringComparison.OrdinalIgnoreCase)
            ? result.Regime.ToString()
            : null;

        // Capture the IS-backtest metrics so ScreeningSurrogateService can mine
        // past failures to fit a TPE surrogate and propose next-cycle params.
        // Strategy may be null on very early failures (before tempStrategy is built).
        var trainResult = result.TrainResult;
        var hasMetrics  = trainResult is not null && result.Strategy is not null;

        var command = new LogDecisionCommand
        {
            EntityType   = "Strategy",
            EntityId     = 0,
            DecisionType = "StrategyGeneration",
            Outcome      = result.FailureOutcome,
            Reason       = result.FailureReason ?? "",
            ContextJson  = JsonSerializer.Serialize(new
            {
                failureReason = result.Failure.ToString(),
                strategyType = result.Strategy?.StrategyType.ToString(),
                symbol = result.Strategy?.Symbol,
                timeframe = result.Strategy?.Timeframe.ToString(),
                regime = result.Regime.ToString(),
                observedRegime = result.ObservedRegime.ToString(),
                generationSource = result.GenerationSource,
                reserveTargetRegime,
                paramsJson = hasMetrics ? result.Strategy!.ParametersJson : null,
                isWinRate = hasMetrics ? (double?)trainResult!.WinRate : null,
                isProfitFactor = hasMetrics ? (double?)trainResult!.ProfitFactor : null,
                isSharpeRatio = hasMetrics ? (double?)trainResult!.SharpeRatio : null,
                isMaxDrawdownPct = hasMetrics ? (double?)trainResult!.MaxDrawdownPct : null,
                isTotalTrades = hasMetrics ? (int?)trainResult!.TotalTrades : null,
            }, JsonOpts),
            Source       = "StrategyGenerationWorker"
        };
        await SendFailureScopedAsync(command, result, ct);
    }

    /// <summary>Logs a successful candidate creation with structured IS/OOS metrics in ContextJson.</summary>
    public async Task LogCreationAsync(ScreeningOutcome candidate, long strategyId, CancellationToken ct)
    {
        await SendScopedAsync(new LogDecisionCommand
        {
            EntityType   = "Strategy",
            EntityId     = strategyId,
            DecisionType = "StrategyGeneration",
            Outcome      = "Created",
            Reason       = string.Format(Inv,
                "Screening passed for {0} regime. IS: WR={1:P1}, PF={2:F2}, Sharpe={3:F2}. OOS: WR={4:P1}, PF={5:F2}, Sharpe={6:F2}",
                candidate.Regime,
                candidate.TrainResult.WinRate, candidate.TrainResult.ProfitFactor,
                candidate.TrainResult.SharpeRatio,
                candidate.OosResult.WinRate, candidate.OosResult.ProfitFactor,
                candidate.OosResult.SharpeRatio),
            ContextJson  = JsonSerializer.Serialize(new
            {
                regime = candidate.Regime.ToString(),
                observedRegime = candidate.ObservedRegime.ToString(),
                generationSource = candidate.GenerationSource,
                reserveTargetRegime = string.Equals(candidate.GenerationSource, "Reserve", StringComparison.OrdinalIgnoreCase)
                    ? candidate.Regime.ToString()
                    : null,
                isWinRate = (double)candidate.TrainResult.WinRate,
                isProfitFactor = (double)candidate.TrainResult.ProfitFactor,
                isSharpeRatio = (double)candidate.TrainResult.SharpeRatio,
                isMaxDrawdownPct = (double)candidate.TrainResult.MaxDrawdownPct,
                isTotalTrades = candidate.TrainResult.TotalTrades,
                oosWinRate = (double)candidate.OosResult.WinRate,
                oosProfitFactor = (double)candidate.OosResult.ProfitFactor,
                oosSharpeRatio = (double)candidate.OosResult.SharpeRatio,
                oosMaxDrawdownPct = (double)candidate.OosResult.MaxDrawdownPct,
                oosTotalTrades = candidate.OosResult.TotalTrades,
                equityCurveR2 = candidate.Metrics?.EquityCurveR2,
                monteCarloPValue = candidate.Metrics?.MonteCarloPValue,
                walkForwardWindowsPassed = candidate.Metrics?.WalkForwardWindowsPassed,
            }, JsonOpts),
            Source = "StrategyGenerationWorker"
        }, ct);
    }

    /// <summary>Logs a Draft strategy being pruned after repeated backtest failures.</summary>
    public async Task LogPruningAsync(long strategyId, string name, int failedCount, CancellationToken ct)
    {
        await SendScopedAsync(new LogDecisionCommand
        {
            EntityType   = "Strategy",
            EntityId     = strategyId,
            DecisionType = "StrategyGeneration",
            Outcome      = "Pruned",
            Reason       = $"Draft strategy '{name}' failed {failedCount} backtests with no successful completion",
            ContextJson  = JsonSerializer.Serialize(new
            {
                strategyName = name,
                failedBacktestCount = failedCount,
            }, JsonOpts),
            Source       = "StrategyGenerationWorker"
        }, ct);
    }

    public async Task LogExecutionFailureAsync(
        Domain.Enums.StrategyType strategyType,
        string symbol,
        Domain.Enums.Timeframe timeframe,
        Domain.Enums.MarketRegime targetRegime,
        Domain.Enums.MarketRegime observedRegime,
        string generationSource,
        string outcome,
        string failureCode,
        Domain.Enums.MarketRegime? reserveTargetRegime,
        CancellationToken ct)
    {
        await SendScopedAsync(new LogDecisionCommand
        {
            EntityType = "Strategy",
            EntityId = 0,
            DecisionType = "StrategyGeneration",
            Outcome = outcome,
            Reason = $"{failureCode} while screening {strategyType} on {symbol}/{timeframe}",
            ContextJson = JsonSerializer.Serialize(new
            {
                strategyType = strategyType.ToString(),
                symbol,
                timeframe = timeframe.ToString(),
                targetRegime = targetRegime.ToString(),
                observedRegime = observedRegime.ToString(),
                generationSource,
                failureCode,
                reserveTargetRegime = reserveTargetRegime?.ToString(),
            }, JsonOpts),
            Source = "StrategyGenerationWorker"
        }, ct);
    }
}
