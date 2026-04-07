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
public partial class StrategyWorker : BackgroundService, IIntegrationEventHandler<PriceUpdatedIntegrationEvent>
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
        RegimeCoherenceChecker regimeCoherenceChecker)
    {
        _logger                  = logger;
        _scopeFactory            = scopeFactory;    // Creates per-event/per-strategy DI scopes
        _eventBus                = eventBus;         // Event bus for subscribing to PriceUpdatedIntegrationEvent
        _evaluators              = evaluators;       // All registered IStrategyEvaluator implementations (one per StrategyType)
        _distributedLock         = distributedLock;  // Prevents concurrent evaluation of the same strategy
        _options                 = options;          // Configurable thresholds (cooldowns, circuit breaker, regime blocking, etc.)
        _metrics                 = metrics;          // OpenTelemetry metrics for observability
        _signalConflictResolver  = signalConflictResolver;  // Resolves opposing/duplicate signals from competing strategies
        _regimeCoherenceChecker  = regimeCoherenceChecker;  // Cross-timeframe regime alignment scoring
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

        // Subscribe to the event bus so Handle() is invoked on every price tick
        _eventBus.Subscribe<PriceUpdatedIntegrationEvent, StrategyWorker>();

        // Start the periodic sweep that expires stale pending trade signals
        _expirySweepTimer = new Timer(RunExpirySweep, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        // Clean up subscriptions and timer when the host signals shutdown
        stoppingToken.Register(() =>
        {
            _expirySweepTimer?.Dispose();
            _tickChannel.Writer.TryComplete();
            _eventBus.Unsubscribe<PriceUpdatedIntegrationEvent, StrategyWorker>();
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
        if (_options.NewsBlackoutMinutesBefore > 0 || _options.NewsBlackoutMinutesAfter > 0)
        {
            bool safeToTrade = await newsFilter.IsSafeToTradeAsync(
                @event.Symbol, @event.Timestamp,
                _options.NewsBlackoutMinutesBefore, _options.NewsBlackoutMinutesAfter,
                _stoppingToken);

            if (!safeToTrade)
            {
                _logger.LogInformation(
                    "StrategyWorker: news blackout active for {Symbol} — skipping tick",
                    @event.Symbol);
                return;
            }
        }

        // Load all active, non-deleted strategies whose symbol matches the price tick.
        // Each strategy will be independently evaluated through the filter pipeline.
        var strategies = await context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .Where(x => x.Symbol == @event.Symbol && x.Status == StrategyStatus.Active && !x.IsDeleted)
            .ToListAsync(_stoppingToken);

        // ── Pre-fetch strategy health snapshots ─────────────────────────────
        // Filter out strategies with Critical health status to prevent evaluating
        // strategies that the StrategyHealthWorker has flagged as unhealthy.
        var strategyIds = strategies.Select(s => s.Id).ToList();
        var criticalStrategyIds = new HashSet<long>();
        if (strategyIds.Count > 0)
        {
            // Get the latest health snapshot per strategy using a grouped query
            var latestSnapshotsCriticalStrategyIds = await context.GetDbContext()
                .Set<Domain.Entities.StrategyPerformanceSnapshot>()
                .Where(x => strategyIds.Contains(x.StrategyId) && !x.IsDeleted)
                .GroupBy(x => x.StrategyId)
                .Select(g => new
                {
                    StrategyId = g.Key,
                    HealthStatus = g.OrderByDescending(x => x.EvaluatedAt).First().HealthStatus
                })
                .Where(x => x.HealthStatus == StrategyHealthStatus.Critical)
                .Select(x => x.StrategyId)
                .ToListAsync(_stoppingToken);

            criticalStrategyIds = [.. latestSnapshotsCriticalStrategyIds];
        }

        // ── Pre-fetch regime snapshots for all distinct timeframes ───────────
        // Hoisted above the loop so that multiple strategies sharing the same
        // symbol/timeframe don't each trigger a separate DB query.
        var regimeCache = new Dictionary<Timeframe, MarketRegimeEnum>();
        if (_options.BlockedRegimes.Count > 0)
        {
            var timeframes = strategies.Select(s => s.Timeframe).Distinct();
            foreach (var tf in timeframes)
            {
                var symbolLatestRegimePerTimeframe = await context.GetDbContext()
                    .Set<Domain.Entities.MarketRegimeSnapshot>()
                    .Where(x => x.Symbol == @event.Symbol && x.Timeframe == tf && !x.IsDeleted)
                    .OrderByDescending(x => x.DetectedAt)
                    .Select(x => x.Regime)
                    .FirstOrDefaultAsync(_stoppingToken);
                regimeCache[tf] = symbolLatestRegimePerTimeframe;
            }
        }

        // ── Backtest qualification gate ────────────────────────────────────
        // Strategies must have at least one successful backtest that meets minimum
        // quality thresholds before they can generate live signals. This prevents
        // untested or poorly-performing strategies from producing real trades.
        var backtestQualifiedIds = await GetBacktestQualifiedStrategyIdsAsync(
            context.GetDbContext(), strategyIds, _stoppingToken);

        // ── Pre-fetch Sharpe ratios for conflict resolution scoring ──────────
        // Used by the SignalConflictResolver to rank competing signals from the
        // same symbol. Fetched once before the parallel loop to avoid N+1 queries.
        var strategySharpeCache = new Dictionary<long, decimal>();
        if (strategyIds.Count > 0)
        {
            var sharpeData = await context.GetDbContext()
                .Set<Domain.Entities.StrategyPerformanceSnapshot>()
                .Where(x => strategyIds.Contains(x.StrategyId) && !x.IsDeleted)
                .GroupBy(x => x.StrategyId)
                .Select(g => new
                {
                    StrategyId = g.Key,
                    SharpeRatio = g.OrderByDescending(x => x.EvaluatedAt).First().SharpeRatio
                })
                .ToListAsync(_stoppingToken);

            strategySharpeCache = sharpeData.ToDictionary(s => s.StrategyId, s => s.SharpeRatio);

            //foreach (var s in sharpeData)
            //    strategySharpeCache[s.StrategyId] = s.SharpeRatio;
        }

        // ── Regime coherence check (cross-timeframe alignment) ───────────────
        // If regimes across H1/H4/D1 disagree for this symbol, the coherence
        // score is low and all signals for the symbol may be suppressed.
        decimal regimeCoherence = await _regimeCoherenceChecker.GetCoherenceScoreAsync(
            @event.Symbol, _stoppingToken);

        if (_options.MinRegimeCoherence > 0 && regimeCoherence < _options.MinRegimeCoherence)
        {
            _logger.LogInformation(
                "StrategyWorker: regime coherence for {Symbol} is {Coherence:F2} (< {Threshold:F2}) — suppressing all signals",
                @event.Symbol, regimeCoherence, _options.MinRegimeCoherence);
            _metrics.SignalsFiltered.Add(1, new("symbol", @event.Symbol), new("stage", "regime_coherence"));
            return;
        }

        // ── Candidate signal collection bag ──────────────────────────────────
        // Instead of publishing signals immediately inside the parallel loop,
        // we collect candidates here and resolve conflicts after all strategies
        // have been evaluated. This two-phase approach (collect → resolve → publish)
        // enables cross-strategy conflict detection: e.g. if Strategy A says BUY EURUSD
        // and Strategy B says SELL EURUSD on the same tick, the conflict resolver picks
        // the winner based on Sharpe ratio, ML confidence, and estimated capacity.
        var candidateSignals = new ConcurrentBag<(PendingSignal Pending, MLScoreResult MlScore)>();

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
                if (_options.SignalCooldownSeconds > 0 &&
                    _lastSignalTime.TryGetValue(strategy.Id, out var lastTime) &&
                    (DateTime.UtcNow - lastTime).TotalSeconds < _options.SignalCooldownSeconds)
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}): cooldown active — {Remaining:F0}s remaining",
                        strategy.Id, strategy.Symbol,
                        _options.SignalCooldownSeconds - (DateTime.UtcNow - lastTime).TotalSeconds);
                    _metrics.SignalCooldownSkips.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                    return;
                }

                // ── Concurrency guard ─────────────────────────────────────────
                // Prevent duplicate evaluation if price events arrive faster than
                // evaluation completes — only one evaluation per strategy at a time.
                var lockKey = $"strategy:eval:{strategy.Id}";
                var lockTimeout = TimeSpan.FromSeconds(_options.LockTimeoutSeconds);
                await using var evalLock = await _distributedLock.TryAcquireAsync(lockKey, lockTimeout, ct);
                if (evalLock is null)
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}) evaluation already in progress — skipping this tick",
                        strategy.Id, strategy.Symbol);
                    _metrics.TicksDroppedLockBusy.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                    return;
                }

                // Resolve the strategy-specific evaluator by matching the StrategyType enum.
                // Each evaluator implements the trading logic for one strategy type:
                //   - BreakoutScalperEvaluator → StrategyType.BreakoutScalper
                //   - MovingAverageCrossoverEvaluator → StrategyType.MovingAverageCrossover
                //   - RSIReversionEvaluator → StrategyType.RSIReversion
                // If no evaluator is registered, the strategy is skipped and a decision log is created.
                var evaluator = _evaluators.FirstOrDefault(e => e.StrategyType == strategy.StrategyType);
                if (evaluator is null)
                {
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

                // Skip if no live price is available in the cache
                var price = strategyCache.Get(strategy.Symbol);
                if (price is null) return;

                // ── Market regime filter (uses pre-fetched cache) ────────────
                if (regimeCache.TryGetValue(strategy.Timeframe, out var latestRegime) &&
                    _options.BlockedRegimes.Contains(latestRegime))
                {
                    _logger.LogInformation(
                        "Strategy {Id}: market regime {Regime} is blocked for {Symbol}/{Tf} — skipping",
                        strategy.Id, latestRegime, strategy.Symbol, strategy.Timeframe);

                    _metrics.SignalsFiltered.Add(1, new("symbol", strategy.Symbol), new("stage", "regime"));

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

                // ── Regime-conditional parameter swap ────────────────────────────────
                // If the OptimizationWorker has stored regime-specific parameters for
                // the current market regime, apply them for this evaluation. We create
                // a shallow copy to avoid dirtying the tracked entity in the outer
                // DbContext's change tracker.
                var evalStrategy = strategy;
                if (regimeCache.TryGetValue(strategy.Timeframe, out var currentRegime))
                {
                    var regimeParams = await strategyContext.GetDbContext()
                        .Set<Domain.Entities.StrategyRegimeParams>()
                        .Where(p => p.StrategyId == strategy.Id
                                 && p.Regime == currentRegime
                                 && !p.IsDeleted)
                        .Select(p => p.ParametersJson)
                        .FirstOrDefaultAsync(ct);

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
                                "Strategy {Id}: regime params for {Regime} failed validation — using base params",
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
                            Id                      = strategy.Id,
                            Name                    = strategy.Name,
                            Description             = strategy.Description,
                            StrategyType            = strategy.StrategyType,
                            Symbol                  = strategy.Symbol,
                            Timeframe               = strategy.Timeframe,
                            ParametersJson          = regimeParams,
                            Status                  = strategy.Status,
                            RiskProfileId           = strategy.RiskProfileId,
                            CreatedAt               = strategy.CreatedAt,
                            LifecycleStage          = strategy.LifecycleStage,
                            LifecycleStageEnteredAt = strategy.LifecycleStageEnteredAt,
                            EstimatedCapacityLots   = strategy.EstimatedCapacityLots,
                            IsDeleted               = strategy.IsDeleted,
                        };
                        } // end regimeParamsValid else
                    }
                }

                // ── Gradual rollout parameter routing ──────────────────────────────
                // If an optimization rollout is in progress (RolloutPct < 100), route
                // traffic between old and new parameters deterministically based on a
                // seed derived from the tick timestamp. This ensures the same tick
                // always picks the same parameter set, enabling consistent A/B comparison.
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
                var mlScore = await strategyMlScorer.ScoreAsync(signal, candles, ct);

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

                // ── Collect candidate signal for conflict resolution ─────────
                // Instead of persisting immediately, add to the candidate bag.
                // After all strategies evaluate, the conflict resolver will pick
                // the best signal per symbol and suppress opposing-direction conflicts.
                strategySharpeCache.TryGetValue(strategy.Id, out var stratSharpe);
                candidateSignals.Add((
                    new PendingSignal(
                        StrategyId:           signal.StrategyId,
                        Symbol:               signal.Symbol,
                        Timeframe:            strategy.Timeframe,
                        StrategyType:         strategy.StrategyType,
                        Direction:            signal.Direction,
                        EntryPrice:           signal.EntryPrice,
                        StopLoss:             signal.StopLoss,
                        TakeProfit:           signal.TakeProfit,
                        SuggestedLotSize:     signal.SuggestedLotSize,
                        Confidence:           signal.Confidence,
                        MLConfidenceScore:    mlScore.ConfidenceScore,
                        MLModelId:            mlScore.MLModelId,
                        EstimatedCapacityLots: strategy.EstimatedCapacityLots,
                        StrategySharpeRatio:  stratSharpe != 0 ? stratSharpe : null,
                        ExpiresAt:            signal.ExpiresAt),
                    mlScore));

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
                var newCount = _consecutiveFailures.AddOrUpdate(strategy.Id, 1, (_, count) => count + 1);
                if (newCount >= _options.MaxConsecutiveFailures)
                {
                    // Track half-open probe failures separately so permanently broken
                    // strategies stop oscillating between open/half-open.
                    if (_circuitOpenedAt.ContainsKey(strategy.Id))
                        _halfOpenProbeFailures.AddOrUpdate(strategy.Id, 1, (_, c) => c + 1);
                    _circuitOpenedAt[strategy.Id] = DateTime.UtcNow;
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
        }

        // Persist and publish winning signals concurrently. Each winner gets its own DI scope
        // because CreateTradeSignalCommand writes to the DB and SaveAndPublish emits an
        // integration event — both require scoped write contexts with independent transactions.
        var winnerCandidates = allCandidates.Where(c => winnerSet.Contains(c.Pending)).ToList();
        var publishOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(winnerCandidates.Count, 4),
            CancellationToken = _stoppingToken
        };
        await Parallel.ForEachAsync(winnerCandidates, publishOptions, async (candidate, ct) =>
        {
            var (pending, mlScore) = candidate;
            using var publishScope = _scopeFactory.CreateScope();
            var mediator     = publishScope.ServiceProvider.GetRequiredService<IMediator>();
            var writeContext  = publishScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var eventService = publishScope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

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
                _lastSignalTime.AddOrUpdate(pending.StrategyId, now, (_, existing) => existing > now ? existing : now);

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
