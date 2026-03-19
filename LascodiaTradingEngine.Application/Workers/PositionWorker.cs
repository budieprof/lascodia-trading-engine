using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
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
    private readonly ILogger<PositionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILivePriceCache _priceCache;
    private readonly IEventBus _eventBus;

    public PositionWorker(
        ILogger<PositionWorker> logger,
        IServiceScopeFactory scopeFactory,
        ILivePriceCache priceCache,
        IEventBus eventBus)
    {
        _logger       = logger;
        _scopeFactory  = scopeFactory;
        _priceCache   = priceCache;
        _eventBus     = eventBus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PositionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            await UpdatePositionsAsync(stoppingToken);
        }

        _logger.LogInformation("PositionWorker stopped");
    }

    private async Task UpdatePositionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope    = _scopeFactory.CreateScope();
            var context        = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var mediator       = scope.ServiceProvider.GetRequiredService<IMediator>();

            var openPositions = await context.GetDbContext()
                .Set<Domain.Entities.Position>()
                .Where(x => x.Status == PositionStatus.Open && !x.IsDeleted)
                .ToListAsync(cancellationToken);

            foreach (var position in openPositions)
            {
                var priceData = _priceCache.Get(position.Symbol);
                if (priceData == null) continue;

                // Use mid price for P&L updates
                decimal currentPrice = (priceData.Value.Bid + priceData.Value.Ask) / 2m;

                await mediator.Send(new UpdatePositionPriceCommand
                {
                    Id           = position.Id,
                    CurrentPrice = currentPrice
                }, cancellationToken);

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

                        await mediator.Send(new ClosePositionCommand
                        {
                            Id         = position.Id,
                            ClosePrice = currentPrice
                        }, cancellationToken);

                        await mediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "Position",
                            EntityId     = position.Id,
                            DecisionType = "StopLossClosure",
                            Outcome      = "Closed",
                            Reason       = $"Stop loss hit at {currentPrice} (SL level: {position.StopLoss}), {position.Symbol} {position.Direction}",
                            Source       = "PositionWorker"
                        }, cancellationToken);

                        PublishPositionClosedEvent(position, currentPrice, "StopLoss");
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

                        await mediator.Send(new ClosePositionCommand
                        {
                            Id         = position.Id,
                            ClosePrice = currentPrice
                        }, cancellationToken);

                        await mediator.Send(new LogDecisionCommand
                        {
                            EntityType   = "Position",
                            EntityId     = position.Id,
                            DecisionType = "TakeProfitClosure",
                            Outcome      = "Closed",
                            Reason       = $"Take profit hit at {currentPrice} (TP level: {position.TakeProfit}), {position.Symbol} {position.Direction}",
                            Source       = "PositionWorker"
                        }, cancellationToken);

                        PublishPositionClosedEvent(position, currentPrice, "TakeProfit");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "PositionWorker error during price update cycle");
        }
    }

    private void PublishPositionClosedEvent(Domain.Entities.Position position, decimal closePrice, string reason)
    {
        const decimal pipFactor  = 10_000m;
        decimal directionSign    = position.Direction == PositionDirection.Long ? 1m : -1m;
        decimal magnitudePips    = (closePrice - position.AverageEntryPrice) * pipFactor * directionSign;

        // Estimate realised PnL (close may not be persisted yet; use approximation for the event payload)
        const decimal standardLot = 100_000m;
        decimal estimatedPnl = position.Direction == PositionDirection.Long
            ? (closePrice - position.AverageEntryPrice) * position.OpenLots * standardLot
            : (position.AverageEntryPrice - closePrice) * position.OpenLots * standardLot;

        _eventBus.Publish(new PositionClosedIntegrationEvent
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
