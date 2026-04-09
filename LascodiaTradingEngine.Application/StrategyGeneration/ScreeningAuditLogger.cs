using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using MediatR;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Centralises all audit trail logging for the strategy generation pipeline.
/// Formats <see cref="LogDecisionCommand"/> payloads consistently across screening failures,
/// candidate creation, and pruning events. Structured metrics are stored in
/// <see cref="LogDecisionCommand.ContextJson"/> so they are queryable without string parsing.
///
/// <b>Thread safety:</b> Stateless — safe to share across parallel screening tasks.
/// The injected <see cref="IMediator"/> handles its own scoping.
/// </summary>
public class ScreeningAuditLogger
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly IMediator _mediator;

    public ScreeningAuditLogger(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Logs a screening failure with structured failure reason and metrics context.</summary>
    public async Task LogFailureAsync(ScreeningOutcome result, CancellationToken ct)
    {
        if (result.FailureOutcome == null) return;
        string? reserveTargetRegime = string.Equals(result.GenerationSource, "Reserve", StringComparison.OrdinalIgnoreCase)
            ? result.Regime.ToString()
            : null;

        await _mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Strategy",
            EntityId     = 0,
            DecisionType = "StrategyGeneration",
            Outcome      = result.FailureOutcome,
            Reason       = result.FailureReason ?? "",
            ContextJson  = JsonSerializer.Serialize(new
            {
                failureReason = result.Failure.ToString(),
                strategyType = result.Strategy.StrategyType.ToString(),
                symbol = result.Strategy.Symbol,
                timeframe = result.Strategy.Timeframe.ToString(),
                regime = result.Regime.ToString(),
                observedRegime = result.ObservedRegime.ToString(),
                generationSource = result.GenerationSource,
                reserveTargetRegime,
            }, JsonOpts),
            Source       = "StrategyGenerationWorker"
        }, ct);
    }

    /// <summary>Logs a successful candidate creation with structured IS/OOS metrics in ContextJson.</summary>
    public async Task LogCreationAsync(ScreeningOutcome candidate, long strategyId, CancellationToken ct)
    {
        await _mediator.Send(new LogDecisionCommand
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
        await _mediator.Send(new LogDecisionCommand
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
        await _mediator.Send(new LogDecisionCommand
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
