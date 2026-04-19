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
/// Background service that periodically evaluates the real-time health of every active
/// strategy based on its most recent 50 executed signals, persists a
/// <see cref="StrategyPerformanceSnapshot"/>, and takes corrective action for critically
/// degraded strategies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polling interval:</b> 60 seconds (<see cref="PollingInterval"/>). The short interval
/// is intentional — this worker provides <em>real-time</em> health monitoring. A companion
/// worker, <see cref="StrategyFeedbackWorker"/>, provides a longer-horizon view over a
/// configurable day window (default 30 days).
/// </para>
///
/// <para>
/// <b>Evaluation window:</b> The last 50 executed <see cref="TradeSignal"/> records with an
/// associated filled <see cref="Order"/>. This rolling window ensures stale historical
/// performance does not mask recent degradation, while providing enough data points for
/// statistically meaningful metric calculations.
/// </para>
///
/// <para>
/// <b>Metrics computed per strategy:</b>
/// <list type="bullet">
///   <item><description>Win rate (winning trades / total trades)</description></item>
///   <item><description>Profit factor (gross profit / gross loss)</description></item>
///   <item><description>Simplified Sharpe ratio (mean PnL / stddev PnL, unannualised)</description></item>
///   <item><description>Max peak-to-trough drawdown on the cumulative PnL series</description></item>
///   <item><description>
///     <b>Health score</b>: composite score in [0, 1] with regime-aware weights.
///     Default (regime-neutral) formula:
///     <c>0.25×WinRate + 0.20×min(1, PF/2) + 0.20×max(0, 1−DD/20) + 0.15×min(1, max(0, Sharpe)/2) + 0.20×min(1, Trades/50)</c>.
///     Five factors: edge (win rate), reward-risk ratio, drawdown resilience, consistency (Sharpe), and sample size.
///     When the latest classified regime for the strategy's symbol/timeframe is known, the weight vector is
///     rebalanced to favour the factor that matters most in that regime (e.g. PF in Trending, DD in Crisis).
///     See <c>Optimization.OptimizationHealthScorer.RegimeWeights</c> for the full table.
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Health status thresholds:</b>
/// <list type="table">
///   <listheader><term>Score range</term><description>Status</description></listheader>
///   <item><term>≥ 0.6</term><description><see cref="StrategyHealthStatus.Healthy"/> — no action</description></item>
///   <item><term>0.3 – 0.6</term><description><see cref="StrategyHealthStatus.Degrading"/> — snapshot persisted, warning logged</description></item>
///   <item><term>&lt; 0.3</term><description><see cref="StrategyHealthStatus.Critical"/> — strategy auto-paused and optimization queued</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Weekly rebalance:</b> Once every 7 days (<see cref="RebalanceInterval"/>), the worker
/// dispatches <see cref="RebalanceEnsembleCommand"/> to recalculate strategy ensemble
/// weights from the latest Sharpe ratios. The rebalance timestamp is held in-process; a
/// restart resets it, causing an immediate rebalance on the next cycle.
/// </para>
///
/// <para>
/// <b>Pipeline position:</b> This worker feeds data into the <c>StrategyPerformanceSnapshots</c>
/// table, which is consumed by performance attribution queries, <see cref="StrategyFeedbackWorker"/>
/// (for consecutive-degrading detection), and the operations dashboard.
/// </para>
/// </remarks>
public class StrategyHealthWorker : BackgroundService
{
    private readonly ILogger<StrategyHealthWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const string CK_PollSecs = "StrategyHealth:PollIntervalSeconds";
    private const string CK_RebalanceDays = "StrategyHealth:RebalanceIntervalDays";
    private const string CK_HealthyThreshold = "StrategyHealth:HealthyThreshold";
    private const string CK_CriticalThreshold = "StrategyHealth:CriticalThreshold";
    private const string CK_EvaluationWindowSize = "StrategyHealth:EvaluationWindowSize";
    private const string CK_MinTradesForCritical = "StrategyHealth:MinTradesForCritical";
    private const int DefaultPollSeconds = 60;
    private const int DefaultRebalanceDays = 7;
    private const decimal DefaultHealthyThreshold = 0.6m;
    private const decimal DefaultCriticalThreshold = 0.3m;
    private const int DefaultEvaluationWindowSize = 50;
    // Default floor on the number of filled-order signals in the window before a
    // strategy can be classified Critical (and auto-paused). A single 10-loss
    // streak on a 5-sample window used to be enough; requiring ~20 filled orders
    // lets Degrading-band signals accumulate before any hard action is taken.
    private const int DefaultMinTradesForCritical = 20;

