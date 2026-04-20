using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using LascodiaTradingEngine.Application.Services;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Core signal-generation worker — the "brain" of the trading engine.
///
/// This worker is entirely event-driven: it subscribes to <see cref="PriceUpdatedIntegrationEvent"/>
/// on the event bus (published by <see cref="ExpertAdvisor.Commands.ReceiveTickBatch.ReceiveTickBatchCommandHandler"/>)
/// and evaluates every active strategy whose symbol matches the updated price.
///
/// <b>Signal generation pipeline (per price tick):</b>
/// <list type="number">
///   <item><b>Global filters</b> (applied once per tick, before strategy loop):
///     <list type="bullet">
///       <item>Stale tick rejection — drops events older than <c>MaxTickAgeSeconds</c> from the event bus backlog.</item>
///       <item>EA health check — verifies at least one active EA instance with a fresh heartbeat owns the symbol.</item>
///       <item>Session filter — blocks trading outside allowed sessions (e.g. Asian, London, NY).</item>
///       <item>News blackout — blocks trading around high-impact economic events.</item>
///       <item>Regime coherence — suppresses all signals when H1/H4/D1 regimes disagree on direction.</item>
///     </list>
///   </item>
///   <item><b>Pre-fetch phase</b> (batched DB queries to avoid N+1 inside the parallel loop):
///     <list type="bullet">
///       <item>Strategy health snapshots — identifies Critical-status strategies to skip.</item>
///       <item>Market regime snapshots — pre-fetches per-timeframe regime for the symbol.</item>
///       <item>Backtest qualification — loads most recent backtest per strategy and checks quality thresholds.</item>
///       <item>Sharpe ratio cache — pre-fetches latest Sharpe for conflict resolution scoring.</item>
///     </list>
///   </item>
///   <item><b>Parallel strategy evaluation</b> (one iteration per active strategy matching the symbol):
///     <list type="bullet">
///       <item>Strategy health filter — skips Critical strategies.</item>
///       <item>Backtest qualification gate — blocks untested or low-quality strategies.</item>
///       <item>Circuit breaker — stops evaluating strategies with repeated failures (half-open recovery).</item>
///       <item>Signal cooldown — prevents rapid-fire signals from the same strategy.</item>
///       <item>Distributed lock — prevents concurrent evaluation of the same strategy from overlapping ticks.</item>
///       <item>Strategy evaluator — runs the strategy-specific logic (MA crossover, RSI reversion, breakout scalper).</item>
///       <item>Confidence modifiers — scales confidence by session quality and MTF confirmation strength.</item>
///       <item>Market regime filter — blocks signals in unfavourable regimes.</item>
///       <item>Multi-timeframe confirmation — requires higher-timeframe alignment.</item>
///       <item>Portfolio correlation check — limits correlated positions.</item>
///       <item>Hawkes burst filter — detects and suppresses signal clustering episodes.</item>
///       <item>ML scoring — runs the active ML model for directional prediction, abstention gating, and suppression.</item>
///     </list>
///   </item>
///   <item><b>Post-loop conflict resolution</b>:
///     <list type="bullet">
///       <item>Collects all candidate signals from the parallel loop into a <see cref="ConcurrentBag{T}"/>.</item>
///       <item><see cref="ISignalConflictResolver"/> resolves opposing-direction conflicts and deduplicates same-direction
///             signals using a priority score (Sharpe, ML confidence, capacity).</item>
///       <item>Winning signals are persisted via <see cref="CreateTradeSignalCommand"/> and published as
///             <see cref="TradeSignalCreatedIntegrationEvent"/> for downstream consumption by
///             <see cref="SignalOrderBridgeWorker"/>.</item>
///     </list>
///   </item>
/// </list>
///
/// <b>Background timer:</b> A 5-minute sweep expires pending/approved signals that have exceeded
/// their TTL, and purges stale in-memory cooldown/circuit-breaker entries for deactivated strategies.
///
/// <b>Thread safety:</b>
/// <list type="bullet">
///   <item>A <see cref="IDistributedLock"/> per strategy prevents overlapping evaluations when price
///         events arrive faster than evaluation completes.</item>
///   <item>In-memory state (<c>_lastSignalTime</c>, <c>_consecutiveFailures</c>, <c>_circuitOpenedAt</c>)
///         uses <see cref="ConcurrentDictionary{TKey,TValue}"/> for lock-free concurrent access.</item>
///   <item>The expiry sweep is guarded by a <see cref="SemaphoreSlim"/> to prevent concurrent executions.</item>
/// </list>
///
/// <b>DI lifetime:</b> Singleton (registered as both <see cref="IHostedService"/> and
/// <see cref="IIntegrationEventHandler{T}"/> forwarding to the same instance). Creates scoped
/// <see cref="IServiceScope"/> instances for each price event and each parallel strategy iteration
/// to isolate scoped services (DbContext, ML scorer, filters).
/// </summary>
public partial class StrategyWorker :
    BackgroundService,
    IIntegrationEventHandler<PriceUpdatedIntegrationEvent>,
    IIntegrationEventHandler<BacktestCompletedIntegrationEvent>,
    IIntegrationEventHandler<StrategyActivatedIntegrationEvent>
{
    private readonly ILogger<StrategyWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly IEnumerable<IStrategyEvaluator> _evaluators;
    private readonly IDistributedLock _distributedLock;
    private readonly StrategyEvaluatorOptions _options;
    private readonly TradingMetrics _metrics;
    private readonly ISignalConflictResolver _signalConflictResolver;
    private readonly RegimeCoherenceChecker _regimeCoherenceChecker;
    private readonly DrawdownRecoveryModeProvider _drawdownRecoveryModeProvider;
    private readonly PortfolioCorrelationSizer _portfolioCorrelationSizer;
    private readonly StrategyMetricsCache _strategyMetricsCache;
    private readonly IMarketHoursCalendar _marketHoursCalendar;
    private readonly StrategyRegimeParamsCache _regimeParamsCache;
    private readonly ISignalRejectionAuditor _rejectionAuditor;

    /// <summary>Tracks the last signal creation time per strategy to enforce cooldown.</summary>
    private readonly ConcurrentDictionary<long, DateTime> _lastSignalTime = new();

    /// <summary>Tracks consecutive evaluation failures per strategy for circuit-breaking.</summary>
    private readonly ConcurrentDictionary<long, int> _consecutiveFailures = new();

    /// <summary>Tracks when the circuit breaker opened per strategy for half-open recovery.</summary>
    private readonly ConcurrentDictionary<long, DateTime> _circuitOpenedAt = new();

    /// <summary>Tracks how many half-open probe attempts have failed per strategy. When this
    /// exceeds <c>MaxHalfOpenProbeFailures</c>, the strategy is permanently circuit-broken.</summary>
    private readonly ConcurrentDictionary<long, int> _halfOpenProbeFailures = new();
    private const int MaxHalfOpenProbeFailures = 5;

    /// <summary>
    /// Strategy IDs whose runtime state (lastSignalAt / consecutiveFailures /
    /// circuitOpenedAt) has changed since the last DB write-back. Populated by
    /// each write to <see cref="_lastSignalTime"/> / <see cref="_consecutiveFailures"/>
    /// / <see cref="_circuitOpenedAt"/>. Drained by the fast persistence loop so
    /// restarts only lose seconds of state rather than the previous 5-minute
    /// sweep cadence.
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _dirtyStrategyIds = new();

    /// <summary>Timer for fast, dirty-flag-driven runtime-state persistence.</summary>
    private Timer? _stateFlushTimer;

    /// <summary>Serialises concurrent state flushes.</summary>
    private readonly SemaphoreSlim _stateFlushLock = new(1, 1);

    /// <summary>Prevents concurrent expiry sweep executions from the timer.</summary>
    private readonly SemaphoreSlim _expirySweepLock = new(1, 1);

    /// <summary>
    /// Bounded channel for tick backpressure. When ticks arrive faster than evaluation
    /// completes, the newest tick per symbol replaces the oldest (DropOldest policy).
    /// This makes tick loss explicit and bounded rather than implicit and unbounded.
    /// </summary>
    private readonly Channel<PriceUpdatedIntegrationEvent> _tickChannel =
        Channel.CreateBounded<PriceUpdatedIntegrationEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>Timer that periodically expires stale pending trade signals.</summary>
    private Timer? _expirySweepTimer;

    /// <summary>
    /// Captured from <see cref="ExecuteAsync"/> so that event-driven <see cref="Handle"/>
    /// calls can propagate host-shutdown cancellation to downstream services like
    /// <see cref="IMLSignalScorer.ScoreAsync"/>.
    /// </summary>
    private CancellationToken _stoppingToken = CancellationToken.None;

    /// <summary>
    /// All dependencies are injected as singletons (or singleton-safe factories like
    /// <see cref="IServiceScopeFactory"/>) because this worker is itself a singleton.
    /// Scoped services (DbContext, ML scorer, etc.) are resolved per-event via scope factories.
    /// </summary>
    public StrategyWorker(
        ILogger<StrategyWorker> logger,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IEnumerable<IStrategyEvaluator> evaluators,
        IDistributedLock distributedLock,
        StrategyEvaluatorOptions options,
        TradingMetrics metrics,
        ISignalConflictResolver signalConflictResolver,
        RegimeCoherenceChecker regimeCoherenceChecker,
        DrawdownRecoveryModeProvider drawdownRecoveryModeProvider,
        PortfolioCorrelationSizer portfolioCorrelationSizer,
        StrategyMetricsCache strategyMetricsCache,
        IMarketHoursCalendar marketHoursCalendar,
        StrategyRegimeParamsCache regimeParamsCache,
        ISignalRejectionAuditor rejectionAuditor)
    {
        _logger                       = logger;
        _scopeFactory                 = scopeFactory;    // Creates per-event/per-strategy DI scopes
        _eventBus                     = eventBus;         // Event bus for subscribing to PriceUpdatedIntegrationEvent
        _evaluators                   = evaluators;       // All registered IStrategyEvaluator implementations (one per StrategyType)
        _distributedLock              = distributedLock;  // Prevents concurrent evaluation of the same strategy
        _options                      = options;          // Configurable thresholds (cooldowns, circuit breaker, regime blocking, etc.)
        _metrics                      = metrics;          // OpenTelemetry metrics for observability
        _signalConflictResolver       = signalConflictResolver;  // Resolves opposing/duplicate signals from competing strategies
        _regimeCoherenceChecker       = regimeCoherenceChecker;  // Cross-timeframe regime alignment scoring
        _drawdownRecoveryModeProvider = drawdownRecoveryModeProvider;  // Cached access to DrawdownRecovery config
        _portfolioCorrelationSizer    = portfolioCorrelationSizer;  // Correlation-aware lot-size multiplier
        _strategyMetricsCache         = strategyMetricsCache;       // Process-lifetime cache for per-strategy Sharpe + health
        _marketHoursCalendar          = marketHoursCalendar;        // Market-closed detection for adaptive signal TTL
        _regimeParamsCache            = regimeParamsCache;          // Per-(strategy,regime) parameter cache, TTL-driven
        _rejectionAuditor             = rejectionAuditor;           // Structured rejection audit stream for operator dashboards
    }

    /// <summary>
    /// Subscribes to price-update events on the event bus, starts a bounded-channel consumer
    /// loop for tick backpressure, and launches the signal expiry sweep timer.
    ///
    /// <b>Backpressure design:</b> The event bus invokes <see cref="Handle"/> on every tick,
    /// which writes the event into a <see cref="BoundedChannel{T}"/> (capacity 256, DropOldest).
    /// This loop reads from the channel and runs the evaluation pipeline. When ticks arrive
    /// faster than evaluation completes, the oldest queued ticks are dropped — the latest
    /// price is always most relevant. This makes tick loss explicit and bounded.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        // Wait for the event bus to finish initializing before subscribing.
        // Without this delay, Subscribe may fire before the bus has established its
        // broker connection, causing silent message loss on startup.
        _logger.LogInformation("StrategyWorker: waiting for event bus readiness before subscribing...");
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        // Hydrate per-strategy cooldown / circuit-breaker state from DB BEFORE subscribing.
        // Subscription is gated on successful hydration: without the in-memory cache warmed,
        // every active strategy would appear to have a zero cooldown, producing a thundering
        // herd of signals on the first tick after restart. Persisted fields live on Strategy
        // (LastSignalAt, ConsecutiveEvaluationFailures, CircuitOpenedAt).
        //
        // Retry policy: up to 5 attempts with exponential backoff (1s, 2s, 4s, 8s, 16s). If
        // all attempts fail we refuse to subscribe — this is fail-closed: better to emit zero
        // signals and alert than to emit a cooldown-less burst. On terminal failure the worker
        // returns, the host will log the unhealthy state, and the operator must investigate.
        const int maxHydrationAttempts = 5;
        bool hydrated = false;
        for (int attempt = 1; attempt <= maxHydrationAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var activeStrategies = await readCtx.GetDbContext()
                    .Set<Domain.Entities.Strategy>()
                    .AsNoTracking()
                    .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
                    .Select(s => new { s.Id, s.LastSignalAt, s.ConsecutiveEvaluationFailures, s.CircuitOpenedAt })
                    .ToListAsync(stoppingToken);

                int hydratedCooldowns = 0, hydratedCircuits = 0;
                foreach (var s in activeStrategies)
                {
                    if (s.LastSignalAt is { } lastAt)
                    {
                        _lastSignalTime[s.Id] = lastAt;
                        hydratedCooldowns++;
                    }
                    if (s.ConsecutiveEvaluationFailures > 0)
                        _consecutiveFailures[s.Id] = s.ConsecutiveEvaluationFailures;
                    if (s.CircuitOpenedAt is { } openedAt)
                    {
                        _circuitOpenedAt[s.Id] = openedAt;
                        hydratedCircuits++;
                    }
                }
                _logger.LogInformation(
                    "StrategyWorker: hydrated runtime state for {Count} strategies (cooldowns={Cooldowns}, open circuits={Circuits}) on attempt {Attempt}/{Max}",
                    activeStrategies.Count, hydratedCooldowns, hydratedCircuits, attempt, maxHydrationAttempts);
                hydrated = true;
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex,
                    "StrategyWorker: cooldown hydration attempt {Attempt}/{Max} failed — retrying in {Backoff}s",
                    attempt, maxHydrationAttempts, backoff.TotalSeconds);
                try { await Task.Delay(backoff, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }

        if (!hydrated)
        {
            _logger.LogCritical(
                "StrategyWorker: cooldown hydration failed after {Max} attempts — refusing to subscribe to price events. No signals will be emitted until the next worker start with a reachable database.",
                maxHydrationAttempts);
            _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("operation", "hydration_failed"));
            return;
        }

        // Subscribe to the event bus so Handle() is invoked on every price tick
        _eventBus.Subscribe<PriceUpdatedIntegrationEvent, StrategyWorker>();

        // Additional subscriptions drive event-invalidation of the per-strategy
        // metrics cache so conflict resolution always uses fresh Sharpe / health
        // after a backtest completes or a strategy is activated.
        _eventBus.Subscribe<BacktestCompletedIntegrationEvent, StrategyWorker>();
        _eventBus.Subscribe<StrategyActivatedIntegrationEvent, StrategyWorker>();

        // Start the periodic sweep that expires stale pending trade signals
        _expirySweepTimer = new Timer(RunExpirySweep, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        // Fast-flush dirty runtime state (lastSignalAt / consecutiveFailures / circuitOpenedAt)
        // so cooldown + circuit-breaker state survives restart with sub-minute staleness.
        _stateFlushTimer = new Timer(RunStateFlush, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        // Clean up subscriptions and timer when the host signals shutdown
        stoppingToken.Register(() =>
        {
            _expirySweepTimer?.Dispose();
            _stateFlushTimer?.Dispose();
            _tickChannel.Writer.TryComplete();
            _eventBus.Unsubscribe<PriceUpdatedIntegrationEvent, StrategyWorker>();
            _eventBus.Unsubscribe<BacktestCompletedIntegrationEvent, StrategyWorker>();
            _eventBus.Unsubscribe<StrategyActivatedIntegrationEvent, StrategyWorker>();
        });

        // Consume ticks from the bounded channel — this is the main evaluation loop
        await foreach (var @event in _tickChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessPriceUpdateAsync(@event);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StrategyWorker: unhandled error processing tick for {Symbol}", @event.Symbol);
                _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("operation", "tick_processing"));
            }
        }
    }

    /// <summary>
    /// Event bus entry point — writes the price event into the bounded channel for
    /// backpressure-controlled consumption. The actual evaluation pipeline runs in
    /// <see cref="ProcessPriceUpdateAsync"/>. When the channel is full (256 items),
    /// the oldest queued tick is dropped (DropOldest policy).
    /// </summary>
    public async Task Handle(PriceUpdatedIntegrationEvent @event)
    {
        if (!_tickChannel.Writer.TryWrite(@event))
        {
            _metrics.TicksDroppedBackpressure.Add(1, new KeyValuePair<string, object?>("symbol", @event.Symbol));
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Invalidates the per-strategy metrics cache for the strategy whose backtest
    /// just completed so the next tick reloads its Sharpe and health from the DB.
    /// Keeping this handler lightweight — no DB I/O — lets the event bus deliver
    /// the completion broadcast without queue buildup.
    /// </summary>
    public Task Handle(BacktestCompletedIntegrationEvent @event)
    {
        _strategyMetricsCache.Invalidate(@event.StrategyId, trigger: "backtest_completed");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Invalidates the per-strategy metrics cache when a strategy flips to Active.
    /// The first tick after activation will then fetch the fresh snapshot rather
    /// than the stale null sentinel the worker may have cached earlier.
    /// </summary>
    public Task Handle(StrategyActivatedIntegrationEvent @event)
    {
        _strategyMetricsCache.Invalidate(@event.StrategyId, trigger: "strategy_activated");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Core evaluation pipeline — processes a single price-update event through the
    /// multi-stage filter chain and generates trade signals for matching strategies.
    /// Called from the bounded-channel consumer loop in <see cref="ExecuteAsync"/>.
    /// Internal for unit test access (InternalsVisibleTo).
    /// </summary>
    internal async Task ProcessPriceUpdateAsync(PriceUpdatedIntegrationEvent @event)
    {
        // Create a DI scope for the pre-loop checks (EA health, session, news, strategy loading).
        // Each parallel strategy iteration creates its own scope for scoped services.
        using var scope = _scopeFactory.CreateScope();
        var context        = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var sessionFilter  = scope.ServiceProvider.GetRequiredService<ISessionFilter>();
        var newsFilter     = scope.ServiceProvider.GetRequiredService<INewsFilter>();

        // ── Stale tick rejection ──────────────────────────────────────────────
        // Drop price events that are too old (e.g. from event bus backlog) to
        // prevent evaluating strategies on outdated prices.
        if (_options.MaxTickAgeSeconds > 0)
        {
            var tickAge = DateTime.UtcNow - @event.Timestamp;
            if (tickAge.TotalSeconds > _options.MaxTickAgeSeconds)
            {
                _logger.LogDebug(
                    "StrategyWorker: tick for {Symbol} is {Age:F1}s old (max {Max}s) — dropping stale event",
                    @event.Symbol, tickAge.TotalSeconds, _options.MaxTickAgeSeconds);
                _metrics.TicksSkippedStale.Add(1, new KeyValuePair<string, object?>("symbol", @event.Symbol));
                return;
            }
        }

        // ── EA health check (applied once per tick) ─────────────────────────
        // If no active EA instance with a fresh heartbeat owns this symbol, the data
        // is stale or unavailable. Prevent strategy evaluation on stale data.
        var maxHeartbeatAge = TimeSpan.FromSeconds(
            _options.MaxEAHeartbeatAgeSeconds > 0 ? _options.MaxEAHeartbeatAgeSeconds : 60);
        bool hasActiveEA = await context.GetDbContext()
            .Set<Domain.Entities.EAInstance>()
            .ActiveAndFreshForSymbol(@event.Symbol, maxHeartbeatAge)
            .AnyAsync(_stoppingToken);

        if (!hasActiveEA)
        {
            _logger.LogWarning(
                "StrategyWorker: no active EA instance owns {Symbol} — skipping tick (DATA_UNAVAILABLE)",
                @event.Symbol);
            _metrics.TicksSkippedNoEA.Add(1, new KeyValuePair<string, object?>("symbol", @event.Symbol));
            return;
        }

        // ── Session filter (applied once per tick, not per strategy) ────────
        // Use the event's timestamp rather than DateTime.UtcNow so that delayed
        // tick delivery doesn't cause the session check to be stale.
        if (_options.AllowedSessions.Count > 0)
        {
            var currentSession = sessionFilter.GetCurrentSession(@event.Timestamp);
            if (!sessionFilter.IsSessionAllowed(currentSession, _options.AllowedSessions))
            {
                _logger.LogDebug(
                    "StrategyWorker: session {Session} not in allowed list — skipping tick for {Symbol}",
                    currentSession, @event.Symbol);
                return;
            }
        }

        // ── News filter (applied once per tick for this symbol) ─────────────
        // Bounded with a per-tick timeout so a slow upstream news provider cannot
        // stall every tick for this symbol. On timeout or error we FAIL CLOSED —
        // drop the tick — because an unknown news state is indistinguishable from
        // "might be in blackout" and we should not trade blind. The timeout is
        // conservative (NewsFilterTimeoutSeconds, default 5s) since this runs on
        // the hot tick path.
        if (_options.NewsBlackoutMinutesBefore > 0 || _options.NewsBlackoutMinutesAfter > 0)
        {
            int timeoutSeconds = Math.Max(1, _options.NewsFilterTimeoutSeconds);
            bool safeToTrade;
            using (var newsCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken))
            {
                newsCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    safeToTrade = await newsFilter.IsSafeToTradeAsync(
                        @event.Symbol, @event.Timestamp,
                        _options.NewsBlackoutMinutesBefore, _options.NewsBlackoutMinutesAfter,
                        newsCts.Token);
                }
                catch (OperationCanceledException) when (!_stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "StrategyWorker: news filter timed out after {Timeout}s for {Symbol} — skipping tick (fail-closed)",
                        timeoutSeconds, @event.Symbol);
                    _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "news_filter_timeout"));
                    return;
                }
                catch (Exception ex) when (!_stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex,
                        "StrategyWorker: news filter failed for {Symbol} — skipping tick (fail-closed)",
                        @event.Symbol);
                    _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "news_filter_error"));
                    return;
                }
            }

            if (!safeToTrade)
            {
                _logger.LogInformation(
                    "StrategyWorker: news blackout active for {Symbol} — skipping tick",
                    @event.Symbol);
                return;
            }
        }

        // Load strategies eligible for evaluation: production Active strategies plus
        // Approved-but-Paused ones that are in the forward-test window (Approved =
        // passed promotion screens, Paused = not yet routed to live). The latter set
        // feeds the PaperExecution pipeline so the paper gate has real forward-fill
        // data before Active status is granted. Branching happens downstream at
        // signal-publication time — same evaluation pipeline, divergent persistence.
        var strategies = await context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .Where(x => x.Symbol == @event.Symbol
                     && !x.IsDeleted
                     && (x.Status == StrategyStatus.Active
                         || (x.Status == StrategyStatus.Paused
                             && x.LifecycleStage == StrategyLifecycleStage.Approved)))
            .ToListAsync(_stoppingToken);

        // ── Pre-fetch strategy health snapshots ─────────────────────────────
        // Filter out strategies with Critical health status to prevent evaluating
        // strategies that the StrategyHealthWorker has flagged as unhealthy.
        if (strategies.Count == 0)
        {
            _logger.LogDebug(
                "StrategyWorker: no active strategies for symbol {Symbol} — skipping tick",
                @event.Symbol);
            return;
        }

        var strategyIds = strategies.Select(s => s.Id).ToList();
        int prefetchTimeoutSeconds = Math.Max(1, _options.PrefetchQueryTimeoutSeconds);

        // ── Pre-fetch per-strategy metrics (Sharpe + health) via cache ──────
        // Single source for both the Critical-health filter and the Sharpe
        // lookup used by conflict resolution. The cache batches DB refreshes
        // across ticks (TTL = StrategyMetricsCacheTtlSeconds) and is invalidated
        // on BacktestCompletedIntegrationEvent / StrategyActivatedIntegrationEvent
        // so meaningful state changes propagate within one tick.
        //
        // Fail-closed on timeout: skip health filtering AND Sharpe ranking for
        // this tick. Evaluation still runs — a slow DB should not starve the
        // whole symbol — but conflict resolution degrades to a tiebreak without
        // Sharpe. The PrefetchQueryTimeouts counter surfaces the regression.
        var criticalStrategyIds = new HashSet<long>();
        var strategySharpeCache = new Dictionary<long, decimal>();
        if (strategyIds.Count > 0)
        {
            using var metricsCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            metricsCts.CancelAfter(TimeSpan.FromSeconds(prefetchTimeoutSeconds));
            try
            {
                var metricsSnapshot = await _strategyMetricsCache.GetManyAsync(
                    context.GetDbContext(), strategyIds, _options.StrategyMetricsCacheTtlSeconds, metricsCts.Token);
                foreach (var kv in metricsSnapshot)
                {
                    if (kv.Value.HealthStatus == StrategyHealthStatus.Critical)
                        criticalStrategyIds.Add(kv.Key);
                    if (kv.Value.Sharpe != 0m)
                        strategySharpeCache[kv.Key] = kv.Value.Sharpe;
                }
            }
            catch (OperationCanceledException) when (!_stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "StrategyWorker: strategy-metrics pre-fetch timed out after {Timeout}s for {Symbol} — skipping health filter and Sharpe ranking this tick",
                    prefetchTimeoutSeconds, @event.Symbol);
                _metrics.PrefetchQueryTimeouts.Add(1,
                    new("symbol", @event.Symbol),
                    new("query", "strategy_metrics"));
            }
            catch (Exception ex) when (!_stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "StrategyWorker: strategy-metrics pre-fetch failed for {Symbol} — skipping health filter and Sharpe ranking this tick",
                    @event.Symbol);
                _metrics.PrefetchQueryTimeouts.Add(1,
                    new("symbol", @event.Symbol),
                    new("query", "strategy_metrics"));
            }
        }

        // ── Pre-fetch regime snapshots for all distinct timeframes ───────────
        // Hoisted above the loop so that multiple strategies sharing the same
        // symbol/timeframe don't each trigger a separate DB query. Bounded by a
        // per-tick timeout — a slow regime table would otherwise make every
        // symbol look unblocked (DB stall reads as "no blocked regime found")
        // and bypass the whole regime filter silently. Fail-closed: drop the
        // tick so the regime filter can never be implicitly bypassed.
        var regimeCache = new Dictionary<Timeframe, MarketRegimeEnum>();
        if (_options.BlockedRegimes.Count > 0)
        {
            var timeframes = strategies.Select(s => s.Timeframe).Distinct().ToList();
            using var regimeCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            regimeCts.CancelAfter(TimeSpan.FromSeconds(prefetchTimeoutSeconds));
            try
            {
                foreach (var tf in timeframes)
                {
                    var symbolLatestRegimePerTimeframe = await context.GetDbContext()
                        .Set<Domain.Entities.MarketRegimeSnapshot>()
                        .Where(x => x.Symbol == @event.Symbol && x.Timeframe == tf && !x.IsDeleted)
                        .OrderByDescending(x => x.DetectedAt)
                        .Select(x => x.Regime)
                        .FirstOrDefaultAsync(regimeCts.Token);
                    regimeCache[tf] = symbolLatestRegimePerTimeframe;
                }
            }
            catch (OperationCanceledException) when (!_stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "StrategyWorker: regime pre-fetch timed out after {Timeout}s for {Symbol} — dropping tick (fail-closed)",
                    prefetchTimeoutSeconds, @event.Symbol);
                _metrics.PrefetchQueryTimeouts.Add(1,
                    new("symbol", @event.Symbol),
                    new("query", "regime"));
                _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "regime_prefetch_timeout"));
                await _rejectionAuditor.RecordAsync("Prefetch", "regime_prefetch_timeout",
                    @event.Symbol, nameof(StrategyWorker),
                    detail: $"Regime pre-fetch exceeded {prefetchTimeoutSeconds}s timeout");
                return;
            }
            catch (Exception ex) when (!_stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "StrategyWorker: regime pre-fetch failed for {Symbol} — dropping tick (fail-closed)",
                    @event.Symbol);
                _metrics.PrefetchQueryTimeouts.Add(1,
                    new("symbol", @event.Symbol),
                    new("query", "regime"));
                _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "regime_prefetch_error"));
                await _rejectionAuditor.RecordAsync("Prefetch", "regime_prefetch_error",
                    @event.Symbol, nameof(StrategyWorker),
                    detail: $"Regime pre-fetch failed: {ex.GetType().Name}");
                return;
            }
        }

        // ── Backtest qualification gate ────────────────────────────────────
        // Strategies must have at least one successful backtest that meets minimum
        // quality thresholds before they can generate live signals. This prevents
        // untested or poorly-performing strategies from producing real trades.
        // Wrapped in a per-call timeout: if the gate query stalls (large
        // BacktestRun table or DB contention) we treat nothing as qualified
        // rather than blocking the whole symbol indefinitely.
        HashSet<long> backtestQualifiedIds;
        using (var gateCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken))
        {
            gateCts.CancelAfter(TimeSpan.FromSeconds(prefetchTimeoutSeconds));
            try
            {
                backtestQualifiedIds = await GetBacktestQualifiedStrategyIdsAsync(
                    context.GetDbContext(), strategyIds, gateCts.Token);
            }
            catch (OperationCanceledException) when (!_stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "StrategyWorker: backtest-qualification query timed out after {Timeout}s for {Symbol} — no strategies qualify on this tick",
                    prefetchTimeoutSeconds, @event.Symbol);
                _metrics.PrefetchQueryTimeouts.Add(1,
                    new("symbol", @event.Symbol),
                    new("query", "backtest_qualification"));
                backtestQualifiedIds = [];
            }
            catch (Exception ex) when (!_stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "StrategyWorker: backtest-qualification query failed for {Symbol} — no strategies qualify on this tick",
                    @event.Symbol);
                _metrics.PrefetchQueryTimeouts.Add(1,
                    new("symbol", @event.Symbol),
                    new("query", "backtest_qualification"));
                backtestQualifiedIds = [];
            }
        }

        // ── Regime coherence check (cross-timeframe alignment) ───────────────
        // If regimes across H1/H4/D1 disagree for this symbol, the coherence
        // score is low and all signals for the symbol may be suppressed.
        //
        // Bounded with a per-tick timeout so a slow DB query cannot stall every
        // strategy for this symbol. On timeout or query failure we FAIL CLOSED —
        // suppress all signals for the symbol on this tick. Previously we defaulted
        // to 1.0 (full coherence), which masked data-plane outages: a slow DB made
        // every symbol look perfectly coherent and all regime-based filtering was
        // bypassed. Fail-closed surfaces the issue via the suppression metric and
        // ensures no signal is emitted without the coherence signal actually being
        // verified. When MinRegimeCoherence is zero (filter disabled), we skip the
        // check entirely — no point failing closed on a disabled gate.
        if (_options.MinRegimeCoherence > 0)
        {
            decimal regimeCoherence;
            using (var coherenceCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken))
            {
                coherenceCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    regimeCoherence = await _regimeCoherenceChecker.GetCoherenceScoreAsync(
                        @event.Symbol, coherenceCts.Token);
                }
                catch (OperationCanceledException) when (!_stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "StrategyWorker: regime coherence query timed out for {Symbol} — suppressing all signals (fail-closed)",
                        @event.Symbol);
                    _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "regime_coherence_timeout"));
                    await _rejectionAuditor.RecordAsync("Regime", "regime_coherence_timeout",
                        @event.Symbol, nameof(StrategyWorker),
                        detail: "Regime coherence query timed out — fail-closed");
                    return;
                }
                catch (Exception ex) when (!_stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex,
                        "StrategyWorker: regime coherence query failed for {Symbol} — suppressing all signals (fail-closed)",
                        @event.Symbol);
                    _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "regime_coherence_error"));
                    await _rejectionAuditor.RecordAsync("Regime", "regime_coherence_error",
                        @event.Symbol, nameof(StrategyWorker),
                        detail: $"Regime coherence query failed: {ex.GetType().Name}");
                    return;
                }
            }

            if (regimeCoherence < _options.MinRegimeCoherence)
            {
                _logger.LogInformation(
                    "StrategyWorker: regime coherence for {Symbol} is {Coherence:F2} (< {Threshold:F2}) — suppressing all signals",
                    @event.Symbol, regimeCoherence, _options.MinRegimeCoherence);
                _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "regime_coherence"));
                await _rejectionAuditor.RecordAsync("Regime", "regime_coherence_low",
                    @event.Symbol, nameof(StrategyWorker),
                    detail: $"Coherence {regimeCoherence:F2} < threshold {_options.MinRegimeCoherence:F2}");
                return;
            }
        }

        // ── Candidate signal collection bag ──────────────────────────────────
        // Instead of publishing signals immediately inside the parallel loop,
        // we collect candidates here and resolve conflicts after all strategies
        // have been evaluated. This two-phase approach (collect → resolve → publish)
        // enables cross-strategy conflict detection: e.g. if Strategy A says BUY EURUSD
        // and Strategy B says SELL EURUSD on the same tick, the conflict resolver picks
        // the winner based on Sharpe ratio, ML confidence, and estimated capacity.
        var candidateSignals = new ConcurrentBag<(PendingSignal Pending, MLScoreResult MlScore, int? MlScoringLatencyMs)>();

        // ── Parallel strategy evaluation ─────────────────────────────────────
        // Each strategy gets its own DI scope so scoped services (DbContext, ML scorer,
        // filters) have independent lifetimes. The pre-fetched regimeCache,
        // criticalStrategyIds, and backtestQualifiedIds are read-only and safe to share
        // across parallel iterations without synchronisation.
        // MaxDegreeOfParallelism caps CPU utilisation to prevent thread pool starvation
        // when many strategies are active on a single symbol.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelStrategies),
            CancellationToken      = _stoppingToken
        };

        await Parallel.ForEachAsync(strategies, parallelOptions, async (strategy, ct) =>
        {
            var evalStopwatch = Stopwatch.StartNew();
            try
            {
                // ── Strategy health filter ──────────────────────────────────
                if (criticalStrategyIds.Contains(strategy.Id))
                {
                    _logger.LogInformation(
                        "Strategy {Id} ({Symbol}): health status is Critical — skipping evaluation",
                        strategy.Id, strategy.Symbol);
                    _metrics.TicksSkippedHealthCritical.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                    return;
                }

                // ── Backtest qualification gate ───────────────────────────────
                // A strategy must have at least one completed backtest that meets
                // minimum quality thresholds (WinRate, ProfitFactor, Sharpe) before
                // it can generate live signals. Untested strategies are skipped.
                if (!backtestQualifiedIds.Contains(strategy.Id))
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}/{Tf}): no qualifying backtest — skipping signal generation",
                        strategy.Id, strategy.Symbol, strategy.Timeframe);
                    return;
                }

                // ── Per-strategy circuit breaker (with half-open recovery) ────
                if (_options.MaxConsecutiveFailures > 0 &&
                    _consecutiveFailures.TryGetValue(strategy.Id, out var failures) &&
                    failures >= _options.MaxConsecutiveFailures)
                {
                    // Half-open: allow a single probe after the recovery period.
                    // If the probe has failed too many times, permanently skip this strategy.
                    if (_halfOpenProbeFailures.TryGetValue(strategy.Id, out var probeFailures) &&
                        probeFailures >= MaxHalfOpenProbeFailures)
                    {
                        _logger.LogWarning(
                            "Strategy {Id} ({Symbol}): circuit breaker PERMANENTLY OPEN — {Failures} consecutive half-open probe failures",
                            strategy.Id, strategy.Symbol, probeFailures);
                        _metrics.StrategiesCircuitBroken.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                        return;
                    }

                    if (_options.CircuitBreakerRecoverySeconds > 0 &&
                        _circuitOpenedAt.TryGetValue(strategy.Id, out var openedAt) &&
                        (DateTime.UtcNow - openedAt).TotalSeconds >= _options.CircuitBreakerRecoverySeconds)
                    {
                        _logger.LogInformation(
                            "Strategy {Id} ({Symbol}): circuit breaker half-open — allowing probe evaluation after {Recovery}s (probe attempt {Attempt}/{Max})",
                            strategy.Id, strategy.Symbol, _options.CircuitBreakerRecoverySeconds,
                            (probeFailures) + 1, MaxHalfOpenProbeFailures);
                        // Let the evaluation proceed as a probe; success resets, failure re-opens
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Strategy {Id} ({Symbol}): circuit breaker open — {Failures} consecutive failures (max {Max})",
                            strategy.Id, strategy.Symbol, failures, _options.MaxConsecutiveFailures);
                        _metrics.StrategiesCircuitBroken.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                        return;
                    }
                }

                // ── Per-strategy signal cooldown ─────────────────────────────
                // Bar-aware floor: even when SignalCooldownSeconds is unset, prevent
                // the same strategy from emitting more than one signal per candle by
                // enforcing a minimum cooldown equal to the strategy's bar duration.
                // Without this, a degenerate evaluator that always returns the same
                // decision can fire 5+ signals within 400ms of ticks inside the same
                // candle — we observed exactly this on the first live fills.
                int barSeconds = strategy.Timeframe switch
                {
                    Timeframe.M1  => 60,
                    Timeframe.M5  => 300,
                    Timeframe.M15 => 900,
                    Timeframe.H1  => 3600,
                    Timeframe.H4  => 14400,
                    Timeframe.D1  => 86400,
                    _             => 300,
                };
                int effectiveCooldown = Math.Max(_options.SignalCooldownSeconds, barSeconds);
                if (effectiveCooldown > 0 &&
                    _lastSignalTime.TryGetValue(strategy.Id, out var lastTime) &&
                    (DateTime.UtcNow - lastTime).TotalSeconds < effectiveCooldown)
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}/{Tf}): cooldown active — {Remaining:F0}s of {Total}s remaining",
                        strategy.Id, strategy.Symbol, strategy.Timeframe,
                        effectiveCooldown - (DateTime.UtcNow - lastTime).TotalSeconds,
                        effectiveCooldown);
                    _metrics.SignalCooldownSkips.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                    return;
                }

                // ── Concurrency guard ─────────────────────────────────────────
                // Prevent duplicate evaluation if price events arrive faster than
                // evaluation completes — only one evaluation per strategy at a time.
                // We wrap the acquisition in a Stopwatch so dashboards can alert
                // on lock-contention creep BEFORE it manifests as dropped ticks:
                // a p95 that steadily climbs toward LockTimeoutSeconds is the
                // leading indicator, dropped ticks are the lagging one.
                var lockKey = $"strategy:eval:{strategy.Id}";
                var lockTimeout = TimeSpan.FromSeconds(_options.LockTimeoutSeconds);
                var lockSw = Stopwatch.StartNew();
                var evalLock = await _distributedLock.TryAcquireAsync(lockKey, lockTimeout, ct);
                lockSw.Stop();
                if (evalLock is null)
                {
                    _metrics.StrategyLockAcquisitionMs.Record(lockSw.Elapsed.TotalMilliseconds,
                        new("outcome", "busy"),
                        new("symbol", strategy.Symbol));
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}) evaluation already in progress — skipping this tick (waited {WaitMs:F0}ms)",
                        strategy.Id, strategy.Symbol, lockSw.Elapsed.TotalMilliseconds);
                    _metrics.TicksDroppedLockBusy.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                    return;
                }
                _metrics.StrategyLockAcquisitionMs.Record(lockSw.Elapsed.TotalMilliseconds,
                    new("outcome", "acquired"),
                    new("symbol", strategy.Symbol));
                await using var evalLockScope = evalLock;

                // Resolve the strategy-specific evaluator by matching the StrategyType enum.
                // Each evaluator implements the trading logic for one strategy type:
                //   - BreakoutScalperEvaluator → StrategyType.BreakoutScalper
                //   - MovingAverageCrossoverEvaluator → StrategyType.MovingAverageCrossover
                //   - RSIReversionEvaluator → StrategyType.RSIReversion
                // If no evaluator is registered, the strategy is skipped and a decision log is created.
                var evaluator = _evaluators.FirstOrDefault(e => e.StrategyType == strategy.StrategyType);
                if (evaluator is null)
                {
                    // Missing-evaluator registrations previously went unnoticed until
                    // someone traced the decision log. Emit a tagged metric so a
                    // broken DI binding surfaces on dashboards within seconds, not
                    // after an operator digs through audit rows.
                    _metrics.EvaluatorMissing.Add(1,
                        new("strategy_type", strategy.StrategyType.ToString()),
                        new("symbol", strategy.Symbol));
                    _logger.LogWarning("No evaluator found for StrategyType={Type}", strategy.StrategyType);

                    // Per-iteration scope for scoped services (DbContext, mediator, filters)
                    using var iterScope = _scopeFactory.CreateScope();
                    var iterMediator = iterScope.ServiceProvider.GetRequiredService<IMediator>();

                    await iterMediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "Strategy",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Skipped",
                        Reason       = $"No evaluator registered for StrategyType={strategy.StrategyType}",
                        Source       = "StrategyWorker"
                    }, ct);

                    return;
                }

                // ── Per-iteration DI scope ──────────────────────────────────
                // Scoped services (DbContext, ML scorer, filters) need independent
                // lifetimes per parallel iteration.
                using var strategyScope   = _scopeFactory.CreateScope();
                var strategyContext       = strategyScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var strategyMediator      = strategyScope.ServiceProvider.GetRequiredService<IMediator>();
                var strategyMlScorer      = strategyScope.ServiceProvider.GetRequiredService<IMLSignalScorer>();
                var strategyCache         = strategyScope.ServiceProvider.GetRequiredService<ILivePriceCache>();
                var strategyMtfFilter     = strategyScope.ServiceProvider.GetRequiredService<IMultiTimeframeFilter>();
                var strategyCorrelation   = strategyScope.ServiceProvider.GetRequiredService<IPortfolioCorrelationChecker>();
                var strategyHawkesFilter  = strategyScope.ServiceProvider.GetRequiredService<IHawkesSignalFilter>();

                // ── Dynamic candle fetch based on evaluator requirements ────
                // Each evaluator declares how many historical candles it needs
                // (e.g., MA crossover needs 200+, RSI needs 14+). We fetch only
                // what's required to avoid unnecessary DB load.
                int requiredCandles = evaluator.MinRequiredCandles(strategy);
                var candles = await strategyContext.GetDbContext()
                    .Set<Domain.Entities.Candle>()
                    .Where(x => x.Symbol == strategy.Symbol && x.Timeframe == strategy.Timeframe && x.IsClosed && !x.IsDeleted)
                    .OrderByDescending(x => x.Timestamp)
                    .Take(requiredCandles)
                    .OrderBy(x => x.Timestamp)      // Re-order ascending for indicator calculations
                    .ToListAsync(ct);

                // Skip if insufficient historical candles for the evaluator
                if (candles.Count < requiredCandles)
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}/{Tf}): only {Available}/{Required} candles available — skipping",
                        strategy.Id, strategy.Symbol, strategy.Timeframe, candles.Count, requiredCandles);
                    return;
                }

                // Skip if no live price is available in the cache. This can happen when
                // the EA has not yet streamed a tick for the symbol, or during a brief
                // reconnect window — emitting a metric + debug log keeps the gap visible
                // in operational dashboards rather than failing silently.
                var price = strategyCache.Get(strategy.Symbol);
                if (price is null)
                {
                    _metrics.TicksSkippedNoLivePrice.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                    _logger.LogDebug(
                        "Strategy {Id}: live price not in cache for {Symbol} — skipping evaluation",
                        strategy.Id, strategy.Symbol);
                    return;
                }

                // ── Market regime filter (uses pre-fetched cache) ────────────
                if (regimeCache.TryGetValue(strategy.Timeframe, out var latestRegime) &&
                    _options.BlockedRegimes.Contains(latestRegime))
                {
                    _logger.LogInformation(
                        "Strategy {Id}: market regime {Regime} is blocked for {Symbol}/{Tf} — skipping",
                        strategy.Id, latestRegime, strategy.Symbol, strategy.Timeframe);

                    _metrics.SignalsFiltered.Add(1, new("symbol", strategy.Symbol), new("stage", "regime"));
                    await _rejectionAuditor.RecordAsync("Regime", "regime_blocked",
                        strategy.Symbol, nameof(StrategyWorker),
                        strategyId: strategy.Id,
                        detail: $"Regime {latestRegime} is in BlockedRegimes for {strategy.Timeframe}",
                        ct: ct);

                    await strategyMediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Filtered",
                        Reason       = $"Market regime {latestRegime} is in blocked list",
                        Source       = "StrategyWorker"
                    }, ct);

                    return;
                }

                // ── Gradual rollout parameter routing ──────────────────────────────
                // If an optimization rollout is in progress (RolloutPct < 100), route
                // traffic between old and new parameters deterministically based on a
                // seed derived from the tick timestamp. This ensures the same tick
                // always picks the same parameter set, enabling consistent A/B comparison.
                // Rollout runs BEFORE regime params so that when regime params fail
                // validation, the fallback preserves rollout modifications (evalStrategy)
                // instead of reverting to the original unmodified strategy.
                var evalStrategy = strategy;
                if (strategy.RolloutPct is not null and < 100)
                {
                    int rolloutSeed = HashCode.Combine(strategy.Id, @event.Timestamp.Ticks / TimeSpan.TicksPerMinute);
                    string selectedParams = Optimization.GradualRolloutManager.SelectParameters(strategy, rolloutSeed);
                    if (selectedParams != evalStrategy.ParametersJson)
                    {
                        evalStrategy = new Domain.Entities.Strategy
                        {
                            Id                      = evalStrategy.Id,
                            Name                    = evalStrategy.Name,
                            Description             = evalStrategy.Description,
                            StrategyType            = evalStrategy.StrategyType,
                            Symbol                  = evalStrategy.Symbol,
                            Timeframe               = evalStrategy.Timeframe,
                            ParametersJson          = selectedParams,
                            Status                  = evalStrategy.Status,
                            RiskProfileId           = evalStrategy.RiskProfileId,
                            CreatedAt               = evalStrategy.CreatedAt,
                            LifecycleStage          = evalStrategy.LifecycleStage,
                            LifecycleStageEnteredAt = evalStrategy.LifecycleStageEnteredAt,
                            EstimatedCapacityLots   = evalStrategy.EstimatedCapacityLots,
                            IsDeleted               = evalStrategy.IsDeleted,
                        };
                    }
                }

                // ── Regime-conditional parameter swap ────────────────────────────────
                // If the OptimizationWorker has stored regime-specific parameters for
                // the current market regime, apply them for this evaluation. We create
                // a shallow copy to avoid dirtying the tracked entity in the outer
                // DbContext's change tracker. When regime params fail validation, the
                // fallback is evalStrategy (which already has rollout modifications).
                if (regimeCache.TryGetValue(strategy.Timeframe, out var currentRegime))
                {
                    // Cached lookup: regime-conditional parameters only change when
                    // the OptimizationWorker promotes a new set, so repeated DB
                    // queries per tick are wasteful. TTL-bounded (default 120s).
                    var regimeParams = await _regimeParamsCache.GetAsync(
                        strategyContext.GetDbContext(),
                        strategy.Id,
                        currentRegime,
                        _options.RegimeParamsCacheTtlSeconds,
                        ct);

                    if (regimeParams is not null)
                    {
                        // Validate regime params before applying — reject malformed JSON
                        // or params with invalid values (negative lots, NaN, etc.)
                        bool regimeParamsValid = true;
                        try
                        {
                            var parsed = System.Text.Json.JsonDocument.Parse(regimeParams);
                            foreach (var prop in parsed.RootElement.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                                    && (double.IsNaN(prop.Value.GetDouble()) || double.IsInfinity(prop.Value.GetDouble())))
                                {
                                    regimeParamsValid = false;
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            regimeParamsValid = false;
                        }

                        if (!regimeParamsValid)
                        {
                            _logger.LogWarning(
                                "Strategy {Id}: regime params for {Regime} failed validation — using evalStrategy params (rollout may be active)",
                                strategy.Id, currentRegime);
                            _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("reason", "regime_param_validation"));
                        }
                        else
                        {
                        _logger.LogDebug(
                            "Strategy {Id} ({Symbol}/{Tf}): applying regime-conditional params for {Regime}",
                            strategy.Id, strategy.Symbol, strategy.Timeframe, currentRegime);
                        evalStrategy = new Domain.Entities.Strategy
                        {
                            Id                      = evalStrategy.Id,
                            Name                    = evalStrategy.Name,
                            Description             = evalStrategy.Description,
                            StrategyType            = evalStrategy.StrategyType,
                            Symbol                  = evalStrategy.Symbol,
                            Timeframe               = evalStrategy.Timeframe,
                            ParametersJson          = regimeParams,
                            Status                  = evalStrategy.Status,
                            RiskProfileId           = evalStrategy.RiskProfileId,
                            CreatedAt               = evalStrategy.CreatedAt,
                            LifecycleStage          = evalStrategy.LifecycleStage,
                            LifecycleStageEnteredAt = evalStrategy.LifecycleStageEnteredAt,
                            EstimatedCapacityLots   = evalStrategy.EstimatedCapacityLots,
                            IsDeleted               = evalStrategy.IsDeleted,
                        };
                        } // end regimeParamsValid else
                    }
                }

                // ── Core strategy evaluation ─────────────────────────────────────
                // This is where the actual trading logic runs. The evaluator analyses
                // the historical candles and current bid/ask to determine if a trade
                // signal should be generated (e.g., MA crossover detected, RSI hit
                // oversold level). Returns null if no signal condition is met on this
                // tick — which is the common case (most ticks don't trigger signals).
                Domain.Entities.TradeSignal? signal;
                using (var evalCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    evalCts.CancelAfter(TimeSpan.FromSeconds(_options.EvaluatorTimeoutSeconds));
                    try
                    {
                        signal = await evaluator.EvaluateAsync(evalStrategy, candles, (price.Value.Bid, price.Value.Ask), evalCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "Strategy {Id} ({Symbol}): evaluator timed out after {Timeout}s",
                            strategy.Id, strategy.Symbol, _options.EvaluatorTimeoutSeconds);
                        _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("reason", "evaluator_timeout"));
                        _consecutiveFailures.AddOrUpdate(strategy.Id, 1, (_, count) => count + 1);
                        _dirtyStrategyIds[strategy.Id] = 1;
                        return;
                    }
                }
                if (signal is null) return;

                // ── Post-evaluator confidence modifiers ──────────────────────
                // These adjust signal confidence based on cross-cutting factors
                // that apply to all evaluators, not just one strategy type.

                // Session quality: scale confidence based on current session's
                // typical liquidity (e.g. LondonNYOverlap = 1.0, Asian = 0.5).
                if (_options.SessionConfidenceWeight > 0 && _options.AllowedSessions.Count > 0)
                {
                    var currentSession = sessionFilter.GetCurrentSession(@event.Timestamp);
                    decimal sessionQuality = sessionFilter.GetSessionQuality(currentSession);
                    decimal sessionFactor = 1.0m - _options.SessionConfidenceWeight * (1.0m - sessionQuality);
                    signal.Confidence = Math.Clamp(signal.Confidence * sessionFactor, 0.1m, 1.0m);
                }

                // Multi-timeframe strength: when MTF is not a hard gate, use the
                // confirmation ratio to scale confidence (0/2 confirmed = penalty,
                // 2/2 = full confidence). Skipped when MTF is a hard gate because
                // the signal already passed the binary check.
                if (_options.MultiTimeframeConfidenceWeight > 0 && !_options.RequireMultiTimeframeConfirmation)
                {
                    decimal mtfStrength = await strategyMtfFilter.GetConfirmationStrengthAsync(
                        signal.Symbol, signal.Direction.ToString(),
                        strategy.Timeframe.ToString(), ct);
                    decimal mtfFactor = 1.0m - _options.MultiTimeframeConfidenceWeight * (1.0m - mtfStrength);
                    signal.Confidence = Math.Clamp(signal.Confidence * mtfFactor, 0.1m, 1.0m);
                }

                // ── Multi-timeframe confirmation filter ─────────────────────
                if (_options.RequireMultiTimeframeConfirmation)
                {
                    bool confirmed = await strategyMtfFilter.IsConfirmedAsync(
                        signal.Symbol, signal.Direction.ToString(),
                        strategy.Timeframe.ToString(), ct);

                    if (!confirmed)
                    {
                        _logger.LogDebug(
                            "Strategy {Id}: {Direction} signal on {Symbol} not confirmed by higher timeframes — skipping",
                            strategy.Id, signal.Direction, signal.Symbol);

                        _metrics.SignalsFiltered.Add(1, new("symbol", signal.Symbol), new("stage", "mtf"));
                        await _rejectionAuditor.RecordAsync("MTF", "mtf_not_confirmed",
                            signal.Symbol, nameof(StrategyWorker),
                            strategyId: strategy.Id,
                            detail: $"Higher-timeframe confirmation failed for {signal.Direction}",
                            ct: ct);

                        await strategyMediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "TradeSignal",
                            EntityId     = strategy.Id,
                            DecisionType = "SignalGeneration",
                            Outcome      = "Filtered",
                            Reason       = "Multi-timeframe confirmation failed",
                            Source       = "StrategyWorker"
                        }, ct);

                        return;
                    }
                }

                // ── Portfolio correlation check ─────────────────────────────
                if (_options.MaxCorrelatedPositions > 0)
                {
                    bool breached = await strategyCorrelation.IsCorrelationBreachedAsync(
                        signal.Symbol, signal.Direction.ToString(),
                        _options.MaxCorrelatedPositions, ct);

                    if (breached)
                    {
                        _logger.LogInformation(
                            "Strategy {Id}: correlation limit breached for {Symbol} {Direction} — skipping",
                            strategy.Id, signal.Symbol, signal.Direction);

                        _metrics.SignalsFiltered.Add(1, new("symbol", signal.Symbol), new("stage", "correlation"));
                        await _rejectionAuditor.RecordAsync("Correlation", "correlation_breached",
                            signal.Symbol, nameof(StrategyWorker),
                            strategyId: strategy.Id,
                            detail: $"Max correlated positions ({_options.MaxCorrelatedPositions}) breached",
                            ct: ct);

                        await strategyMediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "TradeSignal",
                            EntityId     = strategy.Id,
                            DecisionType = "SignalGeneration",
                            Outcome      = "Filtered",
                            Reason       = $"Portfolio correlation limit ({_options.MaxCorrelatedPositions}) breached for {signal.Symbol}",
                            Source       = "StrategyWorker"
                        }, ct);

                        return;
                    }
                }

                // ── Hawkes burst filter (scoped to this strategy) ────────────
                if (_options.HawkesRecentSignalCount > 0)
                {
                    var recentTimestamps = await strategyContext.GetDbContext()
                        .Set<Domain.Entities.TradeSignal>()
                        .Where(x => x.Symbol == strategy.Symbol && x.StrategyId == strategy.Id && !x.IsDeleted)
                        .OrderByDescending(x => x.GeneratedAt)
                        .Take(_options.HawkesRecentSignalCount)
                        .Select(x => x.GeneratedAt)
                        .ToListAsync(ct);

                    bool isBurst = await strategyHawkesFilter.IsBurstEpisodeAsync(
                        signal.Symbol, strategy.Timeframe, recentTimestamps, ct);

                    if (isBurst)
                    {
                        _logger.LogInformation(
                            "Strategy {Id}: Hawkes burst detected for {Symbol}/{Tf} — suppressing signal",
                            strategy.Id, signal.Symbol, strategy.Timeframe);

                        _metrics.SignalsFiltered.Add(1, new("symbol", signal.Symbol), new("stage", "hawkes"));
                        await _rejectionAuditor.RecordAsync("Hawkes", "hawkes_burst",
                            signal.Symbol, nameof(StrategyWorker),
                            strategyId: strategy.Id,
                            detail: "Hawkes process detected signal-clustering episode",
                            ct: ct);

                        await strategyMediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "TradeSignal",
                            EntityId     = strategy.Id,
                            DecisionType = "SignalGeneration",
                            Outcome      = "Filtered",
                            Reason       = "Hawkes process burst episode — signal clustering detected",
                            Source       = "StrategyWorker"
                        }, ct);

                        return;
                    }
                }

                // ── ML scoring ──────────────────────────────────────────────
                // The ML pipeline runs the active model (if any) to:
                //   1. Predict the likely direction (Buy/Sell) and magnitude in pips
                //   2. Produce a calibrated probability and confidence score
                //   3. Determine if the model should abstain (low-confidence environments)
                //   4. Check ensemble disagreement across bagged learners
                // The ML score enriches the signal and can veto it entirely.
                //
                // Stopwatch captures end-to-end scorer latency (including internal calibration
                // and ensemble steps) so MLModelPredictionLog.LatencyMs can drive P50/P95/P99
                // SLA dashboards.
                //
                // Circuit-breaker scope: ML-stack exceptions (inference crashes, calibration
                // worker outages, ONNX runtime errors, etc.) are NOT the strategy's fault.
                // We fail closed on the signal (drop it) but do NOT increment the strategy's
                // consecutive-failure counter — otherwise a transient infra issue would open
                // every strategy's circuit breaker. The outer catch (evaluator-level) still
                // handles strategy-logic exceptions and opens the circuit normally.
                MLScoreResult mlScore;
                int? mlScoringLatencyMs;
                var mlScoringStopwatch = Stopwatch.StartNew();
                int mlTimeoutSeconds = Math.Max(1, _options.MLScoringTimeoutSeconds);
                using var mlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                mlCts.CancelAfter(TimeSpan.FromSeconds(mlTimeoutSeconds));
                try
                {
                    mlScore = await strategyMlScorer.ScoreAsync(signal, candles, mlCts.Token);
                    mlScoringStopwatch.Stop();
                    mlScoringLatencyMs = mlScore.MLModelId.HasValue
                        ? (int)Math.Min(int.MaxValue, mlScoringStopwatch.ElapsedMilliseconds)
                        : null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (mlCts.IsCancellationRequested)
                {
                    // ML scoring exceeded its per-call timeout. Same fail-closed path
                    // as a scorer exception: drop the signal but do NOT open the
                    // strategy circuit breaker — a slow ONNX runtime / backed-up
                    // scorer queue is infra, not strategy-logic failure.
                    mlScoringStopwatch.Stop();
                    _logger.LogError(
                        "Strategy {Id}: ML scoring timed out after {Timeout}s for {Symbol}/{Tf} — suppressing signal (fail-closed); strategy circuit breaker NOT incremented",
                        strategy.Id, mlTimeoutSeconds, signal.Symbol, strategy.Timeframe);

                    _metrics.MLScoringTimeouts.Add(1,
                        new("symbol", signal.Symbol),
                        new("strategy_id", strategy.Id));
                    _metrics.SignalsSuppressed.Add(1,
                        new("symbol", signal.Symbol),
                        new("reason", "ml_scoring_timeout"));
                    await _rejectionAuditor.RecordAsync("MLScoring", "ml_scoring_timeout",
                        signal.Symbol, nameof(StrategyWorker),
                        strategyId: strategy.Id,
                        detail: $"IMLSignalScorer.ScoreAsync exceeded {mlTimeoutSeconds}s",
                        ct: ct);

                    await strategyMediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Suppressed",
                        Reason       = $"ML scorer timed out after {mlTimeoutSeconds}s",
                        Source       = "StrategyWorker"
                    }, ct);

                    return;
                }
                catch (Exception mlEx)
                {
                    mlScoringStopwatch.Stop();
                    _logger.LogError(mlEx,
                        "Strategy {Id}: ML scorer failed for {Symbol}/{Tf} — suppressing signal (fail-closed); strategy circuit breaker NOT incremented",
                        strategy.Id, signal.Symbol, strategy.Timeframe);

                    _metrics.SignalsSuppressed.Add(1,
                        new("symbol", signal.Symbol),
                        new("reason", "ml_scorer_error"));

                    await _rejectionAuditor.RecordAsync("MLScoring", "ml_scorer_error",
                        signal.Symbol, nameof(StrategyWorker),
                        strategyId: strategy.Id,
                        detail: $"{mlEx.GetType().Name}: {mlEx.Message}",
                        ct: ct);

                    await strategyMediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Suppressed",
                        Reason       = $"ML scorer threw {mlEx.GetType().Name}: {mlEx.Message}",
                        Source       = "StrategyWorker"
                    }, ct);

                    // Fail-closed: drop the signal. The rule signal is discarded — we never
                    // send unscored signals downstream. Strategy circuit state is left alone:
                    // this was an infra/ML failure, not a strategy failure.
                    return;
                }

                // ML suppression: when the scorer returns a model ID but null predicted
                // direction, it means the model actively chose NOT to score (cooldown period,
                // consensus failure among bagged learners, or selective scoring gate).
                // This is different from "no ML model active" (MLModelId == null).
                if (mlScore.MLModelId.HasValue && !mlScore.PredictedDirection.HasValue)
                {
                    _logger.LogInformation(
                        "Strategy {Id}: ML scoring suppressed for {Symbol}/{Tf} (model {ModelId}) — skipping signal",
                        strategy.Id, signal.Symbol, strategy.Timeframe, mlScore.MLModelId);

                    _metrics.SignalsSuppressed.Add(1, new("symbol", signal.Symbol), new("reason", "ml_suppression"));
                    await _rejectionAuditor.RecordAsync("MLScoring", "ml_suppression",
                        signal.Symbol, nameof(StrategyWorker),
                        strategyId: strategy.Id,
                        detail: $"ML model {mlScore.MLModelId} suppressed scoring (cooldown/consensus/selective)",
                        ct: ct);

                    await strategyMediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Suppressed",
                        Reason       = $"ML model {mlScore.MLModelId} suppressed scoring (cooldown/consensus/selective gate)",
                        Source       = "StrategyWorker"
                    }, ct);

                    return;
                }

                // Abstention gate: if the ML model indicates the environment is untradeable,
                // skip signal creation.
                if (_options.MinAbstentionScore > 0 &&
                    mlScore.AbstentionScore.HasValue &&
                    mlScore.AbstentionScore.Value < _options.MinAbstentionScore)
                {
                    _logger.LogInformation(
                        "Strategy {Id}: abstention score {Score:F3} below threshold {Threshold:F3} for {Symbol} — skipping",
                        strategy.Id, mlScore.AbstentionScore, _options.MinAbstentionScore, signal.Symbol);

                    _metrics.SignalsSuppressed.Add(1, new("symbol", signal.Symbol), new("reason", "abstention"));
                    await _rejectionAuditor.RecordAsync("Abstention", "abstention_below_threshold",
                        signal.Symbol, nameof(StrategyWorker),
                        strategyId: strategy.Id,
                        detail: $"Score {mlScore.AbstentionScore:F3} < threshold {_options.MinAbstentionScore:F3}",
                        ct: ct);

                    await strategyMediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Suppressed",
                        Reason       = $"ML abstention score {mlScore.AbstentionScore:F3} < threshold {_options.MinAbstentionScore:F3}",
                        Source       = "StrategyWorker"
                    }, ct);

                    return;
                }

                // ── Attach ML predictions to the signal ────────────────────
                // These fields enrich the signal with the ML model's directional
                // prediction, expected magnitude in pips, and confidence score
                // for downstream use by the SignalOrderBridgeWorker and audit trail.
                signal.MLPredictedDirection = mlScore.PredictedDirection;
                signal.MLPredictedMagnitude = mlScore.PredictedMagnitudePips;
                signal.MLConfidenceScore    = mlScore.ConfidenceScore;
                signal.MLModelId            = mlScore.MLModelId;

                // ── DrawdownRecovery: reduce lot size when in Reduced mode ────
                // When the DrawdownRecoveryWorker has placed the portfolio into
                // "Reduced" mode, scale the suggested lot size down. The cached
                // provider avoids a two-query DB hit per signal on this hot path;
                // DrawdownRecoveryWorker invalidates the cache when the mode flips.
                var recoverySnapshot = await _drawdownRecoveryModeProvider.GetAsync(ct);

                decimal adjustedLotSize = signal.SuggestedLotSize;
                if (recoverySnapshot.IsReduced)
                {
                    adjustedLotSize = signal.SuggestedLotSize * recoverySnapshot.ReducedLotMultiplier;
                    _logger.LogInformation(
                        "StrategyWorker: DrawdownRecovery Reduced mode active — lot size reduced {Original} → {Adjusted} (×{Mult}) for {Symbol}",
                        signal.SuggestedLotSize, adjustedLotSize, recoverySnapshot.ReducedLotMultiplier, signal.Symbol);
                }

                // ── Compute the long/short ATR ratio once; shared by vol-target sizing
                //    and vol-conditional TTL below. Null when the candle window is thin
                //    or ATR is degenerate — both consumers then skip their adjustments.
                double? atrRatio = null;
                try
                {
                    if (candles.Count >= 60)
                    {
                        var recentWindow = candles.Skip(candles.Count - 15).Take(15).ToList();
                        var longWindow   = candles.Skip(candles.Count - 60).Take(60).ToList();
                        double shortAtr = MLModels.Shared.MLFeatureHelper.CalculateATR(recentWindow, 14);
                        double longAtr  = MLModels.Shared.MLFeatureHelper.CalculateATR(longWindow, 14);
                        if (shortAtr > 0 && longAtr > 0)
                            atrRatio = longAtr / shortAtr;
                    }
                }
                catch { /* atrRatio stays null; consumers no-op */ }

                // ── Vol-targeted position sizing: scale inversely to realized volatility ──
                // Goal: target constant P&L variance. High-vol → smaller; low-vol → larger.
                // Clamped [0.5, 2.0] so extreme regimes don't 10× the position. Applied
                // AFTER DrawdownRecovery so both compound.
                if (atrRatio is double r)
                {
                    decimal volScale = (decimal)Math.Clamp(r, 0.5, 2.0);
                    adjustedLotSize *= volScale;
                }

                // ── Portfolio correlation adjustment ───────────────────────────
                // Shrink lot size when same-direction correlated positions are already
                // open. Multiplier = 1/sqrt(1 + Σ ρ) across same-direction peers.
                decimal correlationMult = await _portfolioCorrelationSizer.ComputeMultiplierAsync(
                    signal.Symbol, signal.Direction, ct);
                if (correlationMult < 1.0m)
                {
                    adjustedLotSize *= correlationMult;
                }

                // ── Vol-conditional TTL: shorten expiry in HighVol, extend in LowVol ─
                // Information rot scales with realized volatility. Clamped [0.3, 3.0]
                // so extreme spikes don't pathologically shorten TTL.
                DateTime adjustedExpiry = signal.ExpiresAt;
                if (atrRatio is double r2)
                {
                    double ttlMult = Math.Clamp(r2, 0.3, 3.0);
                    TimeSpan originalDuration = signal.ExpiresAt - DateTime.UtcNow;
                    if (originalDuration > TimeSpan.Zero)
                        adjustedExpiry = DateTime.UtcNow.Add(TimeSpan.FromTicks((long)(originalDuration.Ticks * ttlMult)));
                }

                // ── Adaptive TTL across market closures ─────────────────────────
                // Without this, a signal generated late Friday or during the
                // weekend closure would typically expire before Monday's open
                // and contribute nothing but noise. When the market is closed
                // and the current expiry would fall during that closure, extend
                // to <NextMarketOpen> + MarketClosedGracePeriodMinutes so the
                // signal has a real chance to fill on reopen.
                if (_options.AdaptiveSignalTtlEnabled)
                {
                    var nowUtc = DateTime.UtcNow;
                    if (_marketHoursCalendar.IsMarketClosed(signal.Symbol, nowUtc))
                    {
                        var reopen = _marketHoursCalendar.NextMarketOpen(signal.Symbol, nowUtc);
                        var graceExpiry = reopen.AddMinutes(Math.Max(0, _options.MarketClosedGracePeriodMinutes));
                        if (adjustedExpiry < graceExpiry)
                        {
                            _logger.LogInformation(
                                "StrategyWorker: market closed for {Symbol} at {Now:u} — extending signal expiry {OldExpiry:u} → {NewExpiry:u} (reopen {Reopen:u} + {Grace}m grace)",
                                signal.Symbol, nowUtc, adjustedExpiry, graceExpiry, reopen, _options.MarketClosedGracePeriodMinutes);
                            adjustedExpiry = graceExpiry;
                            _metrics.SignalTtlExtendedMarketClosed.Add(1,
                                new KeyValuePair<string, object?>("symbol", signal.Symbol));
                        }
                    }
                }

                // ── Collect candidate signal for conflict resolution ─────────
                // Instead of persisting immediately, add to the candidate bag.
                // After all strategies evaluate, the conflict resolver will pick
                // the best signal per symbol and suppress opposing-direction conflicts.
                strategySharpeCache.TryGetValue(strategy.Id, out var stratSharpe);
                candidateSignals.Add((
                    Pending: new PendingSignal(
                        StrategyId:           signal.StrategyId,
                        Symbol:               signal.Symbol,
                        Timeframe:            strategy.Timeframe,
                        StrategyType:         strategy.StrategyType,
                        Direction:            signal.Direction,
                        EntryPrice:           signal.EntryPrice,
                        StopLoss:             signal.StopLoss,
                        TakeProfit:           signal.TakeProfit,
                        SuggestedLotSize:     adjustedLotSize,
                        Confidence:           signal.Confidence,
                        MLConfidenceScore:    mlScore.ConfidenceScore,
                        MLModelId:            mlScore.MLModelId,
                        EstimatedCapacityLots: strategy.EstimatedCapacityLots,
                        StrategySharpeRatio:  stratSharpe != 0 ? stratSharpe : null,
                        ExpiresAt:            adjustedExpiry),
                    MlScore: mlScore,
                    MlScoringLatencyMs: mlScoringLatencyMs));

                // Reset all circuit breaker state on any successful evaluation (no exception).
                // Previously this only reset failure counters and deferred the full circuit
                // breaker reset until post-conflict-resolution, but that caused strategies
                // to become permanently broken when they consistently lost conflict resolution
                // despite healthy evaluations.
                _consecutiveFailures.TryRemove(strategy.Id, out _);
                _halfOpenProbeFailures.TryRemove(strategy.Id, out _);
                _circuitOpenedAt.TryRemove(strategy.Id, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating strategy {StrategyId} for {Symbol}", strategy.Id, @event.Symbol);
                _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("strategy_id", strategy.Id));

                // Distinguish half-open probe failures from regular failures.
                // When the circuit is already open, this is a probe attempt — track it
                // separately so probe failures don't inflate _consecutiveFailures.
                if (_circuitOpenedAt.ContainsKey(strategy.Id))
                {
                    _halfOpenProbeFailures.AddOrUpdate(strategy.Id, 1, (_, c) => c + 1);
                    _circuitOpenedAt[strategy.Id] = DateTime.UtcNow;
                }
                else
                {
                    var newCount = _consecutiveFailures.AddOrUpdate(strategy.Id, 1, (_, count) => count + 1);
                    if (newCount >= _options.MaxConsecutiveFailures)
                        _circuitOpenedAt[strategy.Id] = DateTime.UtcNow;
                }
                _dirtyStrategyIds[strategy.Id] = 1;

                // Dead-letter audit trail for failed signal evaluations
                try
                {
                    using var dlScope = _scopeFactory.CreateScope();
                    var deadLetterSink = dlScope.ServiceProvider.GetRequiredService<IDeadLetterSink>();
                    await deadLetterSink.WriteAsync(
                        handlerName: "StrategyWorker",
                        eventType: "StrategyEvaluation",
                        eventPayloadJson: $"{{\"strategyId\":{strategy.Id},\"symbol\":\"{strategy.Symbol}\"}}",
                        errorMessage: $"Strategy {strategy.Id} evaluation failed: {ex.Message}",
                        stackTrace: ex.ToString(),
                        attempts: _consecutiveFailures.GetValueOrDefault(strategy.Id, 1),
                        ct);
                }
                catch
                {
                    // Best-effort dead-letter; don't fail the loop — but track the drop
                    _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("reason", "dead_letter_write_failed"));
                }
            }
            finally
            {
                evalStopwatch.Stop();
                _metrics.StrategyEvaluationMs.Record(
                    evalStopwatch.Elapsed.TotalMilliseconds,
                    new("symbol", strategy.Symbol),
                    new("strategy_type", strategy.StrategyType.ToString()));
            }
        });

        // ══════════════════════════════════════════════════════════════════════
        //  Post-loop: Signal conflict resolution and publishing
        // ══════════════════════════════════════════════════════════════════════
        // At this point, all strategies have been evaluated in parallel and their
        // candidate signals collected. Before persisting any of them, the conflict
        // resolver applies two rules:
        //   1. Opposing direction suppression: if Strategy A says BUY EURUSD and
        //      Strategy B says SELL EURUSD, only the higher-priority signal survives.
        //      Priority is scored by: Sharpe ratio > ML confidence > estimated capacity.
        //   2. Same-direction deduplication: multiple strategies generating the same
        //      direction on the same symbol are deduplicated — only the highest-priority
        //      signal is persisted to avoid redundant orders.
        if (candidateSignals.IsEmpty)
            return;

        var allCandidates = candidateSignals.ToList();
        var pendingOnly = allCandidates.Select(c => c.Pending).ToList();
        var winners = _signalConflictResolver.Resolve(pendingOnly);
        // Use reference equality (not value equality) to match exact winner instances.
        // A strategy could theoretically produce multiple candidates on different timeframes,
        // and the resolver may keep some but not all — StrategyId alone can't distinguish them.
        // ReferenceEqualityComparer ensures we match the exact PendingSignal objects the
        // resolver returned, not just objects with the same property values.
        var winnerSet = new HashSet<PendingSignal>(winners, ReferenceEqualityComparer.Instance);

        // Log suppressed signals
        var suppressedCount = allCandidates.Count - winners.Count;
        if (suppressedCount > 0)
        {
            _logger.LogInformation(
                "SignalConflictResolver: {Total} candidates → {Winners} winners, {Suppressed} suppressed for {Symbol}",
                allCandidates.Count, winners.Count, suppressedCount, @event.Symbol);
            _metrics.SignalsFiltered.Add(suppressedCount, new("symbol", @event.Symbol), new("stage", "conflict_resolution"));

            // Emit one audit row per suppressed candidate so operators can see which
            // strategies lost the tick-level conflict even when only the winner's
            // StrategyId appears on the persisted TradeSignal row.
            foreach (var loser in allCandidates.Where(c => !winnerSet.Contains(c.Pending)))
            {
                await _rejectionAuditor.RecordAsync("ConflictResolution", "suppressed_by_conflict",
                    @event.Symbol, nameof(StrategyWorker),
                    strategyId: loser.Pending.StrategyId,
                    detail: $"Lost tick-level conflict resolution on {@event.Symbol}/{loser.Pending.Timeframe} {loser.Pending.Direction}",
                    ct: _stoppingToken);
            }
        }

        // Persist and publish winning signals concurrently. Each winner gets its own DI scope
        // because CreateTradeSignalCommand writes to the DB and SaveAndPublish emits an
        // integration event — both require scoped write contexts with independent transactions.
        var winnerCandidates = allCandidates.Where(c => winnerSet.Contains(c.Pending)).ToList();

        // Build StrategyId → (Status, Entity) lookup so the publish loop can route
        // Approved-but-Paused winners to the paper pipeline instead of creating a real
        // TradeSignal. The lookup is built once from the already-loaded strategies list.
        var strategyById = strategies.ToDictionary(s => s.Id);

        var publishOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(winnerCandidates.Count, 4),
            CancellationToken = _stoppingToken
        };
        await Parallel.ForEachAsync(winnerCandidates, publishOptions, async (candidate, ct) =>
        {
            var (pending, mlScore, mlScoringLatencyMs) = candidate;
            using var publishScope = _scopeFactory.CreateScope();
            var mediator     = publishScope.ServiceProvider.GetRequiredService<IMediator>();
            var writeContext  = publishScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var eventService = publishScope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

            // Multi-timeframe confirmation gate: reject the signal when the next-higher
            // timeframe regime is hostile. Hot-reloadable via MultiTimeframeConfirmation:Enabled.
            // Applies to both live and paper branches so paper-fill data stays honest.
            var mtfFilter = publishScope.ServiceProvider
                .GetService<Strategies.Services.IMultiTimeframeConfirmationFilter>();
            if (mtfFilter is not null)
            {
                var mtf = await mtfFilter.CheckAsync(
                    pending.Symbol, pending.Timeframe, pending.Direction, DateTime.UtcNow, ct);
                if (!mtf.Allowed)
                {
                    _logger.LogDebug(
                        "StrategyWorker: MTF confirmation rejected signal — {Reason}", mtf.RejectionReason);
                    _metrics.SignalsFiltered.Add(1,
                        new("symbol", pending.Symbol),
                        new("stage", "mtf_confirmation"));
                    return;
                }
            }

            // Regime-archetype compatibility gate: reject signals whose strategy type
            // isn't in the current regime's compatible list per IRegimeStrategyMapper.
            // Stops trend-followers firing in Ranging, mean-reversion firing in Trending,
            // anyone firing in Crisis. Hot-reloadable via RegimeArchetypeGate:Enabled.
            var regimeGate = publishScope.ServiceProvider
                .GetService<Strategies.Services.IRegimeArchetypeGateFilter>();
            if (regimeGate is not null)
            {
                var rg = await regimeGate.CheckAsync(
                    pending.Symbol, pending.Timeframe, pending.StrategyType, DateTime.UtcNow, ct);
                if (!rg.Allowed)
                {
                    _logger.LogDebug(
                        "StrategyWorker: regime-archetype gate rejected signal — {Reason}", rg.RejectionReason);
                    _metrics.SignalsFiltered.Add(1,
                        new("symbol", pending.Symbol),
                        new("stage", "regime_archetype"));
                    return;
                }
            }

            // Paper-mode branch: approved-but-not-active strategies route their signals
            // through PaperExecutionRouter instead of creating a real TradeSignal. No
            // integration event is published (no EA consumer) and no real Order follows.
            if (strategyById.TryGetValue(pending.StrategyId, out var owningStrategy)
                && owningStrategy.Status == StrategyStatus.Paused
                && owningStrategy.LifecycleStage == StrategyLifecycleStage.Approved)
            {
                var paperRouter = publishScope.ServiceProvider
                    .GetRequiredService<PaperTrading.Services.IPaperExecutionRouter>();
                await paperRouter.EnqueueAsync(owningStrategy,
                    new PaperTrading.Services.PaperSignalIntent(
                        Direction:            pending.Direction,
                        RequestedEntryPrice:  pending.EntryPrice,
                        LotSize:              pending.SuggestedLotSize > 0 ? pending.SuggestedLotSize : 0.01m,
                        StopLoss:             pending.StopLoss,
                        TakeProfit:           pending.TakeProfit,
                        GeneratedAtUtc:       DateTime.UtcNow,
                        TradeSignalId:        null),
                    (owningStrategy.Symbol is var sym ? @event.Bid : 0m,
                     @event.Ask),
                    ct);
                _metrics.SignalsGenerated.Add(1,
                    new("symbol", pending.Symbol),
                    new("strategy_type", pending.StrategyType.ToString()),
                    new("mode", "paper"));
                return;
            }

            var result = await mediator.Send(new CreateTradeSignalCommand
            {
                StrategyId             = pending.StrategyId,
                Symbol                 = pending.Symbol,
                Direction              = pending.Direction.ToString(),
                EntryPrice             = pending.EntryPrice,
                StopLoss               = pending.StopLoss,
                TakeProfit             = pending.TakeProfit,
                SuggestedLotSize       = pending.SuggestedLotSize,
                Confidence             = pending.Confidence,
                MLPredictedDirection   = mlScore.PredictedDirection?.ToString(),
                MLPredictedMagnitude   = mlScore.PredictedMagnitudePips,
                MLConfidenceScore      = mlScore.ConfidenceScore,
                MLModelId              = mlScore.MLModelId,
                MLRawProbability       = mlScore.RawProbability,
                MLCalibratedProbability = mlScore.CalibratedProbability,
                MLServedCalibratedProbability = mlScore.ServedCalibratedProbability,
                MLDecisionThresholdUsed = mlScore.DecisionThresholdUsed,
                MLEnsembleDisagreement = mlScore.EnsembleDisagreement,
                MLScoringLatencyMs     = mlScoringLatencyMs,
                Timeframe              = pending.Timeframe,
                ExpiresAt              = pending.ExpiresAt
            }, ct);

            if (result.status && result.data > 0)
            {
                await eventService.SaveAndPublish(writeContext, new TradeSignalCreatedIntegrationEvent
                {
                    TradeSignalId = result.data,
                    StrategyId    = pending.StrategyId,
                    Symbol        = pending.Symbol,
                    Direction     = pending.Direction.ToString(),
                    EntryPrice    = pending.EntryPrice
                });

                // Record cooldown timestamp (TryAdd avoids overwriting a timestamp
                // set by a parallel winner for the same strategy on a different timeframe)
                var now = DateTime.UtcNow;
                _lastSignalTime.AddOrUpdate(pending.StrategyId, now, (_, existing) => existing >= now ? existing : now);
                _dirtyStrategyIds[pending.StrategyId] = 1;

                _metrics.SignalsGenerated.Add(1, new("symbol", pending.Symbol), new("strategy_type", pending.StrategyType.ToString()));

                await mediator.Send(new LogDecisionCommand
                {
                    EntityType   = "TradeSignal",
                    EntityId     = result.data,
                    DecisionType = "SignalGenerated",
                    Outcome      = "Created",
                    Reason       = $"Strategy {pending.StrategyId} generated {pending.Direction} signal on {pending.Symbol} at {pending.EntryPrice}, " +
                                   $"SL={pending.StopLoss:F5}, TP={pending.TakeProfit:F5}, Confidence={pending.Confidence:P2}" +
                                   (suppressedCount > 0 ? $" (won conflict resolution over {suppressedCount} candidates)" : ""),
                    Source       = "StrategyWorker"
                }, ct);
            }
        });
    }

    // ── Expiry sweep, backtest gates, config helper, and Dispose are in
    //    StrategyWorker.Maintenance.cs and StrategyWorker.Gates.cs ──
}
