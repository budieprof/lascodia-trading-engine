using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MediatR;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes a rolling <b>daily</b> performance audit for every strategy based on filled
/// orders within a configurable day window (default 30 days), and persists a
/// <see cref="StrategyPerformanceSnapshot"/>.
///
/// <para>
/// Complements <see cref="StrategyHealthWorker"/>, which evaluates the last 50 signals
/// every 60 seconds (real-time health). This worker runs on a longer cadence (configurable,
/// default 1 hour) over a larger time window to surface <em>trend degradation</em> that
/// real-time sampling may miss — e.g. a strategy that looks healthy in the last 50 trades
/// but is slowly losing edge over 30 days.
/// </para>
///
/// Additional actions:
/// <list type="bullet">
///   <item><description>
///     Strategies with <c>Degrading</c> health for two consecutive evaluations trigger
///     an <see cref="OptimizationRun"/> (queued, not yet running).
///   </description></item>
///   <item><description>
///     Strategies with <c>Critical</c> health are auto-paused if not already paused.
///   </description></item>
/// </list>
///
/// Config keys (EngineConfig, all hot-reloadable):
/// <list type="bullet">
///   <item><description><c>StrategyFeedback:PollIntervalHours</c> — default 1</description></item>
///   <item><description><c>StrategyFeedback:WindowDays</c> — default 30</description></item>
/// </list>
/// </summary>
public sealed class StrategyFeedbackWorker : BackgroundService
{
    private const string CK_PollHours  = "StrategyFeedback:PollIntervalHours";
    private const string CK_WindowDays = "StrategyFeedback:WindowDays";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<StrategyFeedbackWorker> _logger;

    public StrategyFeedbackWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<StrategyFeedbackWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyFeedbackWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollHours = 1;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollHours  = await GetConfigAsync<int>(ctx, CK_PollHours,  1,  stoppingToken);
                int window = await GetConfigAsync<int>(ctx, CK_WindowDays, 30, stoppingToken);

                await EvaluateAllStrategiesAsync(ctx, writeCtx, mediator, window, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrategyFeedbackWorker loop error");
            }