    /// <summary>
    /// In-process timestamp of the last successful ensemble rebalance. Initialised from
    /// the persisted <c>StrategyHealth:LastRebalancedAtUtc</c> config key on first use
    /// to avoid redundant rebalance churn on restart.
    /// </summary>
    private DateTime _lastRebalancedAt = DateTime.MinValue;
    private bool _lastRebalancedAtLoaded;

    /// <summary>Max backoff delay on consecutive failures (5 minutes).</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private int _consecutiveFailures;

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="scopeFactory">Factory for creating per-cycle DI scopes.</param>
    public StrategyHealthWorker(ILogger<StrategyHealthWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point for the hosted service. On each 60-second cycle:
    /// <list type="number">
    ///   <item><description>Evaluates all active strategies via <see cref="EvaluateAllActiveStrategiesAsync"/>.</description></item>
    ///   <item><description>Checks whether the weekly ensemble rebalance is due.</description></item>
    /// </list>
    /// </summary>
    /// <param name="stoppingToken">Signalled by the host on shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyHealthWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            try
            {
                await EvaluateAllActiveStrategiesAsync(stoppingToken);
                await TriggerRebalanceIfDueAsync(stoppingToken);
                _consecutiveFailures = 0;

                // Read configurable poll interval from EngineConfig
                using var configScope = _scopeFactory.CreateScope();
                var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                pollSecs = await GetConfigAsync<int>(configCtx, CK_PollSecs, DefaultPollSeconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "StrategyHealthWorker: polling error (consecutive failures: {Failures})",
                    _consecutiveFailures);
            }

            var pollingInterval = TimeSpan.FromSeconds(pollSecs);

            // Exponential backoff on consecutive failures: 60s, 120s, 240s, capped at 5min
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    pollingInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : pollingInterval;

            await Task.Delay(delay, stoppingToken);
        }

