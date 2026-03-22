using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Core signal-generation worker that reacts to live price ticks via the event bus.
/// For every <see cref="PriceUpdatedIntegrationEvent"/>, this worker:
/// <list type="number">
///   <item>Applies session and news blackout filters to decide whether trading is allowed.</item>
///   <item>Loads all active strategies matching the updated symbol.</item>
///   <item>Evaluates each strategy using its registered <see cref="IStrategyEvaluator"/>.</item>
///   <item>Runs multi-timeframe confirmation, portfolio correlation, and Hawkes burst filters.</item>
///   <item>Scores the signal through the ML pipeline (<see cref="IMLSignalScorer"/>), including
///         abstention gating and suppression logic.</item>
///   <item>Creates a <see cref="Domain.Entities.TradeSignal"/> and publishes a
///         <see cref="TradeSignalCreatedIntegrationEvent"/> for downstream consumption
///         by <see cref="SignalOrderBridgeWorker"/>.</item>
/// </list>
/// Additionally, a background timer sweeps for expired pending signals every 5 minutes.
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

    /// <summary>Timer that periodically expires stale pending trade signals.</summary>
    private Timer? _expirySweepTimer;

    /// <summary>
    /// Captured from <see cref="ExecuteAsync"/> so that event-driven <see cref="Handle"/>
    /// calls can propagate host-shutdown cancellation to downstream services like
    /// <see cref="IMLSignalScorer.ScoreAsync"/>.
    /// </summary>
    private CancellationToken _stoppingToken;

    public StrategyWorker(
        ILogger<StrategyWorker> logger,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IEnumerable<IStrategyEvaluator> evaluators,
        IDistributedLock distributedLock,
        StrategyEvaluatorOptions options)
    {
        _logger          = logger;
        _scopeFactory    = scopeFactory;
        _eventBus        = eventBus;
        _evaluators      = evaluators;
        _distributedLock = distributedLock;
        _options         = options;
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
        // Create a fresh DI scope per event — the worker is a singleton but scoped
        // services (DbContext, risk checker, etc.) need per-invocation lifetimes.
        using var scope = _scopeFactory.CreateScope();
        var context      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
        var mlScorer     = scope.ServiceProvider.GetRequiredService<IMLSignalScorer>();
        var cache        = scope.ServiceProvider.GetRequiredService<ILivePriceCache>();
        var eventService = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

        // Resolve signal filters — each filter acts as a gate in the signal pipeline
        var sessionFilter    = scope.ServiceProvider.GetRequiredService<ISessionFilter>();
        var newsFilter       = scope.ServiceProvider.GetRequiredService<INewsFilter>();
        var mtfFilter        = scope.ServiceProvider.GetRequiredService<IMultiTimeframeFilter>();
        var correlationCheck = scope.ServiceProvider.GetRequiredService<IPortfolioCorrelationChecker>();
        var hawkesFilter     = scope.ServiceProvider.GetRequiredService<IHawkesSignalFilter>();

        // ── Session filter (applied once per tick, not per strategy) ────────
        if (_options.AllowedSessions.Count > 0)
        {
            var currentSession = sessionFilter.GetCurrentSession(DateTime.UtcNow);
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
                @event.Symbol, DateTime.UtcNow,
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
            .ToListAsync();

        foreach (var strategy in strategies)
        {
            try
            {
                // ── Concurrency guard ─────────────────────────────────────────
                // Prevent duplicate evaluation if price events arrive faster than
                // evaluation completes — only one evaluation per strategy at a time.
                var lockKey = $"strategy:eval:{strategy.Id}";
                await using var evalLock = await _distributedLock.TryAcquireAsync(lockKey);
                if (evalLock is null)
                {
                    _logger.LogDebug(
                        "Strategy {Id} ({Symbol}) evaluation already in progress — skipping this tick",
                        strategy.Id, strategy.Symbol);
                    continue;
                }

                var evaluator = _evaluators.FirstOrDefault(e => e.StrategyType == strategy.StrategyType);
                if (evaluator is null)
                {
                    _logger.LogWarning("No evaluator found for StrategyType={Type}", strategy.StrategyType);

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "Strategy",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Skipped",
                        Reason       = $"No evaluator registered for StrategyType={strategy.StrategyType}",
                        Source       = "StrategyWorker"
                    });

                    continue;
                }

                // ── Dynamic candle fetch based on evaluator requirements ────
                // Each evaluator declares how many historical candles it needs
                // (e.g., MA crossover needs 200+, RSI needs 14+). We fetch only
                // what's required to avoid unnecessary DB load.
                int requiredCandles = evaluator.MinRequiredCandles(strategy);
                var candles = await context.GetDbContext()
                    .Set<Domain.Entities.Candle>()
                    .Where(x => x.Symbol == strategy.Symbol && x.Timeframe == strategy.Timeframe && x.IsClosed && !x.IsDeleted)
                    .OrderByDescending(x => x.Timestamp)
                    .Take(requiredCandles)
                    .OrderBy(x => x.Timestamp)      // Re-order ascending for indicator calculations
                    .ToListAsync();

                // Skip if no live price is available in the cache
                var price = cache.Get(strategy.Symbol);
                if (price is null) continue;

                // Run the strategy-specific evaluation logic (e.g., MA crossover, RSI reversion)
                // Returns null if no signal condition is met on this tick.
                var signal = await evaluator.EvaluateAsync(strategy, candles, (price.Value.Bid, price.Value.Ask), _stoppingToken);
                if (signal is null) continue;

                // ── Multi-timeframe confirmation filter ─────────────────────
                if (_options.RequireMultiTimeframeConfirmation)
                {
                    bool confirmed = await mtfFilter.IsConfirmedAsync(
                        signal.Symbol, signal.Direction.ToString(),
                        strategy.Timeframe.ToString(), _stoppingToken);

                    if (!confirmed)
                    {
                        _logger.LogDebug(
                            "Strategy {Id}: {Direction} signal on {Symbol} not confirmed by higher timeframes — skipping",
                            strategy.Id, signal.Direction, signal.Symbol);

                        await mediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "TradeSignal",
                            EntityId     = strategy.Id,
                            DecisionType = "SignalGeneration",
                            Outcome      = "Filtered",
                            Reason       = "Multi-timeframe confirmation failed",
                            Source       = "StrategyWorker"
                        });

                        continue;
                    }
                }

                // ── Portfolio correlation check ─────────────────────────────
                if (_options.MaxCorrelatedPositions > 0)
                {
                    bool breached = await correlationCheck.IsCorrelationBreachedAsync(
                        signal.Symbol, signal.Direction.ToString(),
                        _options.MaxCorrelatedPositions, _stoppingToken);

                    if (breached)
                    {
                        _logger.LogInformation(
                            "Strategy {Id}: correlation limit breached for {Symbol} {Direction} — skipping",
                            strategy.Id, signal.Symbol, signal.Direction);

                        await mediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "TradeSignal",
                            EntityId     = strategy.Id,
                            DecisionType = "SignalGeneration",
                            Outcome      = "Filtered",
                            Reason       = $"Portfolio correlation limit ({_options.MaxCorrelatedPositions}) breached for {signal.Symbol}",
                            Source       = "StrategyWorker"
                        });

                        continue;
                    }
                }

                // ── Hawkes burst filter ─────────────────────────────────────
                if (_options.HawkesRecentSignalCount > 0)
                {
                    var recentTimestamps = await context.GetDbContext()
                        .Set<Domain.Entities.TradeSignal>()
                        .Where(x => x.Symbol == strategy.Symbol && !x.IsDeleted)
                        .OrderByDescending(x => x.GeneratedAt)
                        .Take(_options.HawkesRecentSignalCount)
                        .Select(x => x.GeneratedAt)
                        .ToListAsync();

                    bool isBurst = await hawkesFilter.IsBurstEpisodeAsync(
                        signal.Symbol, strategy.Timeframe, recentTimestamps, _stoppingToken);

                    if (isBurst)
                    {
                        _logger.LogInformation(
                            "Strategy {Id}: Hawkes burst detected for {Symbol}/{Tf} — suppressing signal",
                            strategy.Id, signal.Symbol, strategy.Timeframe);

                        await mediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "TradeSignal",
                            EntityId     = strategy.Id,
                            DecisionType = "SignalGeneration",
                            Outcome      = "Filtered",
                            Reason       = "Hawkes process burst episode — signal clustering detected",
                            Source       = "StrategyWorker"
                        });

                        continue;
                    }
                }

                // ── ML scoring ──────────────────────────────────────────────
                var mlScore = await mlScorer.ScoreAsync(signal, candles, _stoppingToken);

                // ML suppression: when the scorer returns all nulls (cooldown, suppression,
                // or consensus failure), skip signal creation entirely.
                if (mlScore.MLModelId.HasValue && !mlScore.PredictedDirection.HasValue)
                {
                    _logger.LogInformation(
                        "Strategy {Id}: ML scoring suppressed for {Symbol}/{Tf} (model {ModelId}) — skipping signal",
                        strategy.Id, signal.Symbol, strategy.Timeframe, mlScore.MLModelId);

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Suppressed",
                        Reason       = $"ML model {mlScore.MLModelId} suppressed scoring (cooldown/consensus failure)",
                        Source       = "StrategyWorker"
                    });

                    continue;
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

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = strategy.Id,
                        DecisionType = "SignalGeneration",
                        Outcome      = "Suppressed",
                        Reason       = $"ML abstention score {mlScore.AbstentionScore:F3} < threshold {_options.MinAbstentionScore:F3}",
                        Source       = "StrategyWorker"
                    });

                    continue;
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
                var result = await mediator.Send(new CreateTradeSignalCommand
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
                    MLEnsembleDisagreement = mlScore.EnsembleDisagreement,
                    Timeframe              = strategy.Timeframe,
                    ExpiresAt              = signal.ExpiresAt
                });

                // On successful creation, publish the integration event so
                // SignalOrderBridgeWorker can pick it up for risk checking and order creation.
                if (result.status && result.data > 0)
                {
                    await eventService.SaveAndPublish(writeContext, new TradeSignalCreatedIntegrationEvent
                    {
                        TradeSignalId = result.data,
                        StrategyId    = signal.StrategyId,
                        Symbol        = signal.Symbol,
                        Direction     = signal.Direction.ToString(),
                        EntryPrice    = signal.EntryPrice
                    });

                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType   = "TradeSignal",
                        EntityId     = result.data,
                        DecisionType = "SignalGenerated",
                        Outcome      = "Created",
                        Reason       = $"Strategy {strategy.Id} generated {signal.Direction} signal on {signal.Symbol} at {signal.EntryPrice}, " +
                                       $"SL={signal.StopLoss:F5}, TP={signal.TakeProfit:F5}, Confidence={signal.Confidence:P2}",
                        Source       = "StrategyWorker"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating strategy {StrategyId} for {Symbol}", strategy.Id, @event.Symbol);
            }
        }
    }

    /// <summary>
    /// Timer callback that sweeps for pending trade signals whose <c>ExpiresAt</c> has passed.
    /// Expired signals are transitioned via <see cref="ExpireTradeSignalCommand"/> so they are
    /// no longer eligible for order creation by <see cref="SignalOrderBridgeWorker"/>.
    /// Runs on a fire-and-forget <see cref="Task.Run"/> to avoid blocking the timer thread.
    /// </summary>
    private void RunExpirySweep(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope  = _scopeFactory.CreateScope();
                var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();
                var context      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

                // Find all pending signals that have exceeded their time-to-live
                var expiredIds = await context.GetDbContext()
                    .Set<Domain.Entities.TradeSignal>()
                    .Where(x => x.Status == TradeSignalStatus.Pending && x.ExpiresAt < DateTime.UtcNow && !x.IsDeleted)
                    .Select(x => x.Id)
                    .ToListAsync();

                // Expire each signal individually via the CQRS command
                foreach (var id in expiredIds)
                    await mediator.Send(new ExpireTradeSignalCommand { Id = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in signal expiry sweep");
            }
        });
    }
}
