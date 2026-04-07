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
/// Monitors execution quality per strategy and auto-pauses any strategy whose rolling
/// average slippage or fill latency persistently exceeds configured thresholds.
///
/// <para>
/// <see cref="OrderExecutionWorker"/> writes a <see cref="ExecutionQualityLog"/> for
/// every filled order. Without a consumer that acts on those records, a strategy can
/// silently incur 3-5 pip slippage on every trade — eroding edge that backtests never
/// modelled. This worker closes that feedback loop.
/// </para>
///
/// Decision rules (evaluated over the last <c>ExecQuality:WindowFills</c> fills):
/// <list type="bullet">
///   <item><description>
///     Avg <see cref="ExecutionQualityLog.SlippagePips"/> &gt;
///     <c>ExecQuality:MaxAvgSlippagePips</c> → strategy paused.
///   </description></item>
///   <item><description>
///     Avg <see cref="ExecutionQualityLog.SubmitToFillMs"/> &gt;
///     <c>ExecQuality:MaxAvgLatencyMs</c> → strategy paused.
///   </description></item>
/// </list>
///
/// Config keys (EngineConfig, all hot-reloadable):
/// <list type="bullet">
///   <item><description><c>ExecQuality:PollIntervalMinutes</c> — default 15</description></item>
///   <item><description><c>ExecQuality:WindowFills</c> — default 50 (minimum fills required for statistical significance)</description></item>
///   <item><description><c>ExecQuality:MaxAvgSlippagePips</c> — default 3.0</description></item>
///   <item><description><c>ExecQuality:MaxAvgLatencyMs</c> — default 2000</description></item>
///   <item><description><c>ExecQuality:AutoPauseEnabled</c> — default true</description></item>
/// </list>
/// </summary>
public sealed class ExecutionQualityCircuitBreakerWorker : BackgroundService
{
    /// <summary>EngineConfig key: how often the circuit-breaker runs, in minutes (default 15).</summary>
    private const string CK_PollMins     = "ExecQuality:PollIntervalMinutes";

    /// <summary>
    /// EngineConfig key: the rolling window size — the number of most recent fills used
    /// to compute averages (default 20). A strategy must have at least this many fills
    /// before the circuit breaker will evaluate it, preventing false trips on small samples.
    /// </summary>
    private const string CK_WindowFills  = "ExecQuality:WindowFills";

    /// <summary>
    /// EngineConfig key: the maximum acceptable average slippage in pips (default 3.0).
    /// Exceeding this threshold over the last <c>WindowFills</c> fills trips the circuit
    /// breaker. For a EUR/USD 4-decimal pair, 3 pips = 0.0003 price units.
    /// </summary>
    private const string CK_MaxSlippage  = "ExecQuality:MaxAvgSlippagePips";

    /// <summary>
    /// EngineConfig key: the maximum acceptable average order fill latency in milliseconds
    /// (default 2000 ms). High latency is a leading indicator of broker connectivity issues
    /// or liquidity stress that will manifest as slippage if left unchecked.
    /// </summary>
    private const string CK_MaxLatencyMs = "ExecQuality:MaxAvgLatencyMs";

    /// <summary>
    /// EngineConfig key: when <c>true</c> (default), a breaching strategy is automatically
    /// paused. When <c>false</c>, the worker logs an audit warning but does not pause,
    /// effectively putting it into observation-only mode.
    /// </summary>
    private const string CK_AutoPause    = "ExecQuality:AutoPauseEnabled";
    private const string CK_HysteresisMargin = "ExecQuality:HysteresisMarginPct";

    private readonly IServiceScopeFactory                              _scopeFactory;
    private readonly ILogger<ExecutionQualityCircuitBreakerWorker>     _logger;

