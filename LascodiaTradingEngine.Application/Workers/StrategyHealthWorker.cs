using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyEnsemble.Commands.RebalanceEnsemble;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically evaluates the health of each active strategy
/// based on its last 50 executed signals, persists a StrategyPerformanceSnapshot,
/// and auto-pauses + triggers optimization for critically degraded strategies.
/// </summary>
public class StrategyHealthWorker : BackgroundService
{
    private readonly ILogger<StrategyHealthWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan PollingInterval    = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RebalanceInterval  = TimeSpan.FromDays(7);

    private DateTime _lastRebalancedAt = DateTime.MinValue;

    public StrategyHealthWorker(ILogger<StrategyHealthWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyHealthWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAllActiveStrategiesAsync(stoppingToken);
                await TriggerWeeklyRebalanceIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in StrategyHealthWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("StrategyHealthWorker stopped");
    }

    /// <summary>
    /// Triggers a full ensemble rebalance once per week by dispatching
    /// <see cref="RebalanceEnsembleCommand"/>. Weights are recalculated from the
    /// latest Sharpe ratios of all active strategies.
    /// </summary>
    private async Task TriggerWeeklyRebalanceIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastRebalancedAt < RebalanceInterval)
            return;

        try
        {
            using var scope  = _scopeFactory.CreateScope();
            var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

            _logger.LogInformation("StrategyHealthWorker: triggering scheduled weekly ensemble rebalance");
            await mediator.Send(new RebalanceEnsembleCommand(), ct);
            _lastRebalancedAt = DateTime.UtcNow;

            _logger.LogInformation("StrategyHealthWorker: weekly ensemble rebalance completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StrategyHealthWorker: weekly ensemble rebalance failed");
        }
    }

    private async Task EvaluateAllActiveStrategiesAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var strategies = await readContext.GetDbContext()
            .Set<Strategy>()
            .Where(x => x.Status == StrategyStatus.Active && !x.IsDeleted)
            .ToListAsync(ct);

        foreach (var strategy in strategies)
        {
            try
            {
                await EvaluateStrategyAsync(strategy, writeContext, readContext, mediator, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrategyHealthWorker: evaluation failed for strategy {StrategyId}", strategy.Id);
            }
        }
    }

    private async Task EvaluateStrategyAsync(
        Strategy strategy,
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext readContext,
        IMediator mediator,
        CancellationToken ct)
    {
        // Load the last 50 executed signals for this strategy
        var signals = await readContext.GetDbContext()
            .Set<TradeSignal>()
            .Where(x => x.StrategyId == strategy.Id
                     && x.Status == TradeSignalStatus.Executed
                     && x.OrderId != null
                     && !x.IsDeleted)
            .OrderByDescending(x => x.GeneratedAt)
            .Take(50)
            .ToListAsync(ct);

        if (signals.Count == 0)
        {
            _logger.LogDebug(
                "StrategyHealthWorker: no executed signals for strategy {StrategyId}, skipping", strategy.Id);
            return;
        }

        // Load the associated filled orders to compute PnL per signal
        var orderIds = signals
            .Where(s => s.OrderId.HasValue)
            .Select(s => s.OrderId!.Value)
            .Distinct()
            .ToList();

        var orders = await readContext.GetDbContext()
            .Set<Order>()
            .Where(x => orderIds.Contains(x.Id) && x.Status == OrderStatus.Filled && !x.IsDeleted)
            .ToDictionaryAsync(x => x.Id, ct);

        var pnlList = new List<decimal>();

        foreach (var signal in signals)
        {
            if (!signal.OrderId.HasValue || !orders.TryGetValue(signal.OrderId.Value, out var order))
                continue;

            if (order.FilledPrice is null) continue;

            // Simplified PnL: pips * lots * pip value (100,000 units per standard lot)
            decimal pnl;
            if (signal.Direction == TradeDirection.Buy)
                pnl = (order.FilledPrice.Value - signal.EntryPrice) * signal.SuggestedLotSize * 100_000m;
            else
                pnl = (signal.EntryPrice - order.FilledPrice.Value) * signal.SuggestedLotSize * 100_000m;

            pnlList.Add(pnl);
        }

        if (pnlList.Count == 0)
        {
            _logger.LogDebug(
                "StrategyHealthWorker: no filled orders with PnL data for strategy {StrategyId}, skipping", strategy.Id);
            return;
        }

        int totalTrades   = pnlList.Count;
        int winningTrades = pnlList.Count(p => p > 0);
        int losingTrades  = pnlList.Count(p => p <= 0);

        decimal winRate     = totalTrades > 0 ? (decimal)winningTrades / totalTrades : 0m;
        decimal grossProfit = pnlList.Where(p => p > 0).Sum();
        decimal grossLoss   = Math.Abs(pnlList.Where(p => p < 0).Sum());
        decimal profitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 2m : 0m;

        // Simple Sharpe estimate: mean PnL / stddev PnL
        decimal meanPnl  = pnlList.Average();
        decimal variance = pnlList.Select(p => (p - meanPnl) * (p - meanPnl)).Average();
        decimal stddev   = variance > 0 ? (decimal)Math.Sqrt((double)variance) : 1m;
        decimal sharpe   = stddev > 0 ? meanPnl / stddev : 0m;

        // Max drawdown: peak-to-trough on cumulative PnL series
        decimal peak     = 0m;
        decimal maxDrawdown = 0m;
        decimal running  = 0m;
        foreach (var p in pnlList)
        {
            running += p;
            if (running > peak) peak = running;
            decimal drawdown = peak > 0 ? (peak - running) / peak * 100m : 0m;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        decimal totalPnL = pnlList.Sum();

        // HealthScore = 0.4*WinRate + 0.3*min(1, PF/2) + 0.3*max(0, 1 - MaxDrawdownPct/20)
        decimal healthScore =
            0.4m * winRate
            + 0.3m * Math.Min(1m, profitFactor / 2m)
            + 0.3m * Math.Max(0m, 1m - maxDrawdown / 20m);

        StrategyHealthStatus healthStatus = healthScore >= 0.6m ? StrategyHealthStatus.Healthy
            : healthScore >= 0.3m ? StrategyHealthStatus.Degrading
            : StrategyHealthStatus.Critical;

        var snapshot = new StrategyPerformanceSnapshot
        {
            StrategyId     = strategy.Id,
            WindowTrades   = totalTrades,
            WinningTrades  = winningTrades,
            LosingTrades   = losingTrades,
            WinRate        = winRate,
            ProfitFactor   = profitFactor,
            SharpeRatio    = sharpe,
            MaxDrawdownPct = maxDrawdown,
            TotalPnL       = totalPnL,
            HealthScore    = healthScore,
            HealthStatus   = healthStatus,
            EvaluatedAt    = DateTime.UtcNow
        };

        await writeContext.GetDbContext()
            .Set<StrategyPerformanceSnapshot>()
            .AddAsync(snapshot, ct);

        // Auto-pause critical strategies and trigger optimization
        OptimizationRun? optimizationRun = null;

        if (healthStatus == StrategyHealthStatus.Critical)
        {
            _logger.LogWarning(
                "StrategyHealthWorker: strategy {StrategyId} is Critical (HealthScore={Score:F2}), auto-pausing and triggering optimization",
                strategy.Id, healthScore);

            var liveStrategy = await writeContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == strategy.Id && !x.IsDeleted, ct);

            if (liveStrategy is not null && liveStrategy.Status == StrategyStatus.Active)
                liveStrategy.Status = StrategyStatus.Paused;

            optimizationRun = new OptimizationRun
            {
                StrategyId  = strategy.Id,
                TriggerType = TriggerType.AutoDegrading,
                Status      = OptimizationRunStatus.Queued,
                StartedAt   = DateTime.UtcNow
            };

            await writeContext.GetDbContext()
                .Set<OptimizationRun>()
                .AddAsync(optimizationRun, ct);
        }

        await writeContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "StrategyHealthWorker: strategy {StrategyId} evaluated — HealthStatus={Status}, Score={Score:F2}",
            strategy.Id, healthStatus, healthScore);

        // ── Audit trail ──────────────────────────────────────────────────────────

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Strategy",
            EntityId     = strategy.Id,
            DecisionType = "HealthEvaluation",
            Outcome      = healthStatus.ToString(),
            Reason       = $"HealthScore={healthScore:F2}, WinRate={winRate:P2}, ProfitFactor={profitFactor:F2}, MaxDrawdown={maxDrawdown:F2}%",
            Source       = "StrategyHealthWorker"
        }, ct);

        if (healthStatus == StrategyHealthStatus.Critical)
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = strategy.Id,
                DecisionType = "AutoPause",
                Outcome      = "Paused",
                Reason       = $"HealthScore={healthScore:F2} fell below critical threshold (0.3); strategy auto-paused",
                Source       = "StrategyHealthWorker"
            }, ct);

            if (optimizationRun is not null && optimizationRun.Id > 0)
            {
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "OptimizationRun",
                    EntityId     = optimizationRun.Id,
                    DecisionType = "OptimizationTriggered",
                    Outcome      = "Queued",
                    Reason       = $"Strategy {strategy.Id} is Critical — auto-degrading optimization queued",
                    Source       = "StrategyHealthWorker"
                }, ct);
            }
        }
    }
}