            await Task.Delay(TimeSpan.FromHours(pollHours), stoppingToken);
        }

        _logger.LogInformation("StrategyFeedbackWorker stopping.");
    }

    // ── Main evaluation loop ──────────────────────────────────────────────────

    private async Task EvaluateAllStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        int                                     windowDays,
        CancellationToken                       ct)
    {
        var strategies = await readCtx.Set<Strategy>()
            .Where(s => !s.IsDeleted &&
                        (s.Status == StrategyStatus.Active || s.Status == StrategyStatus.Paused))
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug("StrategyFeedbackWorker: evaluating {Count} strategy/strategies (window={Days}d).",
            strategies.Count, windowDays);

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();
            await EvaluateStrategyAsync(strategy, readCtx, writeCtx, mediator, windowDays, ct);
        }
    }

    private async Task EvaluateStrategyAsync(
        Strategy                                strategy,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        int                                     windowDays,
        CancellationToken                       ct)
    {
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Load all filled orders within the window for this strategy
        var orders = await readCtx.Set<Order>()
            .Where(o => o.StrategyId    == strategy.Id        &&
                        o.Status        == OrderStatus.Filled &&
                        o.FilledAt      >= windowStart        &&
                        o.FilledPrice   != null               &&
                        !o.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        if (orders.Count == 0)
        {
            _logger.LogDebug("StrategyFeedbackWorker: no filled orders in {Days}d for strategy {Id} — skipping.",
                windowDays, strategy.Id);
            return;
        }

        // ── Compute per-trade PnL ─────────────────────────────────────────────
        // Estimate PnL: direction × (FilledPrice − Price) × Quantity × contract size
        const decimal pipFactor = 10_000m;
        var pnlList = new List<decimal>(orders.Count);

        foreach (var o in orders)
        {
            decimal raw = o.OrderType == OrderType.Buy
                ? (o.FilledPrice!.Value - o.Price) * pipFactor
                : (o.Price - o.FilledPrice!.Value) * pipFactor;

            pnlList.Add(raw * o.Quantity);
        }

        // ── Metrics ───────────────────────────────────────────────────────────
        int     totalTrades   = pnlList.Count;
        int     winningTrades = pnlList.Count(p => p > 0);
        int     losingTrades  = pnlList.Count(p => p <= 0);
        decimal winRate       = (decimal)winningTrades / totalTrades;
        decimal grossProfit   = pnlList.Where(p => p > 0).Sum();
        decimal grossLoss     = Math.Abs(pnlList.Where(p => p < 0).Sum());
        decimal profitFactor  = grossLoss > 0 ? grossProfit / grossLoss
                                              : grossProfit > 0 ? 2m : 0m;

        decimal meanPnl   = pnlList.Average();
        decimal variance  = pnlList.Select(p => (p - meanPnl) * (p - meanPnl)).Average();
        decimal stddev    = variance > 0 ? (decimal)Math.Sqrt((double)variance) : 1m;
        decimal sharpe    = stddev > 0 ? meanPnl / stddev : 0m;
        decimal totalPnL  = pnlList.Sum();

        // Peak-to-trough max drawdown on cumulative PnL
        decimal peak = 0m, maxDrawdown = 0m, running = 0m;
        foreach (var p in pnlList)
        {
            running += p;
            if (running > peak) peak = running;
            decimal dd = peak > 0 ? (peak - running) / peak * 100m : 0m;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }

        // Health score (same formula as StrategyHealthWorker for consistency)
        decimal healthScore =
            0.4m * winRate
            + 0.3m * Math.Min(1m, profitFactor / 2m)
            + 0.3m * Math.Max(0m, 1m - maxDrawdown / 20m);

        StrategyHealthStatus healthStatus = healthScore >= 0.6m ? StrategyHealthStatus.Healthy
            : healthScore >= 0.3m ? StrategyHealthStatus.Degrading
            : StrategyHealthStatus.Critical;

        // ── Persist snapshot ──────────────────────────────────────────────────
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

        writeCtx.Set<StrategyPerformanceSnapshot>().Add(snapshot);

        // ── Check consecutive Degrading to queue optimization ─────────────────
        bool queuedOptimization = false;

        if (healthStatus == StrategyHealthStatus.Degrading)
        {
            var previousSnapshot = await readCtx.Set<StrategyPerformanceSnapshot>()
                .Where(s => s.StrategyId == strategy.Id && !s.IsDeleted)
                .OrderByDescending(s => s.EvaluatedAt)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            if (previousSnapshot?.HealthStatus == StrategyHealthStatus.Degrading)
            {
                bool alreadyQueued = await readCtx.Set<OptimizationRun>()
                    .AnyAsync(r => r.StrategyId == strategy.Id &&
                                   (r.Status == OptimizationRunStatus.Queued ||
                                    r.Status == OptimizationRunStatus.Running),
                              ct);

                if (!alreadyQueued)
                {
                    writeCtx.Set<OptimizationRun>().Add(new OptimizationRun
                    {
                        StrategyId  = strategy.Id,
                        TriggerType = TriggerType.AutoDegrading,
                        Status      = OptimizationRunStatus.Queued,
                        StartedAt   = DateTime.UtcNow
                    });
                    queuedOptimization = true;

                    _logger.LogWarning(
                        "StrategyFeedbackWorker: strategy {Id} Degrading for two consecutive evaluations " +
                        "over {Days}-day window — queued optimization.",
                        strategy.Id, windowDays);
                }
            }
        }

        // ── Auto-pause critical strategies ────────────────────────────────────
        if (healthStatus == StrategyHealthStatus.Critical &&
            strategy.Status == StrategyStatus.Active)
        {
            await writeCtx.Set<Strategy>()
                .Where(s => s.Id == strategy.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, StrategyStatus.Paused),
                    ct);

            _logger.LogWarning(
                "StrategyFeedbackWorker: strategy {Id} is Critical over {Days}-day window " +
                "(HealthScore={Score:F2}) — auto-paused.",
                strategy.Id, windowDays, healthScore);
        }

        await writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "StrategyFeedbackWorker: strategy {Id} — {Status} (score={Score:F2}, " +
            "trades={Trades}, PnL={Pnl:F2}, WR={WR:P1}, PF={PF:F2}) over {Days}d.",
            strategy.Id, healthStatus, healthScore,
            totalTrades, totalPnL, winRate, profitFactor, windowDays);

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Strategy",
            EntityId     = strategy.Id,
            DecisionType = "FeedbackEvaluation",
            Outcome      = healthStatus.ToString(),
            Reason       = $"30d window: WinRate={winRate:P1}, PF={profitFactor:F2}, " +
                           $"Sharpe={sharpe:F2}, MaxDD={maxDrawdown:F1}%, Trades={totalTrades}" +
                           (queuedOptimization ? " — optimization queued" : string.Empty),
            Source       = "StrategyFeedbackWorker"
        }, ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
