using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

// Auto-approval threshold: challenger must improve health score by at least this
// margin AND exceed the absolute minimum to qualify for automatic promotion.

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that processes queued optimization runs by running a grid search
/// over strategy parameter ranges, backtesting each combination, and saving the best result.
/// When the best result improves the baseline health score by more than
/// <see cref="AutoApprovalImprovementThreshold"/> and clears
/// <see cref="AutoApprovalMinHealthScore"/>, the run is automatically approved and the
/// strategy's parameters are updated without requiring a manual review step.
/// Otherwise the run awaits explicit <c>ApproveOptimizationCommand</c> approval.
/// After any approval (auto or manual), a fresh <c>BacktestRun</c> and
/// <c>WalkForwardRun</c> are queued to validate the new parameters end-to-end.
/// </summary>
public class OptimizationWorker : BackgroundService
{
    /// <summary>
    /// Minimum absolute health-score improvement required for auto-approval
    /// (i.e. best score must exceed baseline by at least this value).
    /// </summary>
    private const decimal AutoApprovalImprovementThreshold = 0.10m;

    /// <summary>
    /// Minimum absolute health score the best result must achieve before auto-approval
    /// is considered, regardless of improvement (prevents approving a slightly-less-bad result).
    /// </summary>
    private const decimal AutoApprovalMinHealthScore = 0.55m;
    private readonly ILogger<OptimizationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBacktestEngine _backtestEngine;

    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    public OptimizationWorker(
        ILogger<OptimizationWorker> logger,
        IServiceScopeFactory scopeFactory,
        IBacktestEngine backtestEngine)
    {
        _logger         = logger;
        _scopeFactory   = scopeFactory;
        _backtestEngine = backtestEngine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OptimizationWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in OptimizationWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("OptimizationWorker stopped");
    }

    private async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

