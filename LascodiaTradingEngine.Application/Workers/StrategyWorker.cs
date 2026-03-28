using System.Collections.Concurrent;
using System.Diagnostics;
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
using LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Core signal-generation worker that reacts to live price ticks via the event bus.
/// For every <see cref="PriceUpdatedIntegrationEvent"/>, this worker:
/// <list type="number">
///   <item>Verifies at least one active EA instance owns the symbol (data availability).</item>
///   <item>Applies session and news blackout filters to decide whether trading is allowed.</item>
///   <item>Loads all active strategies matching the updated symbol.</item>
///   <item>Filters out strategies with Critical health status.</item>
///   <item>Evaluates each strategy using its registered <see cref="IStrategyEvaluator"/>.</item>
///   <item>Runs market regime, multi-timeframe confirmation, portfolio correlation, and Hawkes burst filters.</item>
///   <item>Scores the signal through the ML pipeline (<see cref="IMLSignalScorer"/>), including
///         abstention gating and suppression logic.</item>
///   <item>Creates a <see cref="Domain.Entities.TradeSignal"/> and publishes a
///         <see cref="TradeSignalCreatedIntegrationEvent"/> for downstream consumption
///         by <see cref="SignalOrderBridgeWorker"/>.</item>
/// </list>
/// Additionally, a background timer sweeps for expired pending/approved signals every 5 minutes.
/// <para>
/// <b>Thread safety:</b> A distributed lock per strategy prevents overlapping evaluations
/// when price events arrive faster than evaluation completes.
/// </para>
/// </summary>
public class StrategyWorker : BackgroundService, IIntegrationEventHandler<PriceUpdatedIntegrationEvent>
{
    private readonly ILogger<StrategyWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly IEnumerable<IStrategyEvaluator> _evaluators;
    private readonly IDistributedLock _distributedLock;
    private readonly StrategyEvaluatorOptions _options;
    private readonly TradingMetrics _metrics;

    /// <summary>Tracks the last signal creation time per strategy to enforce cooldown.</summary>
    private readonly ConcurrentDictionary<long, DateTime> _lastSignalTime = new();

    /// <summary>Tracks consecutive evaluation failures per strategy for circuit-breaking.</summary>
    private readonly ConcurrentDictionary<long, int> _consecutiveFailures = new();

    /// <summary>Tracks when the circuit breaker opened per strategy for half-open recovery.</summary>
    private readonly ConcurrentDictionary<long, DateTime> _circuitOpenedAt = new();

    /// <summary>Prevents concurrent expiry sweep executions from the timer.</summary>
    private readonly SemaphoreSlim _expirySweepLock = new(1, 1);

    /// <summary>Timer that periodically expires stale pending trade signals.</summary>
    private Timer? _expirySweepTimer;

    /// <summary>
    /// Captured from <see cref="ExecuteAsync"/> so that event-driven <see cref="Handle"/>
    /// calls can propagate host-shutdown cancellation to downstream services like
    /// <see cref="IMLSignalScorer.ScoreAsync"/>.
    /// </summary>
    private CancellationToken _stoppingToken = CancellationToken.None;

    public StrategyWorker(
        ILogger<StrategyWorker> logger,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IEnumerable<IStrategyEvaluator> evaluators,
        IDistributedLock distributedLock,
        StrategyEvaluatorOptions options,
        TradingMetrics metrics)
    {
        _logger          = logger;
        _scopeFactory    = scopeFactory;
        _eventBus        = eventBus;
        _evaluators      = evaluators;
        _distributedLock = distributedLock;
        _options         = options;
        _metrics         = metrics;
    }

    /// <summary>
    /// Subscribes to price-update events on the event bus and starts the signal expiry sweep timer.
    /// The worker is entirely event-driven — it does no polling of its own. The expiry sweep
    /// runs on a 5-minute interval to clean up pending signals that have exceeded their TTL.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
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
            _eventBus.Unsubscribe<PriceUpdatedIntegrationEvent, StrategyWorker>();
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles a price-update event from the event bus. This is the main entry point for
    /// the strategy evaluation pipeline. Each invocation processes all active strategies
    /// for the updated symbol through a multi-stage filter chain before creating signals.
    /// </summary>
    /// <param name="event">Contains the updated symbol, bid, and ask prices.</param>
    public async Task Handle(PriceUpdatedIntegrationEvent @event)
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
            var latestSnapshots = await context.GetDbContext()
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