    /// <summary>
    /// Initialises the worker.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating per-cycle DI scopes.</param>
    /// <param name="logger">Structured logger.</param>
    public ExecutionQualityCircuitBreakerWorker(
        IServiceScopeFactory                             scopeFactory,
        ILogger<ExecutionQualityCircuitBreakerWorker>    logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. On each iteration:
    /// <list type="number">
    ///   <item><description>Reads all five EngineConfig thresholds (hot-reloadable).</description></item>
    ///   <item><description>
    ///     Calls <see cref="CheckAllStrategiesAsync"/> which iterates every strategy that has
    ///     at least one <see cref="ExecutionQualityLog"/> row and evaluates its rolling averages.
    ///   </description></item>
    ///   <item><description>Waits for the configured poll interval before the next cycle.</description></item>
    /// </list>
    /// </summary>
    /// <param name="stoppingToken">Signalled by the host on application shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExecutionQualityCircuitBreakerWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default interval used if the EngineConfig row has not been created yet.
            int pollMins = 15;

            try
            {
                // Fresh async scope per cycle keeps the EF change-tracker clean and
                // ensures config reads reflect the latest DB values each iteration.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var ctx      = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                // Read all thresholds from EngineConfig — hot-reloadable without restart.
                pollMins            = await GetConfigAsync<int>   (ctx, CK_PollMins,    15,   stoppingToken);
                int    windowFills  = await GetConfigAsync<int>   (ctx, CK_WindowFills, 50,   stoppingToken);
                double maxSlippage  = await GetConfigAsync<double>(ctx, CK_MaxSlippage, 3.0,  stoppingToken);
                double maxLatencyMs = await GetConfigAsync<double>(ctx, CK_MaxLatencyMs,2000, stoppingToken);
                bool   autoPause    = await GetConfigAsync<bool>  (ctx, CK_AutoPause,   true, stoppingToken);
                double hysteresisMargin = await GetConfigAsync<double>(ctx, CK_HysteresisMargin, 0.20, stoppingToken);

                await CheckAllStrategiesAsync(
                    ctx, writeCtx, mediator,
                    windowFills, maxSlippage, maxLatencyMs, autoPause, hysteresisMargin,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExecutionQualityCircuitBreakerWorker loop error");
            }

            await Task.Delay(TimeSpan.FromMinutes(pollMins), stoppingToken);
        }

        _logger.LogInformation("ExecutionQualityCircuitBreakerWorker stopping.");
    }

    // ── Per-strategy quality check ────────────────────────────────────────────

