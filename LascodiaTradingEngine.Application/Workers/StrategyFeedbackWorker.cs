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
    /// <summary>EngineConfig key: how often the worker runs, in hours (default 1).</summary>
    private const string CK_PollHours  = "StrategyFeedback:PollIntervalHours";

    /// <summary>
    /// EngineConfig key: the number of calendar days to look back when loading filled orders
    /// for the performance window (default 30 days). Increasing this value smooths out
    /// short-term volatility in the metrics; decreasing it makes the worker more responsive
    /// to recent performance changes.
    /// </summary>
    private const string CK_WindowDays = "StrategyFeedback:WindowDays";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<StrategyFeedbackWorker> _logger;

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating per-cycle async DI scopes.</param>
    /// <param name="logger">Structured logger.</param>
    public StrategyFeedbackWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<StrategyFeedbackWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. On each iteration:
    /// <list type="number">
    ///   <item><description>Opens a fresh async DI scope.</description></item>
    ///   <item><description>Hot-reloads <c>StrategyFeedback:PollIntervalHours</c> and <c>StrategyFeedback:WindowDays</c> from EngineConfig.</description></item>
    ///   <item><description>Calls <see cref="EvaluateAllStrategiesAsync"/> to process all Active and Paused strategies.</description></item>
    ///   <item><description>Waits for the configured interval before the next cycle.</description></item>
    /// </list>
    /// </summary>
    /// <param name="stoppingToken">Signalled by the host on shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyFeedbackWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default interval used before the first successful config read.
            int pollHours = 1;

            try
            {
                // Async scope ensures both EF contexts are properly disposed after each cycle.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                // Hot-reload configuration — changes take effect on the next cycle without restart.
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

    /// <summary>
    /// Loads all non-deleted strategies in <see cref="StrategyStatus.Active"/> or
    /// <see cref="StrategyStatus.Paused"/> state and evaluates each one over the
    /// configured day window.
    ///
    /// <para>
    /// Paused strategies are included so that their long-window metrics remain up to date.
    /// This allows a resumed strategy to immediately surface its historical performance
    /// without waiting for the next evaluation cycle.
    /// </para>
    /// </summary>
    /// <param name="readCtx">Read DB context for querying strategies and orders.</param>
    /// <param name="writeCtx">Write DB context for persisting snapshots and actions.</param>
    /// <param name="mediator">MediatR for audit trail entries.</param>
    /// <param name="windowDays">Number of calendar days to look back for filled orders.</param>
    /// <param name="ct">Propagated cancellation token.</param>
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
            // Check cancellation between strategies to allow a clean shutdown mid-cycle.
            ct.ThrowIfCancellationRequested();
            await EvaluateStrategyAsync(strategy, readCtx, writeCtx, mediator, windowDays, ct);
        }
    }

    /// <summary>
    /// Evaluates a single strategy over the configured day window. Computes the same
    /// composite health score as <see cref="StrategyHealthWorker"/> for consistency, but
    /// sources data from calendar-dated filled <see cref="Order"/> records rather than
    /// executed <see cref="TradeSignal"/> records, giving a broader and more reliable
    /// picture of long-horizon performance.
    ///
    /// <para>
    /// <b>PnL estimation formula:</b>
    /// <c>raw PnL = (FilledPrice − Price) × pipFactor</c> for Buy orders, inverted for
    /// Sell orders, then multiplied by <c>Quantity</c>. <c>pipFactor</c> is 10,000 —
    /// this converts price-unit differences for 4-decimal pairs (EUR/USD, GBP/USD) into
    /// approximate pip-based P&amp;L figures. For 2-decimal pairs (JPY crosses) the factor
    /// would need to be 100; this is a known simplification.
    /// </para>
    ///
    /// <para>
    /// <b>Consecutive-degrading detection:</b> If the current evaluation is
    /// <see cref="StrategyHealthStatus.Degrading"/> <em>and</em> the most recent previous
    /// snapshot for the same strategy was also Degrading, an <see cref="OptimizationRun"/>
    /// is queued (unless one is already queued or running). This provides a grace period —
    /// a single Degrading evaluation does not trigger optimization, but two consecutive
    /// ones do.
    /// </para>
    ///
    /// <para>
    /// <b>Critical auto-pause:</b> Uses <c>ExecuteUpdateAsync</c> (a bulk SQL UPDATE)
    /// rather than entity tracking to avoid loading the strategy entity separately —
    /// the strategy object from <see cref="EvaluateAllStrategiesAsync"/> was loaded
    /// <c>AsNoTracking</c>.
    /// </para>
    /// </summary>
    /// <param name="strategy">Strategy to evaluate (loaded AsNoTracking).</param>
    /// <param name="readCtx">Read DB context.</param>
    /// <param name="writeCtx">Write DB context.</param>
    /// <param name="mediator">MediatR for audit entries.</param>
    /// <param name="windowDays">Number of days to look back for filled orders.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task EvaluateStrategyAsync(
        Strategy                                strategy,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        int                                     windowDays,
        CancellationToken                       ct)
    {
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Load all filled orders within the window for this strategy.
        // FilledPrice must be non-null — orders without a fill price cannot contribute to PnL.
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
        // Estimate PnL: direction × (FilledPrice − Price) × Quantity × pip factor.
        // pipFactor = 10,000 converts price differences to approximate pip values for
        // 4-decimal pairs (EUR/USD, GBP/USD). This is a known approximation —
        // actual P&L will differ for JPY pairs (2-decimal) or instruments with non-standard
        // pip sizes, but it provides a consistent relative comparison across strategies.
        const decimal pipFactor = 10_000m;
        var pnlList = new List<decimal>(orders.Count);

        foreach (var o in orders)
        {
            decimal raw = o.OrderType == OrderType.Buy
                ? (o.FilledPrice!.Value - o.Price) * pipFactor
                : (o.Price - o.FilledPrice!.Value) * pipFactor;

            // Multiply by Quantity (lot size) to scale PnL proportionally to position size.
            pnlList.Add(raw * o.Quantity);
        }

        // ── Metrics ───────────────────────────────────────────────────────────
        int     totalTrades   = pnlList.Count;
        int     winningTrades = pnlList.Count(p => p > 0);
        int     losingTrades  = pnlList.Count(p => p <= 0);
        decimal winRate       = (decimal)winningTrades / totalTrades;
        decimal grossProfit   = pnlList.Where(p => p > 0).Sum();
        decimal grossLoss     = Math.Abs(pnlList.Where(p => p < 0).Sum());

        // Cap profit factor at 2.0 when there are no losing trades to avoid an infinite value
        // that would make the health score artificially perfect.
        decimal profitFactor  = grossLoss > 0 ? grossProfit / grossLoss
                                              : grossProfit > 0 ? 2m : 0m;

        // Unannualised Sharpe ratio — mean PnL / stddev of PnL.
        // Clamping stddev to 1 when variance is 0 avoids division by zero (all trades identical).
        decimal meanPnl   = pnlList.Average();
        decimal variance  = pnlList.Select(p => (p - meanPnl) * (p - meanPnl)).Average();
        decimal stddev    = variance > 0 ? (decimal)Math.Sqrt((double)variance) : 1m;
        decimal sharpe    = stddev > 0 ? meanPnl / stddev : 0m;
        decimal totalPnL  = pnlList.Sum();

        // Peak-to-trough max drawdown on the cumulative PnL series (percentage).
        decimal peak = 0m, maxDrawdown = 0m, running = 0m;
        foreach (var p in pnlList)
        {
            running += p;
            if (running > peak) peak = running;
            decimal dd = peak > 0 ? (peak - running) / peak * 100m : 0m;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }

        // Health score (5-factor, aligned with OptimizationWorker for consistency):
        // 0.25*WinRate + 0.20*min(1, PF/2) + 0.20*max(0, 1 - DD/20) + 0.15*min(1, max(0, Sharpe)/2) + 0.20*min(1, Trades/50)
        decimal healthScore = Optimization.OptimizationHealthScorer.ComputeHealthScore(winRate, profitFactor, maxDrawdown, sharpe, totalTrades);

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
        // A single Degrading evaluation is not sufficient to queue optimization —
        // it could be a transient market condition. Two consecutive Degrading evaluations
        // (across any time window) signal a persistent edge deterioration that warrants
        // parameter re-optimization.
        bool queuedOptimization = false;

        if (healthStatus == StrategyHealthStatus.Degrading)
        {
            // Read the most recently written snapshot for this strategy (before the one
            // just created, which hasn't been saved yet) to detect the consecutive pattern.
            var previousSnapshot = await readCtx.Set<StrategyPerformanceSnapshot>()
                .Where(s => s.StrategyId == strategy.Id && !s.IsDeleted)
                .OrderByDescending(s => s.EvaluatedAt)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            if (previousSnapshot?.HealthStatus == StrategyHealthStatus.Degrading)
            {
                // Check whether an optimization run is already in progress to avoid
                // queuing duplicate runs for the same strategy.
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
        // Only pause if still Active — a previously paused strategy does not need
        // re-pausing. ExecuteUpdateAsync issues a targeted SQL UPDATE without loading the entity.
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

        // Single SaveChangesAsync flushes the snapshot and any queued optimization run.
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "StrategyFeedbackWorker: strategy {Id} — {Status} (score={Score:F2}, " +
            "trades={Trades}, PnL={Pnl:F2}, WR={WR:P1}, PF={PF:F2}) over {Days}d.",
            strategy.Id, healthStatus, healthScore,
            totalTrades, totalPnL, winRate, profitFactor, windowDays);

        // ── Audit trail ───────────────────────────────────────────────────────
        // The audit entry includes whether optimization was queued so operators can
        // correlate evaluation outcomes with subsequent OptimizationRun records.
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

    /// <summary>
    /// Reads a typed value from the <see cref="EngineConfig"/> table, returning
    /// <paramref name="defaultValue"/> if the key is absent or the conversion fails.
    /// All reads use <c>AsNoTracking</c> to avoid unnecessary change-tracking overhead
    /// for configuration-only queries.
    /// </summary>
    /// <typeparam name="T">Target type (e.g. <see cref="int"/>, <see cref="bool"/>).</typeparam>
    /// <param name="ctx">Read DB context.</param>
    /// <param name="key">EngineConfig key to look up.</param>
    /// <param name="defaultValue">Fallback value when the key is missing or unreadable.</param>
    /// <param name="ct">Propagated cancellation token.</param>
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

        // Convert.ChangeType handles the most common primitive conversions
        // (string → int, string → bool, etc.). Any conversion failure falls back to default.
        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