            criticalStrategyIds = new HashSet<long>(latestSnapshots);
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
                var regime = await context.GetDbContext()
                    .Set<Domain.Entities.MarketRegimeSnapshot>()
                    .Where(x => x.Symbol == @event.Symbol && x.Timeframe == tf && !x.IsDeleted)
                    .OrderByDescending(x => x.DetectedAt)
                    .Select(x => x.Regime)
                    .FirstOrDefaultAsync(_stoppingToken);
                regimeCache[tf] = regime;
            }
        }

        // ── Parallel strategy evaluation ─────────────────────────────────────
        // Each strategy gets its own DI scope so scoped services (DbContext, ML scorer,
        // filters) have independent lifetimes. The pre-fetched regimeCache and
        // criticalStrategyIds are read-only and safe to share across iterations.
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

                // ── Per-strategy circuit breaker (with half-open recovery) ────
                if (_options.MaxConsecutiveFailures > 0 &&
                    _consecutiveFailures.TryGetValue(strategy.Id, out var failures) &&
                    failures >= _options.MaxConsecutiveFailures)
                {
                    // Half-open: allow a single probe after the recovery period
                    if (_options.CircuitBreakerRecoverySeconds > 0 &&
                        _circuitOpenedAt.TryGetValue(strategy.Id, out var openedAt) &&
                        (DateTime.UtcNow - openedAt).TotalSeconds >= _options.CircuitBreakerRecoverySeconds)
                    {
                        _logger.LogInformation(
                            "Strategy {Id} ({Symbol}): circuit breaker half-open — allowing probe evaluation after {Recovery}s",
                            strategy.Id, strategy.Symbol, _options.CircuitBreakerRecoverySeconds);
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
                await using var evalLock = await _distributedLock.TryAcquireAsync(lockKey, ct);
                if (evalLock is null)
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}) evaluation already in progress — skipping this tick",
                        strategy.Id, strategy.Symbol);
                    _metrics.TicksDroppedLockBusy.Add(1, new("symbol", strategy.Symbol), new("strategy_id", strategy.Id));
                    return;
                }

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

                // Run the strategy-specific evaluation logic (e.g., MA crossover, RSI reversion)
                // Returns null if no signal condition is met on this tick.
                var signal = await evaluator.EvaluateAsync(strategy, candles, (price.Value.Bid, price.Value.Ask), ct);
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
                var mlScore = await strategyMlScorer.ScoreAsync(signal, candles, ct);

                // ML suppression: when the scorer returns all nulls (cooldown, suppression,
                // or consensus failure), skip signal creation entirely.
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

                // ── Persist the trade signal via CQRS command ─────────────────
                var result = await strategyMediator.Send(new CreateTradeSignalCommand
                {
                    StrategyId             = signal.StrategyId,
                    Symbol                 = signal.Symbol,
                    Direction              = signal.Direction.ToString(),
                    EntryPrice             = signal.EntryPrice,
                    StopLoss               = signal.StopLoss,
                    TakeProfit             = signal.TakeProfit,
                    SuggestedLotSize       = signal.SuggestedLotSize,
                    Confidence             = signal.Confidence,
                    MLPredictedDirection   = signal.MLPredictedDirection?.ToString(),
                    MLPredictedMagnitude   = signal.MLPredictedMagnitude,
                    MLConfidenceScore      = signal.MLConfidenceScore,
                    MLModelId              = signal.MLModelId,
                    MLRawProbability       = mlScore.RawProbability,
                    MLCalibratedProbability = mlScore.CalibratedProbability,
                    MLDecisionThresholdUsed = mlScore.DecisionThresholdUsed,
                    MLEnsembleDisagreement = mlScore.EnsembleDisagreement,
                    Timeframe              = strategy.Timeframe,
                    ExpiresAt              = signal.ExpiresAt
                }, ct);

                // On successful creation, publish the integration event so
                // SignalOrderBridgeWorker can pick it up for risk checking and order creation.
                if (result.status && result.data > 0)
                {
                    // Resolve write context only when we actually need to publish
                    var writeContext = strategyScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var eventService = strategyScope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

                    await eventService.SaveAndPublish(writeContext, new TradeSignalCreatedIntegrationEvent
                    {
                        TradeSignalId = result.data,
                        StrategyId    = signal.StrategyId,
                        Symbol        = signal.Symbol,
                        Direction     = signal.Direction.ToString(),
                        EntryPrice    = signal.EntryPrice
                    });

                    // Record cooldown timestamp
                    _lastSignalTime[strategy.Id] = DateTime.UtcNow;

                    _metrics.SignalsGenerated.Add(1, new("symbol", signal.Symbol), new("strategy_type", strategy.StrategyType.ToString()));

                    await strategyMediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = result.data,
                        DecisionType = "SignalGenerated",
                        Outcome      = "Created",
                        Reason       = $"Strategy {strategy.Id} generated {signal.Direction} signal on {signal.Symbol} at {signal.EntryPrice}, " +
                                       $"SL={signal.StopLoss:F5}, TP={signal.TakeProfit:F5}, Confidence={signal.Confidence:P2}",
                        Source       = "StrategyWorker"
                    }, ct);
                }

                // Reset failure counter on any successful evaluation (no exception).
                // Circuit breaker is only fully reset when a signal is actually generated,
                // to avoid prematurely exiting half-open state on no-signal evaluations.
                _consecutiveFailures.TryRemove(strategy.Id, out _);
                if (signal is not null && result.status && result.data > 0)
                    _circuitOpenedAt.TryRemove(strategy.Id, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating strategy {StrategyId} for {Symbol}", strategy.Id, @event.Symbol);
                _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("strategy_id", strategy.Id));
                var newCount = _consecutiveFailures.AddOrUpdate(strategy.Id, 1, (_, count) => count + 1);
                if (newCount >= _options.MaxConsecutiveFailures)
                    _circuitOpenedAt.TryAdd(strategy.Id, DateTime.UtcNow);
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
    }

    /// <summary>
    /// Timer callback that sweeps for pending and approved trade signals whose <c>ExpiresAt</c>
    /// has passed. Expired signals are transitioned via <see cref="ExpireTradeSignalCommand"/>
    /// so they are no longer eligible for order creation by <see cref="SignalOrderBridgeWorker"/>.
    /// Runs on a fire-and-forget <see cref="Task.Run"/> to avoid blocking the timer thread.
    /// Processes up to <see cref="StrategyEvaluatorOptions.ExpirySweepBatchSize"/> signals per cycle.
    /// Protected by a <see cref="SemaphoreSlim"/> to prevent concurrent sweeps.
    /// </summary>
    private void RunExpirySweep(object? state)
    {
        _ = Task.Run(async () =>
        {
            if (_stoppingToken.IsCancellationRequested) return;

            // Skip if a previous sweep is still running
            if (!await _expirySweepLock.WaitAsync(0, _stoppingToken)) return;

            try
            {
                using var scope  = _scopeFactory.CreateScope();
                var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
                var context      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

                // Find pending and approved signals that have exceeded their time-to-live
                var expiredIds = await context.GetDbContext()
                    .Set<Domain.Entities.TradeSignal>()
                    .Where(x => (x.Status == TradeSignalStatus.Pending || x.Status == TradeSignalStatus.Approved)
                                && x.ExpiresAt < DateTime.UtcNow
                                && !x.IsDeleted)
                    .OrderBy(x => x.ExpiresAt)
                    .Take(_options.ExpirySweepBatchSize)
                    .Select(x => x.Id)
                    .ToListAsync(_stoppingToken);

                // Expire each signal individually via the CQRS command
                foreach (var id in expiredIds)
                {
                    if (_stoppingToken.IsCancellationRequested) break;
                    await mediator.Send(new ExpireTradeSignalCommand { Id = id }, _stoppingToken);
                }

                if (expiredIds.Count > 0)
                    _logger.LogInformation("Signal expiry sweep: expired {Count} signals", expiredIds.Count);

                // Purge stale cooldown entries for strategies that are no longer active.
                // This prevents the ConcurrentDictionary from growing unbounded over time.
                var activeStrategyIds = await context.GetDbContext()
                    .Set<Domain.Entities.Strategy>()
                    .Where(x => x.Status == StrategyStatus.Active && !x.IsDeleted)
                    .Select(x => x.Id)
                    .ToListAsync(_stoppingToken);

                var activeSet = new HashSet<long>(activeStrategyIds);
                foreach (var key in _lastSignalTime.Keys)
                {
                    if (!activeSet.Contains(key))
                        _lastSignalTime.TryRemove(key, out _);
                }
                foreach (var key in _consecutiveFailures.Keys)
                {
                    if (!activeSet.Contains(key))
                        _consecutiveFailures.TryRemove(key, out _);
                }
                foreach (var key in _circuitOpenedAt.Keys)
                {
                    if (!activeSet.Contains(key))
                        _circuitOpenedAt.TryRemove(key, out _);
                }
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — expected
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in signal expiry sweep");
                _metrics.WorkerErrors.Add(1, new("worker", "StrategyWorker"), new("operation", "expiry_sweep"));
            }
            finally
            {
                _expirySweepLock.Release();
            }
        }, CancellationToken.None);
    }

    public override void Dispose()
    {
        _expirySweepTimer?.Dispose();
        _expirySweepLock.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