    /// <summary>
    /// Identifies all strategies that have at least one <see cref="ExecutionQualityLog"/>
    /// record and evaluates each one in turn. Strategies with no logs are skipped — the
    /// circuit breaker only acts on evidence, not absence of data.
    /// </summary>
    /// <param name="readCtx">Read DB context for loading quality log data.</param>
    /// <param name="writeCtx">Write DB context for pausing breaching strategies.</param>
    /// <param name="mediator">MediatR used for audit trail entries.</param>
    /// <param name="windowFills">Minimum number of fills required before a strategy is evaluated.</param>
    /// <param name="maxSlippage">Maximum acceptable average slippage in pips.</param>
    /// <param name="maxLatencyMs">Maximum acceptable average fill latency in milliseconds.</param>
    /// <param name="autoPause">Whether breaching strategies should be automatically paused.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task CheckAllStrategiesAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        int                                     windowFills,
        double                                  maxSlippage,
        double                                  maxLatencyMs,
        bool                                    autoPause,
        double                                  hysteresisMargin,
        CancellationToken                       ct)
    {
        // Only evaluate strategies that have at least one execution quality log.
        // This avoids unnecessary per-strategy queries for strategies that have never traded.
        var strategyIds = await readCtx.Set<ExecutionQualityLog>()
            .Where(l => l.StrategyId != null && !l.IsDeleted)
            .Select(l => l.StrategyId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (strategyIds.Count == 0) return;

        _logger.LogDebug(
            "ExecutionQualityCircuitBreakerWorker: checking {Count} strategy/strategies.",
            strategyIds.Count);

        foreach (var strategyId in strategyIds)
        {
            // Honour cancellation between strategies — avoids holding resources if
            // the host requests shutdown mid-cycle.
            ct.ThrowIfCancellationRequested();
            await CheckStrategyAsync(
                strategyId, readCtx, writeCtx, mediator,
                windowFills, maxSlippage, maxLatencyMs, autoPause, hysteresisMargin, ct);
        }
    }

    /// <summary>
    /// Evaluates execution quality for a single strategy. Computes rolling averages of
    /// slippage and fill latency over the most recent <paramref name="windowFills"/> fills,
    /// then compares them against the configured thresholds.
    ///
    /// <para>
    /// <b>Circuit-breaker logic:</b>
    /// <list type="bullet">
    ///   <item><description>
    ///     If the strategy has fewer than <paramref name="windowFills"/> fills, evaluation is
    ///     skipped to avoid acting on a statistically insignificant sample.
    ///   </description></item>
    ///   <item><description>
    ///     If either threshold is exceeded and <paramref name="autoPause"/> is <c>true</c>,
    ///     the strategy is paused via a single targeted SQL UPDATE (only if currently Active).
    ///   </description></item>
    ///   <item><description>
    ///     If <paramref name="autoPause"/> is <c>false</c>, a warning audit entry is written
    ///     but no state change is made — useful for observing circuit-breaker behaviour in
    ///     a new deployment before enabling enforcement.
    ///   </description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="strategyId">The ID of the strategy being evaluated.</param>
    /// <param name="readCtx">Read DB context.</param>
    /// <param name="writeCtx">Write DB context.</param>
    /// <param name="mediator">MediatR for audit trail entries.</param>
    /// <param name="windowFills">Rolling window size in number of fills.</param>
    /// <param name="maxSlippage">Slippage threshold in pips.</param>
    /// <param name="maxLatencyMs">Latency threshold in milliseconds.</param>
    /// <param name="autoPause">Whether to pause the strategy on breach.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    private async Task CheckStrategyAsync(
        long                                    strategyId,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        IMediator                               mediator,
        int                                     windowFills,
        double                                  maxSlippage,
        double                                  maxLatencyMs,
        bool                                    autoPause,
        double                                  hysteresisMargin,
        CancellationToken                       ct)
    {
        // Load the most recent N fills for this strategy, ordered newest-first.
        // AsNoTracking is used because we only read — no change tracking needed.
        var logs = await readCtx.Set<ExecutionQualityLog>()
            .Where(l => l.StrategyId == strategyId && !l.IsDeleted)
            .OrderByDescending(l => l.RecordedAt)
            .Take(windowFills)
            .AsNoTracking()
            .ToListAsync(ct);

        // Require a minimum sample size to avoid false positives from sparse data.
        // A strategy that just started trading will not be evaluated until enough
        // fill records have been written by OrderExecutionWorker.
        if (logs.Count < windowFills)
        {
            _logger.LogDebug(
                "ExecQualityCircuitBreaker: strategy {Id} has only {N}/{Min} fills — skipping.",
                strategyId, logs.Count, windowFills);
            return;
        }

        // Compute rolling averages — cast SlippagePips to double for consistent arithmetic.
        double avgSlippage  = (double)logs.Average(l => l.SlippagePips);
        double avgLatency   = logs.Average(l => l.SubmitToFillMs);

        bool slippageBreached = avgSlippage  > maxSlippage;
        bool latencyBreached  = avgLatency   > maxLatencyMs;

        // Hysteresis: if a strategy was previously paused by this circuit breaker and
        // its metrics have recovered below the recovery threshold (trip - margin),
        // auto-resume it. This prevents flapping where metrics oscillate around the
        // threshold boundary. E.g., with maxSlippage=3.0 and margin=0.20, the strategy
        // trips at >3.0 but only recovers when avgSlippage drops below 2.4 (3.0 * 0.80).
        double slippageRecovery = maxSlippage * (1.0 - hysteresisMargin);
        double latencyRecovery  = maxLatencyMs * (1.0 - hysteresisMargin);

        if (!slippageBreached && !latencyBreached)
        {
            // Check if this strategy was paused by THIS circuit breaker and has now recovered
            // below the hysteresis recovery threshold — auto-resume it. Only resume if the
            // pause reason was "ExecutionQuality" to avoid overriding pauses from other
            // sources (DrawdownRecovery, StrategyHealth, manual operator pause).
            bool fullyRecovered = avgSlippage <= slippageRecovery && avgLatency <= latencyRecovery;
            if (fullyRecovered && autoPause)
            {
                int resumed = await writeCtx.Set<Strategy>()
                    .Where(s => s.Id == strategyId && s.Status == StrategyStatus.Paused && !s.IsDeleted
                             && s.PauseReason == "ExecutionQuality")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, StrategyStatus.Active)
                        .SetProperty(x => x.PauseReason, (string?)null),
                        ct);

                if (resumed > 0)
                {
                    _logger.LogInformation(
                        "ExecQualityCircuitBreaker: strategy {Id} AUTO-RESUMED — metrics recovered below hysteresis threshold " +
                        "(avgSlippage={Slip:F2} <= {SlipRecovery:F2}, avgLatency={Lat:F0} <= {LatRecovery:F0})",
                        strategyId, avgSlippage, slippageRecovery, avgLatency, latencyRecovery);

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "Strategy",
                        EntityId     = strategyId,
                        DecisionType = "ExecQualityRecovery",
                        Outcome      = "Resumed",
                        Reason       = $"Metrics recovered: avgSlippage={avgSlippage:F2} pips <= {slippageRecovery:F2}, " +
                                       $"avgLatency={avgLatency:F0} ms <= {latencyRecovery:F0} " +
                                       $"(hysteresis margin={hysteresisMargin:P0})",
                        Source       = "ExecutionQualityCircuitBreakerWorker"
                    }, ct);
                }
            }

            _logger.LogDebug(
                "ExecQualityCircuitBreaker: strategy {Id} OK — avgSlippage={Slip:F2} pips, avgLatency={Lat:F0} ms.",
                strategyId, avgSlippage, avgLatency);
            return;
        }

        // Build a human-readable breach reason that will appear in the audit trail
        // and in the Warning log, making it clear exactly which threshold was crossed.
        var reasons = new List<string>();
        if (slippageBreached)
            reasons.Add($"avgSlippage={avgSlippage:F2} pips > threshold {maxSlippage:F1} pips");
        if (latencyBreached)
            reasons.Add($"avgLatency={avgLatency:F0} ms > threshold {maxLatencyMs:F0} ms");

        string reason = string.Join("; ", reasons) + $" (over last {windowFills} fills)";

        _logger.LogWarning(
            "ExecQualityCircuitBreaker: strategy {Id} breached execution quality thresholds — {Reason}",
            strategyId, reason);

        if (autoPause)
        {
            // Only pause if currently Active — idempotent if already paused by another worker.
            // ExecuteUpdateAsync issues a single targeted SQL UPDATE without loading the entity.
            int affected = await writeCtx.Set<Strategy>()
                .Where(s => s.Id == strategyId && s.Status == StrategyStatus.Active && !s.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, StrategyStatus.Paused)
                    .SetProperty(x => x.PauseReason, "ExecutionQuality"),
                    ct);

            if (affected > 0)
            {
                _logger.LogWarning(
                    "ExecQualityCircuitBreaker: strategy {Id} AUTO-PAUSED due to poor execution quality.",
                    strategyId);

                // Record the circuit-break decision with full context for post-mortem analysis.
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "Strategy",
                    EntityId     = strategyId,
                    DecisionType = "ExecQualityCircuitBreak",
                    Outcome      = "Paused",
                    Reason       = reason,
                    Source       = "ExecutionQualityCircuitBreakerWorker"
                }, ct);
            }
        }
        else
        {
            // Audit-only mode — log but don't pause. Useful during initial deployments
            // to observe the circuit breaker without affecting trading activity.
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = "Strategy",
                EntityId     = strategyId,
                DecisionType = "ExecQualityWarning",
                Outcome      = "Warning",
                Reason       = reason + " (AutoPause disabled)",
                Source       = "ExecutionQualityCircuitBreakerWorker"
            }, ct);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from the <see cref="EngineConfig"/> table by key, returning
    /// <paramref name="defaultValue"/> when the key is absent or the value cannot be
    /// converted to <typeparamref name="T"/>. All config reads use <c>AsNoTracking</c> to
    /// avoid polluting the EF change tracker with configuration rows.
    /// </summary>
    /// <typeparam name="T">Target primitive type (e.g. <see cref="int"/>, <see cref="double"/>, <see cref="bool"/>).</typeparam>
    /// <param name="ctx">Read DB context.</param>
    /// <param name="key">EngineConfig key.</param>
    /// <param name="defaultValue">Value returned when the key is missing or conversion fails.</param>
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

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