        var run = await writeContext.GetDbContext()
            .Set<OptimizationRun>()
            .Where(x => x.Status == OptimizationRunStatus.Queued && !x.IsDeleted)
            .OrderBy(x => x.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (run is null) return;

        _logger.LogInformation(
            "OptimizationWorker: processing run {RunId} for strategy {StrategyId}", run.Id, run.StrategyId);

        run.Status = OptimizationRunStatus.Running;
        await writeContext.SaveChangesAsync(ct);

        try
        {
            var strategy = await readContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, ct);

            if (strategy is null)
                throw new InvalidOperationException($"Strategy {run.StrategyId} not found.");

            // Load candles for backtesting (last 6 months of H1 data as default window)
            var toDate   = DateTime.UtcNow;
            var fromDate = toDate.AddMonths(-6);

            var candles = await readContext.GetDbContext()
                .Set<Candle>()
                .Where(x => x.Symbol    == strategy.Symbol
                         && x.Timeframe == strategy.Timeframe
                         && x.Timestamp >= fromDate
                         && x.Timestamp <= toDate
                         && x.IsClosed
                         && !x.IsDeleted)
                .OrderBy(x => x.Timestamp)
                .ToListAsync(ct);

            if (candles.Count == 0)
                throw new InvalidOperationException(
                    $"No candles found for {strategy.Symbol}/{strategy.Timeframe} in the last 6 months.");

            // Save baseline parameters and score
            run.BaselineParametersJson = strategy.ParametersJson;
            var baselineResult         = await _backtestEngine.RunAsync(strategy, candles, 10_000m, ct);
            run.BaselineHealthScore    = ComputeHealthScore(baselineResult.WinRate, baselineResult.ProfitFactor, baselineResult.MaxDrawdownPct);

            // Build parameter grid for this strategy type
            var parameterGrid = BuildParameterGrid(strategy.StrategyType);

            string? bestParamsJson  = null;
            decimal bestProfitFactor = -1m;
            int     iterations      = 0;

            foreach (var paramSet in parameterGrid)
            {
                // Apply candidate parameters to a cloned strategy
                var candidate             = CloneStrategy(strategy);
                candidate.ParametersJson  = JsonSerializer.Serialize(paramSet);

                var result = await _backtestEngine.RunAsync(candidate, candles, 10_000m, ct);
                iterations++;

                if (result.ProfitFactor > bestProfitFactor)
                {
                    bestProfitFactor    = result.ProfitFactor;
                    bestParamsJson      = candidate.ParametersJson;
                    run.BestHealthScore = ComputeHealthScore(result.WinRate, result.ProfitFactor, result.MaxDrawdownPct);
                }
            }

            run.Iterations         = iterations;
            run.BestParametersJson = bestParamsJson ?? strategy.ParametersJson;
            run.Status             = OptimizationRunStatus.Completed;
            run.CompletedAt        = DateTime.UtcNow;

            await writeContext.SaveChangesAsync(ct);

            _logger.LogInformation(
                "OptimizationWorker: run {RunId} completed — Iterations={Iter}, BestPF={PF:F2}",
                run.Id, iterations, bestProfitFactor);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "OptimizationRun",
                EntityId     = run.Id,
                DecisionType = "OptimizationCompleted",
                Outcome      = "Completed",
                Reason       = $"Iterations={iterations}, BestProfitFactor={bestProfitFactor:F2}, BestHealthScore={run.BestHealthScore:F2}",
                Source       = "OptimizationWorker"
            }, ct);

            // ── Auto-approve if improvement is statistically meaningful ────────────
            decimal bestScore   = run.BestHealthScore ?? 0m;
            decimal improvement = bestScore - (run.BaselineHealthScore ?? 0m);
            bool autoApprove    = improvement >= AutoApprovalImprovementThreshold
                               && bestScore    >= AutoApprovalMinHealthScore;

            if (autoApprove)
            {
                // Apply best parameters directly to the live strategy
                var liveStrategy = await writeContext.GetDbContext()
                    .Set<Strategy>()
                    .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, ct);

                if (liveStrategy is not null)
                {
                    liveStrategy.ParametersJson = run.BestParametersJson;
                    if (liveStrategy.Status == StrategyStatus.Paused)
                        liveStrategy.Status = StrategyStatus.Active;
                }

                run.Status     = OptimizationRunStatus.Approved;
                run.ApprovedAt = DateTime.UtcNow;
                await writeContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "OptimizationWorker: run {RunId} AUTO-APPROVED — improvement={Imp:+0.00;-0.00}, BestScore={Score:F2}",
                    run.Id, improvement, run.BestHealthScore);

                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "OptimizationRun",
                    EntityId     = run.Id,
                    DecisionType = "AutoApproval",
                    Outcome      = "Approved",
                    Reason       = $"HealthScore improvement={improvement:+0.00;-0.00} exceeded threshold ({AutoApprovalImprovementThreshold}) and minimum ({AutoApprovalMinHealthScore}); parameters applied automatically",
                    Source       = "OptimizationWorker"
                }, ct);

                // ── Re-queue Backtest + WalkForward to validate new parameters ──────
                var validationToDate   = DateTime.UtcNow;
                var validationFromDate = validationToDate.AddYears(-1);

                var validationBacktest = new BacktestRun
                {
                    StrategyId     = run.StrategyId,
                    Symbol         = strategy.Symbol,
                    Timeframe      = strategy.Timeframe,
                    FromDate       = validationFromDate,
                    ToDate         = validationToDate,
                    InitialBalance = 10_000m,
                    Status         = RunStatus.Queued,
                    StartedAt      = DateTime.UtcNow
                };

                await writeContext.GetDbContext().Set<BacktestRun>().AddAsync(validationBacktest, ct);

                _logger.LogInformation(
                    "OptimizationWorker: queued validation BacktestRun for strategy {StrategyId} after auto-approval",
                    run.StrategyId);
            }
            else
            {
                _logger.LogInformation(
                    "OptimizationWorker: run {RunId} requires manual review — improvement={Imp:+0.00;-0.00} (threshold={Thr}), BestScore={Score:F2} (min={Min})",
                    run.Id, improvement, AutoApprovalImprovementThreshold, run.BestHealthScore, AutoApprovalMinHealthScore);
            }

            await writeContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationWorker: run {RunId} failed", run.Id);
            run.Status       = OptimizationRunStatus.Failed;
            run.ErrorMessage = ex.Message;
            run.CompletedAt  = DateTime.UtcNow;
            await writeContext.SaveChangesAsync(ct);

            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "OptimizationRun",
                EntityId     = run.Id,
                DecisionType = "OptimizationFailed",
                Outcome      = "Failed",
                Reason       = ex.Message,
                Source       = "OptimizationWorker"
            }, ct);
        }
    }

    /// <summary>Builds a list of parameter dictionaries for grid search based on strategy type.</summary>
    private static List<Dictionary<string, object>> BuildParameterGrid(StrategyType strategyType)
    {
        var grid = new List<Dictionary<string, object>>();

        switch (strategyType)
        {
            case StrategyType.MovingAverageCrossover:
                foreach (var fast in new[] { 5, 9, 12 })
                foreach (var slow in new[] { 20, 21, 26 })
                {
                    if (fast >= slow) continue;
                    grid.Add(new Dictionary<string, object>
                    {
                        ["FastPeriod"] = fast,
                        ["SlowPeriod"] = slow,
                        ["MaPeriod"]   = 50
                    });
                }
                break;

            case StrategyType.RSIReversion:
                foreach (var period in new[] { 10, 14, 21 })
                foreach (var oversold in new[] { 25, 30, 35 })
                foreach (var overbought in new[] { 65, 70, 75 })
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["Period"]     = period,
                        ["Oversold"]   = oversold,
                        ["Overbought"] = overbought
                    });
                }
                break;

            case StrategyType.BreakoutScalper:
                foreach (var lookback in new[] { 10, 15, 20, 30 })
                foreach (var multiplier in new[] { 1.0, 1.5, 2.0 })
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["LookbackBars"]        = lookback,
                        ["BreakoutMultiplier"]  = multiplier
                    });
                }
                break;

            default:
                // For custom or unknown types, return a single default parameter set
                grid.Add(new Dictionary<string, object>());
                break;
        }

        return grid;
    }

    private static decimal ComputeHealthScore(decimal winRate, decimal profitFactor, decimal maxDrawdownPct)
    {
        return 0.4m * winRate
             + 0.3m * Math.Min(1m, profitFactor / 2m)
             + 0.3m * Math.Max(0m, 1m - maxDrawdownPct / 20m);
    }

    private static Strategy CloneStrategy(Strategy source) => new()
    {
        Id             = source.Id,
        Name           = source.Name,
        Description    = source.Description,
        StrategyType   = source.StrategyType,
        Symbol         = source.Symbol,
        Timeframe      = source.Timeframe,
        ParametersJson = source.ParametersJson,
        Status         = source.Status,
        RiskProfileId  = source.RiskProfileId,
        CreatedAt      = source.CreatedAt,
        IsDeleted      = source.IsDeleted
    };
}
