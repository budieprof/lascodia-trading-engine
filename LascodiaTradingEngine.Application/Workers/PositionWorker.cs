using System.Collections.Concurrent;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Commands.ClosePosition;
using LascodiaTradingEngine.Application.Positions.Commands.UpdatePositionPrice;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that updates open position prices from the live price cache
/// every 10 seconds, triggers SL/TP closures when levels are hit, and — after each
/// closure — publishes a <c>PositionClosedIntegrationEvent</c> so downstream consumers
/// (e.g., <c>PredictionOutcomeWorker</c>) can react reactively without polling.
/// </summary>
public class PositionWorker : BackgroundService
{
    private const int InvertedQuoteEscalationThreshold = 5;

    private readonly ILogger<PositionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILivePriceCache _priceCache;
    private readonly TradingMetrics _metrics;
    private readonly IDistributedLock _distributedLock;
    private readonly ConcurrentDictionary<string, int> _consecutiveInvertedQuotes = new();

    public PositionWorker(
        ILogger<PositionWorker> logger,
        IServiceScopeFactory scopeFactory,
        ILivePriceCache priceCache,
        TradingMetrics metrics,
        IDistributedLock distributedLock)
    {
        _logger          = logger;
        _scopeFactory    = scopeFactory;
        _priceCache      = priceCache;
        _metrics         = metrics;
        _distributedLock = distributedLock;
    }

    private const string CK_PollSecs = "Position:PollIntervalSeconds";
    private const int DefaultPollSeconds = 10;
    private const int MaxBackoffSeconds = 300; // 5 minutes
    private int _consecutiveErrors;