        _logger.LogInformation("StrategyHealthWorker stopped");
    }

    /// <summary>
    /// Triggers a full ensemble rebalance once per week by dispatching
    /// <see cref="RebalanceEnsembleCommand"/>. Weights are recalculated from the
    /// latest Sharpe ratios of all active strategies.
    ///
    /// <para>
    /// The rebalance is guarded by <see cref="_lastRebalancedAt"/> rather than a DB flag
    /// to keep the check cheap (no DB read required). This means a worker restart will
    /// trigger an immediate rebalance, which is acceptable because rebalancing is idempotent.
    /// </para>
    /// </summary>
    private async Task TriggerRebalanceIfDueAsync(CancellationToken ct)
    {
        // Read configurable rebalance interval from EngineConfig
        int rebalanceDays = DefaultRebalanceDays;
        try
        {
            using var configScope = _scopeFactory.CreateScope();
            var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            rebalanceDays = await GetConfigAsync<int>(configCtx, CK_RebalanceDays, DefaultRebalanceDays, ct);
        }
        catch { /* use default */ }

        // Hydrate from persisted config on first access to avoid restart-triggered rebalance churn
        if (!_lastRebalancedAtLoaded)
        {
            _lastRebalancedAtLoaded = true;
            try
            {
                using var loadScope = _scopeFactory.CreateScope();
                var loadCtx = loadScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var persisted = await GetConfigAsync<string>(loadCtx, "StrategyHealth:LastRebalancedAtUtc", "", ct);
                if (DateTime.TryParse(persisted, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    _lastRebalancedAt = parsed;
            }
            catch { /* use in-memory default */ }
        }

        if (DateTime.UtcNow - _lastRebalancedAt < TimeSpan.FromDays(rebalanceDays))
            return;

        try
        {
            using var scope  = _scopeFactory.CreateScope();
            var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
            var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();

            _logger.LogInformation("StrategyHealthWorker: triggering scheduled weekly ensemble rebalance");
            await mediator.Send(new RebalanceEnsembleCommand(), ct);
            _lastRebalancedAt = DateTime.UtcNow;

            // Persist so the next restart doesn't immediately retrigger
            await UpsertConfigAsync(writeCtx, "StrategyHealth:LastRebalancedAtUtc",
                _lastRebalancedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);

            _logger.LogInformation("StrategyHealthWorker: weekly ensemble rebalance completed");
        }
        catch (Exception ex)
        {
            // Don't let a failed rebalance crash the polling loop —
            // next cycle will retry via the _lastRebalancedAt check.
            _logger.LogError(ex, "StrategyHealthWorker: weekly ensemble rebalance failed");
        }
    }

    /// <summary>
    /// Iterates all strategies currently in <see cref="StrategyStatus.Active"/> state
    /// and evaluates each one independently. Per-strategy evaluation errors are isolated
    /// so a bad strategy record does not block evaluation of others.
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task EvaluateAllActiveStrategiesAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        // Optional resolve — when the provider isn't registered (older test fixtures) we
        // fall through to the hardcoded RegimeWeights defaults inside OptimizationHealthScorer.
        var weightsProvider = scope.ServiceProvider.GetService<Optimization.RegimeHealthWeightsProvider>();
        IReadOnlyDictionary<LascodiaTradingEngine.Domain.Enums.MarketRegime,
            Optimization.OptimizationHealthScorer.HealthWeights>? regimeWeightOverrides = null;
        if (weightsProvider is not null)
        {
            try
            {
                regimeWeightOverrides = await weightsProvider.GetAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StrategyHealthWorker: regime weight overrides load failed; using defaults");
            }
        }

        // Read configurable health band thresholds from EngineConfig
        decimal healthyThreshold = await GetConfigAsync<decimal>(readContext, CK_HealthyThreshold, DefaultHealthyThreshold, ct);
        decimal criticalThreshold = await GetConfigAsync<decimal>(readContext, CK_CriticalThreshold, DefaultCriticalThreshold, ct);
        // Sanity: ensure healthy > critical, both in [0, 1]
        healthyThreshold = Math.Clamp(healthyThreshold, 0.01m, 1.0m);
        criticalThreshold = Math.Clamp(criticalThreshold, 0.01m, healthyThreshold);

        int evaluationWindowSize = Math.Max(5,
            await GetConfigAsync<int>(readContext, CK_EvaluationWindowSize, DefaultEvaluationWindowSize, ct));
        int minTradesForCritical = Math.Max(0,
            await GetConfigAsync<int>(readContext, CK_MinTradesForCritical, DefaultMinTradesForCritical, ct));

        var strategies = await readContext.GetDbContext()
            .Set<Strategy>()
            .Where(x => x.Status == StrategyStatus.Active && !x.IsDeleted)
            .ToListAsync(ct);

        int evaluated = 0, failed = 0;

        foreach (var strategy in strategies)
        {
            try
            {
                await EvaluateStrategyAsync(strategy, writeContext, readContext, mediator, ct,
                    healthyThreshold, criticalThreshold, evaluationWindowSize, minTradesForCritical,
                    regimeWeightOverrides);
                evaluated++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "StrategyHealthWorker: evaluation failed for strategy {StrategyId}", strategy.Id);
            }
        }

        if (strategies.Count > 0)
        {
            _logger.LogDebug(
                "StrategyHealthWorker cycle: {Total} strategies, {Evaluated} evaluated, {Failed} failed",
                strategies.Count, evaluated, failed);
        }
    }

    /// <summary>
    /// Evaluates the health of a single strategy using its last 50 executed signals.
    ///
    /// <para>
    /// <b>PnL estimation:</b> PnL per signal is computed as
    /// <c>(FilledPrice − EntryPrice) × SuggestedLotSize × 100,000</c> for Buy signals,
    /// inverted for Sell signals. The multiplier of 100,000 converts price difference
    /// (in price units) to an approximate monetary value assuming one standard lot.
    /// This is a simplified model; actual P&amp;L may differ due to swap costs and spread.
    /// </para>
    ///
    /// <para>
    /// <b>Health score formula (5-factor, regime-aware):</b>
    /// Default (regime-neutral) weights: WR 25 %, PF 20 %, DD 20 %, Sharpe 15 %, N 20 %. Normalisers:
    /// PF is divided by 2 and capped at 1; DD is mapped to <c>max(0, 1 − DD/20)</c> so 20 % drawdown
    /// contributes zero; Sharpe is clamped to [0, 2] and divided by 2; sample size reaches full
    /// contribution at 50 trades. The worker looks up the latest classified regime for the strategy's
    /// symbol/timeframe and delegates to
    /// <c>Optimization.OptimizationHealthScorer.ComputeHealthScore(..., regime)</c>, which applies
    /// regime-specific weights (see <c>RegimeWeights</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Critical response:</b> A Critical strategy is paused via EF entity tracking
    /// (not a bulk update) so that the same <c>SaveChangesAsync</c> call that persists the
    /// snapshot also updates the strategy status atomically within the same DB transaction.
    /// </para>
    /// </summary>
    /// <param name="strategy">The active strategy to evaluate.</param>
    /// <param name="writeContext">Write DB context for persisting the snapshot and pausing.</param>
    /// <param name="readContext">Read DB context for loading signals and orders.</param>
    /// <param name="mediator">MediatR for audit trail entries.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task EvaluateStrategyAsync(
        Strategy strategy,
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext readContext,
        IMediator mediator,
        CancellationToken ct,
        decimal healthyThreshold = 0.6m,
        decimal criticalThreshold = 0.3m,
        int evaluationWindowSize = DefaultEvaluationWindowSize,
        int minTradesForCritical = DefaultMinTradesForCritical,
        IReadOnlyDictionary<LascodiaTradingEngine.Domain.Enums.MarketRegime, Optimization.OptimizationHealthScorer.HealthWeights>? regimeWeightOverrides = null)
    {
        // Load the last N executed signals for this strategy (configurable via
        // StrategyHealth:EvaluationWindowSize, default 50). Only signals with an
        // OrderId are useful — they represent signals that resulted in actual
        // market executions.
        var signals = await readContext.GetDbContext()
            .Set<TradeSignal>()
            .Where(x => x.StrategyId == strategy.Id
                     && x.Status == TradeSignalStatus.Executed
                     && x.OrderId != null
                     && !x.IsDeleted)
            .OrderByDescending(x => x.GeneratedAt)
            .Take(evaluationWindowSize)
            .ToListAsync(ct);

        if (signals.Count == 0)
        {
            _logger.LogDebug(
                "StrategyHealthWorker: no executed signals for strategy {StrategyId}, skipping", strategy.Id);
            return;
        }

        // Load the associated filled orders to compute PnL per signal.
        // Using a dictionary keyed by OrderId avoids N+1 queries.
        var orderIds = signals
            .Where(s => s.OrderId.HasValue)
            .Select(s => s.OrderId!.Value)
            .Distinct()
            .ToList();

        var orders = await readContext.GetDbContext()
            .Set<Order>()
            .Where(x => orderIds.Contains(x.Id) && x.Status == OrderStatus.Filled && !x.IsDeleted)
            .ToDictionaryAsync(x => x.Id, ct);

        // Pre-load contract sizes for all symbols involved
        var symbolsInvolved = signals.Select(s => s.Symbol).Distinct().ToList();
        var contractSizes = await readContext.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .Where(x => symbolsInvolved.Contains(x.Symbol) && !x.IsDeleted)
            .ToDictionaryAsync(x => x.Symbol, x => x.ContractSize, StringComparer.OrdinalIgnoreCase, ct);

        var pnlList = new List<decimal>();

        foreach (var signal in signals)
        {
            if (!signal.OrderId.HasValue || !orders.TryGetValue(signal.OrderId.Value, out var order))
                continue;

            if (order.FilledPrice is null) continue;

            decimal contractSize = contractSizes.GetValueOrDefault(signal.Symbol, 100_000m);
            decimal pnl;
            if (signal.Direction == TradeDirection.Buy)
                pnl = (order.FilledPrice.Value - signal.EntryPrice) * signal.SuggestedLotSize * contractSize;
            else
                pnl = (signal.EntryPrice - order.FilledPrice.Value) * signal.SuggestedLotSize * contractSize;

            pnlList.Add(pnl);
        }

        if (pnlList.Count == 0)
        {
            _logger.LogDebug(
                "StrategyHealthWorker: no filled orders with PnL data for strategy {StrategyId}, skipping", strategy.Id);
            return;
        }

        // ── Metric calculations ───────────────────────────────────────────────

        int totalTrades   = pnlList.Count;
        int winningTrades = pnlList.Count(p => p > 0);
        int losingTrades  = pnlList.Count(p => p <= 0);

        decimal winRate     = totalTrades > 0 ? (decimal)winningTrades / totalTrades : 0m;
        decimal grossProfit = pnlList.Where(p => p > 0).Sum();
        decimal grossLoss   = Math.Abs(pnlList.Where(p => p < 0).Sum());

        // Profit factor: grossProfit / grossLoss. Guard against zero grossLoss (all winning).
        // Cap at 2.0 for a strategy with no losses to avoid inflating the health score.
        decimal profitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 2m : 0m;

        // Simple (unannualised) Sharpe estimate: mean PnL / standard deviation of PnL.
        // NOTE: This is NOT annualized (no sqrt(252) multiplier) because it operates on
        // a rolling trade window rather than daily returns. For annualized Sharpe ratios
        // (using the sqrt(252) convention), see PerformanceAttributionWorker which computes
        // SharpeRatio7d/30d from daily return series.
        // Stddev is clamped to 1 if zero (i.e. all trades had identical PnL) to prevent
        // division by zero and avoid a spuriously high Sharpe.
        decimal meanPnl  = pnlList.Average();
        decimal variance = pnlList.Select(p => (p - meanPnl) * (p - meanPnl)).Average();
        decimal stddev   = variance > 0 ? (decimal)Math.Sqrt((double)variance) : 1m;
        decimal sharpe   = stddev > 0 ? meanPnl / stddev : 0m;

        // Peak-to-trough max drawdown on the cumulative PnL series (percentage).
        // Iterates trades in the same chronological order they were executed.
        decimal peak     = 0m;
        decimal maxDrawdown = 0m;
        decimal running  = 0m;
        foreach (var p in pnlList)
        {
            running += p;
            if (running > peak) peak = running;
            // Only calculate drawdown when there is a positive peak to measure from.
            decimal drawdown = peak > 0 ? (peak - running) / peak * 100m : 0m;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        decimal totalPnL = pnlList.Sum();

        // Pull the latest classified regime for this symbol+timeframe so the
        // health score can be weighted to match the regime. The same value is
        // stored on the snapshot for the regime-stratified promotion gate.
        // Falls through to null if no regime snapshot has been captured yet —
        // downstream consumers treat null as "unknown" and scoring falls back
        // to the regime-neutral default weights.
        var latestRegime = await writeContext.GetDbContext()
            .Set<MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(r => r.Symbol == strategy.Symbol
                     && r.Timeframe == strategy.Timeframe
                     && !r.IsDeleted)
            .OrderByDescending(r => r.DetectedAt)
            .Select(r => (LascodiaTradingEngine.Domain.Enums.MarketRegime?)r.Regime)
            .FirstOrDefaultAsync(ct);

        // ── Health score (5-factor, regime-aware, config-tunable) ─────────────
        // Default weights: 0.25*WR + 0.20*min(1, PF/2) + 0.20*max(0, 1 - DD/20) + 0.15*min(1, max(0, Sharpe)/2) + 0.20*min(1, N/50).
        // With a regime, the weight vector rebalances (e.g. Trending weights PF higher, Crisis weights DD higher).
        // See OptimizationHealthScorer.RegimeWeights for the full table. The weightOverrides map is
        // populated once per cycle by RegimeHealthWeightsProvider from EngineConfig
        // (StrategyHealth:RegimeWeights:<Regime> JSON), so operators can rebalance live without a
        // redeploy. Bootstrap/permutation analyses continue to use the regime-neutral overload because
        // they test trade-stream statistics, not live suitability.
        decimal healthScore = Optimization.OptimizationHealthScorer.ComputeHealthScore(
            winRate, profitFactor, maxDrawdown, sharpe, totalTrades, latestRegime, regimeWeightOverrides);

        // Classify into three bands used to drive automated responses.
        // Sample-size gate: a single unlucky streak on a tiny window (e.g. 10 losses
        // out of 15) should not be enough to classify Critical and auto-pause. If
        // the window hasn't accumulated MinTradesForCritical filled orders yet, we
        // cap the classification at Degrading so more evidence accrues before any
        // hard action (pause + optimization queue) is taken. Set MinTradesForCritical
        // to 0 to restore the old behaviour.
        StrategyHealthStatus healthStatus = healthScore >= healthyThreshold ? StrategyHealthStatus.Healthy
            : healthScore >= criticalThreshold ? StrategyHealthStatus.Degrading
            : StrategyHealthStatus.Critical;
        if (healthStatus == StrategyHealthStatus.Critical
            && minTradesForCritical > 0
            && totalTrades < minTradesForCritical)
        {
            _logger.LogInformation(
                "StrategyHealthWorker: strategy {StrategyId} scored {Score:F2} but only {Trades} filled orders in window " +
                "(min {MinTrades} for Critical) — downgrading to Degrading until more samples accumulate",
                strategy.Id, healthScore, totalTrades, minTradesForCritical);
            healthStatus = StrategyHealthStatus.Degrading;
        }

        // Persist the snapshot for dashboard queries and StrategyFeedbackWorker
        // consecutive-degrading detection.
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
            MarketRegime   = latestRegime,
            EvaluatedAt    = DateTime.UtcNow
        };

        await writeContext.GetDbContext()
            .Set<StrategyPerformanceSnapshot>()
            .AddAsync(snapshot, ct);

        // ── Auto-pause critical strategies and trigger optimization ────────────
        OptimizationRun? optimizationRun = null;

        if (healthStatus == StrategyHealthStatus.Critical)
        {
            _logger.LogWarning(
                "StrategyHealthWorker: strategy {StrategyId} is Critical (HealthScore={Score:F2}), auto-pausing and triggering optimization",
                strategy.Id, healthScore);

            // Load via the write context so EF tracks the status change.
            // This approach ensures the pause and snapshot are saved in the same call.
            var liveStrategy = await writeContext.GetDbContext()
                .Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == strategy.Id && !x.IsDeleted, ct);

            if (liveStrategy is not null && liveStrategy.Status == StrategyStatus.Active)
            {
                liveStrategy.Status = StrategyStatus.Paused;
                liveStrategy.PauseReason = "StrategyHealth";
            }

            // Queue an optimization run — the OptimizationWorker will pick this up
            // and attempt to improve the strategy's parameters.
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

        // Single SaveChangesAsync persists: snapshot + strategy pause + optimization run.
        await writeContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "StrategyHealthWorker: strategy {StrategyId} evaluated — HealthStatus={Status}, Score={Score:F2}",
            strategy.Id, healthStatus, healthScore);

        // ── Audit trail ──────────────────────────────────────────────────────────
        // Record the full evaluation context so post-mortems can explain why a strategy
        // was paused or deemed healthy at any point in time.

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
                Reason       = $"HealthScore={healthScore:F2} fell below critical threshold ({criticalThreshold:F2}); strategy auto-paused",
                Source       = "StrategyHealthWorker"
            }, ct);

            // Audit the optimization run only if the ID was assigned after SaveChangesAsync.
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

    private static async Task<T> GetConfigAsync<T>(
        IReadApplicationDbContext readContext, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await readContext.GetDbContext()
            .Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    private static Task UpsertConfigAsync(
        IWriteApplicationDbContext writeCtx, string key, string value, CancellationToken ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx.GetDbContext(), key, value, ct: ct);
}
