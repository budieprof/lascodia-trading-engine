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
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using LascodiaTradingEngine.Application.Services;
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
    private readonly ISignalConflictResolver _signalConflictResolver;
    private readonly RegimeCoherenceChecker _regimeCoherenceChecker;

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
        TradingMetrics metrics,
        ISignalConflictResolver signalConflictResolver,
        RegimeCoherenceChecker regimeCoherenceChecker)
    {
        _logger                  = logger;
        _scopeFactory            = scopeFactory;
        _eventBus                = eventBus;
        _evaluators              = evaluators;
        _distributedLock         = distributedLock;
        _options                 = options;
        _metrics                 = metrics;
        _signalConflictResolver  = signalConflictResolver;
        _regimeCoherenceChecker  = regimeCoherenceChecker;
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

            foreach (var s in sharpeData)
                strategySharpeCache[s.StrategyId] = s.SharpeRatio;
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
        // have been evaluated. This enables cross-strategy conflict detection.
        var candidateSignals = new ConcurrentBag<(PendingSignal Pending, MLScoreResult MlScore)>();

        // ── Parallel strategy evaluation ─────────────────────────────────────
        // Each strategy gets its own DI scope so scoped services (DbContext, ML scorer,
        // filters) have independent lifetimes. The pre-fetched regimeCache,
        // criticalStrategyIds, and backtestQualifiedIds are read-only and safe to share.
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

                // Reset failure counter on any successful evaluation (no exception).
                // Circuit breaker is NOT reset here — it is only reset after the signal
                // survives conflict resolution and is actually persisted, to avoid
                // prematurely exiting half-open state for strategies that keep losing
                // conflict resolution.
                _consecutiveFailures.TryRemove(strategy.Id, out _);
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

        // ══════════════════════════════════════════════════════════════════════
        //  Post-loop: Signal conflict resolution and publishing
        // ══════════════════════════════════════════════════════════════════════
        // Now that all strategies have been evaluated in parallel, resolve
        // cross-strategy conflicts (opposing directions suppressed, same-direction
        // deduplication by priority score) before persisting any signals.
        if (candidateSignals.IsEmpty)
            return;

        var allCandidates = candidateSignals.ToList();
        var pendingOnly = allCandidates.Select(c => c.Pending).ToList();
        var winners = _signalConflictResolver.Resolve(pendingOnly);
        // Use reference equality to match exact winner instances, not just StrategyId.
        // A strategy could produce multiple candidates (e.g. different timeframes), and
        // the resolver may keep some but not all — StrategyId alone can't distinguish them.
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

        // Persist and publish winning signals concurrently (each gets its own DI scope)
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

                // Signal survived conflict resolution and was persisted — fully reset circuit breaker
                _circuitOpenedAt.TryRemove(pending.StrategyId, out _);

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

    // ════════════════════════════════════════════════════════════════════════════
    //  Backtest qualification gate
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the set of strategy IDs that have at least one completed backtest meeting
    /// minimum quality thresholds. Strategies not in this set are blocked from generating
    /// live signals.
    ///
    /// <para>
    /// Configurable via <see cref="Domain.Entities.EngineConfig"/>:
    /// <list type="bullet">
    ///   <item><c>Backtest:Gate:Enabled</c>        — master switch (default true)</item>
    ///   <item><c>Backtest:Gate:MinWinRate</c>            — minimum win rate (default 0.60 = 60%)</item>
    ///   <item><c>Backtest:Gate:MinProfitFactor</c>       — minimum profit factor; must be &gt; 1.0 (default 1.0)</item>
    ///   <item><c>Backtest:Gate:MinTotalTrades</c>        — fallback min trades (default 5)</item>
    ///   <item><c>Backtest:Gate:MinTotalTrades:M5M15</c>  — min trades for M1/M5/M15 (default 10)</item>
    ///   <item><c>Backtest:Gate:MinTotalTrades:H1</c>     — min trades for H1 (default 5)</item>
    ///   <item><c>Backtest:Gate:MinTotalTrades:H4</c>     — min trades for H4 (default 5)</item>
    ///   <item><c>Backtest:Gate:MinTotalTrades:D1</c>     — min trades for D1 (default 3)</item>
    ///   <item><c>Backtest:Gate:MaxDrawdownPct</c>        — maximum drawdown allowed (default 0.25 = 25%)</item>
    ///   <item><c>Backtest:Gate:MinSharpe</c>             — minimum Sharpe ratio (default 0.0)</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<HashSet<long>> GetBacktestQualifiedStrategyIdsAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        List<long> strategyIds,
        CancellationToken ct)
    {
        // Check if the gate is enabled
        bool gateEnabled = await GetConfigAsync<bool>(ctx, "Backtest:Gate:Enabled", true, ct);
        if (!gateEnabled)
        {
            // Gate disabled — all strategies are qualified
            return new HashSet<long>(strategyIds);
        }

        // Load qualification thresholds from EngineConfig (hot-reloadable)
        // Only profitable, winning strategies should qualify.
        double minWinRate      = await GetConfigAsync<double>(ctx, "Backtest:Gate:MinWinRate",      0.60, ct);
        double minProfitFactor = await GetConfigAsync<double>(ctx, "Backtest:Gate:MinProfitFactor", 1.0,  ct);
        double maxDrawdownPct  = await GetConfigAsync<double>(ctx, "Backtest:Gate:MaxDrawdownPct",  0.25, ct);
        double minSharpe       = await GetConfigAsync<double>(ctx, "Backtest:Gate:MinSharpe",       0.0,  ct);

        // Timeframe-adaptive MinTotalTrades: higher timeframes produce fewer signals,
        // so they need a lower trade threshold to avoid permanently blocking profitable
        // H4/D1 strategies that only generate 5-8 trades in a 365-day backtest window.
        int minTradesDefault = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades", 5, ct);
        int minTradesM5M15   = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:M5M15", 10, ct);
        int minTradesH1      = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:H1",    5,  ct);
        int minTradesH4      = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:H4",    5,  ct);
        int minTradesD1      = await GetConfigAsync<int>(ctx, "Backtest:Gate:MinTotalTrades:D1",    3,  ct);

        // Build a lookup of strategy timeframes for adaptive trade thresholds
        var strategyTimeframes = await ctx.Set<Domain.Entities.Strategy>()
            .Where(s => strategyIds.Contains(s.Id) && !s.IsDeleted)
            .Select(s => new { s.Id, s.Timeframe })
            .ToListAsync(ct);
        var timeframeMap = strategyTimeframes.ToDictionary(s => s.Id, s => s.Timeframe);

        // Load the most recent completed backtest per strategy (only for strategies in scope)
        var recentBacktests = await ctx.Set<Domain.Entities.BacktestRun>()
            .Where(r => strategyIds.Contains(r.StrategyId)
                        && r.Status == RunStatus.Completed
                        && !r.IsDeleted
                        && r.ResultJson != null)
            .GroupBy(r => r.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                ResultJson = g.OrderByDescending(r => r.CompletedAt).First().ResultJson
            })
            .ToListAsync(ct);

        var qualifiedIds = new HashSet<long>();

        foreach (var bt in recentBacktests)
        {
            if (string.IsNullOrWhiteSpace(bt.ResultJson))
                continue;

            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<BacktestResult>(bt.ResultJson);
                if (result is null) continue;

                // Resolve timeframe-adaptive MinTotalTrades
                int minTotalTrades = minTradesDefault;
                if (timeframeMap.TryGetValue(bt.StrategyId, out var tf))
                {
                    minTotalTrades = tf switch
                    {
                        Timeframe.M1 or Timeframe.M5 or Timeframe.M15 => minTradesM5M15,
                        Timeframe.H1  => minTradesH1,
                        Timeframe.H4  => minTradesH4,
                        Timeframe.D1  => minTradesD1,
                        _             => minTradesDefault,
                    };
                }

                bool meetsMinTrades  = result.TotalTrades >= minTotalTrades;
                bool meetsWinRate    = (double)result.WinRate >= minWinRate;
                bool meetsPF         = (double)result.ProfitFactor >= minProfitFactor;
                bool meetsDrawdown   = (double)result.MaxDrawdownPct <= maxDrawdownPct;
                bool meetsSharpe     = (double)result.SharpeRatio >= minSharpe;

                if (meetsMinTrades && meetsWinRate && meetsPF && meetsDrawdown && meetsSharpe)
                {
                    qualifiedIds.Add(bt.StrategyId);
                }
                else
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Tf}): backtest did not meet qualification — " +
                        "trades={Trades}/{MinTrades} winRate={WR:P1}/{MinWR:P1} " +
                        "pf={PF:F2}/{MinPF:F2} dd={DD:P1}/{MaxDD:P1} sharpe={S:F2}/{MinS:F2}",
                        bt.StrategyId, tf,
                        result.TotalTrades, minTotalTrades,
                        (double)result.WinRate, minWinRate,
                        (double)result.ProfitFactor, minProfitFactor,
                        (double)result.MaxDrawdownPct, maxDrawdownPct,
                        (double)result.SharpeRatio, minSharpe);
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Strategy {Id}: failed to deserialise backtest ResultJson — treating as unqualified",
                    bt.StrategyId);
            }
        }

        return qualifiedIds;
    }

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<Domain.Entities.EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    public override void Dispose()
    {
        _expirySweepTimer?.Dispose();
        _expirySweepLock.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