    /// <summary>
    /// Main polling loop, updating open position prices and checking SL/TP levels.
    /// The poll interval is configurable via EngineConfig key <c>Position:PollIntervalSeconds</c>
    /// (defaults to 10).
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PositionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollSeconds;
            try
            {
                using var configScope = _scopeFactory.CreateScope();
                var configCtx = configScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var entry = await configCtx.GetDbContext()
                    .Set<Domain.Entities.EngineConfig>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == CK_PollSecs, stoppingToken);
                if (entry?.Value is not null && int.TryParse(entry.Value, out var parsed) && parsed > 0)
                    pollSecs = parsed;
            }
            catch { /* use default */ }

            try
            {
                await UpdatePositionsAsync(stoppingToken);
                _consecutiveErrors = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _consecutiveErrors++;
                int backoffSecs = Math.Min(pollSecs * (int)Math.Pow(2, _consecutiveErrors - 1), MaxBackoffSeconds);
                _logger.LogError(ex,
                    "PositionWorker: error in update cycle (consecutive={Count}), backing off {Backoff}s",
                    _consecutiveErrors, backoffSecs);
                await Task.Delay(TimeSpan.FromSeconds(backoffSecs), stoppingToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("PositionWorker stopped");
    }

    /// <summary>
    /// Loads all open positions, updates their current price from the live cache,
    /// and checks whether SL or TP levels have been breached. On breach, the position
    /// is closed via <see cref="ClosePositionCommand"/> and a
    /// <see cref="PositionClosedIntegrationEvent"/> is published.
    /// </summary>
    private async Task UpdatePositionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope    = _scopeFactory.CreateScope();
            var context        = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var writeContext   = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var mediator       = scope.ServiceProvider.GetRequiredService<IMediator>();
            var eventService   = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

            // Fetch all open positions across all accounts/strategies
            var openPositions = await context.GetDbContext()
                .Set<Domain.Entities.Position>()
                .Where(x => x.Status == PositionStatus.Open && !x.IsDeleted)
                .ToListAsync(cancellationToken);

            int priceUpdated = 0, slClosed = 0, tpClosed = 0, cacheMisses = 0, errors = 0;

            foreach (var position in openPositions)
            {
                try
                {
                    // Look up the latest bid/ask for this symbol from the in-memory cache
                    var priceData = _priceCache.Get(position.Symbol);
                    if (priceData == null)
                    {
                        cacheMisses++;
                        _metrics.PriceCacheMisses.Add(1, new KeyValuePair<string, object?>("symbol", position.Symbol));
                        _logger.LogWarning(
                            "PositionWorker: no live price for {Symbol} — position {Id} skipped (SL/TP not monitored this cycle)",
                            position.Symbol, position.Id);
                        continue;
                    }

                    // Price sanity: reject inverted quotes (Ask < Bid)
                    if (priceData.Value.Ask < priceData.Value.Bid)
                    {
                        int count = _consecutiveInvertedQuotes.AddOrUpdate(position.Symbol, 1, (_, c) => c + 1);
                        var logLevel = count >= InvertedQuoteEscalationThreshold
                            ? LogLevel.Error
                            : LogLevel.Warning;
                        _logger.Log(logLevel,
                            "PositionWorker: inverted quote for {Symbol} (Bid={Bid}, Ask={Ask}) — skipping position {Id} (consecutive: {Count})",
                            position.Symbol, priceData.Value.Bid, priceData.Value.Ask, position.Id, count);
                        _metrics.WorkerErrors.Add(1,
                            new KeyValuePair<string, object?>("worker", "PositionWorker"),
                            new KeyValuePair<string, object?>("reason", "inverted_quote"));
                        continue;
                    }
                    _consecutiveInvertedQuotes.TryRemove(position.Symbol, out _);

                    // Use mid price for P&L updates
                    decimal currentPrice = (priceData.Value.Bid + priceData.Value.Ask) / 2m;

                    // Wrap mediator calls with a timeout to prevent hung handlers from
                    // blocking the entire position update cycle
                    using var priceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    priceCts.CancelAfter(TimeSpan.FromSeconds(30));

                    await mediator.Send(new UpdatePositionPriceCommand
                    {
                        Id           = position.Id,
                        CurrentPrice = currentPrice
                    }, priceCts.Token);
                    priceUpdated++;

                    // Lock per position to prevent concurrent close from PositionWorker
                    // and OrderExecutionWorker operating on the same position simultaneously.
                    var lockKey = $"position:close:{position.Id}";
                    await using var posLock = await _distributedLock.TryAcquireAsync(lockKey, cancellationToken);
                    if (posLock is null) continue; // another worker is handling this position

                    // Check Stop Loss
                    if (position.StopLoss.HasValue)
                    {
                        bool slHit = position.Direction == PositionDirection.Long
                            ? currentPrice <= position.StopLoss.Value
                            : currentPrice >= position.StopLoss.Value;

                        if (slHit)
                        {
                            _logger.LogWarning("Stop loss hit for Position {Id} ({Symbol} {Direction}) at {Price}",
                                position.Id, position.Symbol, position.Direction, currentPrice);

                            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            closeCts.CancelAfter(TimeSpan.FromSeconds(30));

                            await mediator.Send(new ClosePositionCommand
                            {
                                Id         = position.Id,
                                ClosePrice = currentPrice
                            }, closeCts.Token);

                            await mediator.Send(new LogDecisionCommand
                            {
                                EntityType   = "Position",
                                EntityId     = position.Id,
                                DecisionType = "StopLossClosure",
                                Outcome      = "Closed",
                                Reason       = $"Stop loss hit at {currentPrice} (SL level: {position.StopLoss}), {position.Symbol} {position.Direction}",
                                Source       = "PositionWorker"
                            }, closeCts.Token);

                            await PublishPositionClosedEventAsync(writeContext, context, eventService, position, currentPrice, "StopLoss");
                            slClosed++;
                            continue;
                        }
                    }

                    // Check Take Profit
                    if (position.TakeProfit.HasValue)
                    {
                        bool tpHit = position.Direction == PositionDirection.Long
                            ? currentPrice >= position.TakeProfit.Value
                            : currentPrice <= position.TakeProfit.Value;

                        if (tpHit)
                        {
                            _logger.LogInformation("Take profit hit for Position {Id} ({Symbol} {Direction}) at {Price}",
                                position.Id, position.Symbol, position.Direction, currentPrice);

                            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            closeCts.CancelAfter(TimeSpan.FromSeconds(30));

                            await mediator.Send(new ClosePositionCommand
                            {
                                Id         = position.Id,
                                ClosePrice = currentPrice
                            }, closeCts.Token);

                            await mediator.Send(new LogDecisionCommand
                            {
                                EntityType   = "Position",
                                EntityId     = position.Id,
                                DecisionType = "TakeProfitClosure",
                                Outcome      = "Closed",
                                Reason       = $"Take profit hit at {currentPrice} (TP level: {position.TakeProfit}), {position.Symbol} {position.Direction}",
                                Source       = "PositionWorker"
                            }, closeCts.Token);

                            await PublishPositionClosedEventAsync(writeContext, context, eventService, position, currentPrice, "TakeProfit");
                            tpClosed++;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // Host shutdown — propagate
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogError(ex,
                        "PositionWorker: error processing position {PositionId} ({Symbol}) — skipping to next position",
                        position.Id, position.Symbol);
                    _metrics.WorkerErrors.Add(1,
                        new KeyValuePair<string, object?>("worker", "PositionWorker"),
                        new KeyValuePair<string, object?>("position_id", position.Id));
                }
            }

            if (openPositions.Count > 0)
            {
                _logger.LogDebug(
                    "PositionWorker cycle: {Total} positions, {Updated} priced, {SL} SL closed, {TP} TP closed, {Miss} cache misses, {Errors} errors",
                    openPositions.Count, priceUpdated, slClosed, tpClosed, cacheMisses, errors);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "PositionWorker error during price update cycle");
            throw; // Let the outer loop handle backoff
        }
    }

    /// <summary>
    /// Publishes a <see cref="PositionClosedIntegrationEvent"/> after a SL or TP closure.
    /// Computes slippage magnitude in pips and an estimated realised PnL (based on a
    /// standard FX lot size of 100,000 units) for the event payload. The estimated PnL
    /// is approximate — the authoritative value is persisted by the close command handler.
    /// </summary>
    /// <param name="writeContext">Write DB context for atomic save-and-publish.</param>
    /// <param name="eventService">Integration event service for publishing.</param>
    /// <param name="position">The closed position entity (still holds pre-close state).</param>
    /// <param name="closePrice">Mid-price at which the position was closed.</param>
    /// <param name="reason">Closure reason label (e.g., "StopLoss", "TakeProfit").</param>
    private async Task PublishPositionClosedEventAsync(
        IWriteApplicationDbContext writeContext,
        IReadApplicationDbContext readContext,
        IIntegrationEventService eventService,
        Domain.Entities.Position position,
        decimal closePrice,
        string reason)
    {
        // Standard 5-decimal FX pip factor
        const decimal pipFactor  = 10_000m;
        // Sign convention: +1 for Long (profit when price rises), −1 for Short
        decimal directionSign    = position.Direction == PositionDirection.Long ? 1m : -1m;
        decimal magnitudePips    = (closePrice - position.AverageEntryPrice) * pipFactor * directionSign;

        // Load actual contract size from currency pair spec instead of assuming 100k
        var currencyPair = await readContext.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == position.Symbol && !x.IsDeleted);

        decimal contractSize = currencyPair?.ContractSize ?? 100_000m;

        // Estimate realised PnL including swap and commission
        decimal tradePnl = position.Direction == PositionDirection.Long
            ? (closePrice - position.AverageEntryPrice) * position.OpenLots * contractSize
            : (position.AverageEntryPrice - closePrice) * position.OpenLots * contractSize;
        decimal estimatedPnl = tradePnl + position.Swap + position.Commission;

        _metrics.PositionsClosed.Add(1, new KeyValuePair<string, object?>("reason", reason));
        _metrics.PositionPnL.Record((double)estimatedPnl);

        await eventService.SaveAndPublish(writeContext, new PositionClosedIntegrationEvent
        {
            PositionId          = position.Id,
            TradeSignalId       = null,     // Position has no direct FK to TradeSignal; resolved by PredictionOutcomeWorker
            StrategyId          = null,
            Symbol              = position.Symbol,
            Direction           = position.Direction,
            EntryPrice          = position.AverageEntryPrice,
            ClosePrice          = closePrice,
            RealisedPnL         = estimatedPnl,
            ActualMagnitudePips = magnitudePips,
            WasProfitable       = estimatedPnl > 0,
            CloseReason         = reason,
            ClosedAt            = DateTime.UtcNow
        });
    }
}
