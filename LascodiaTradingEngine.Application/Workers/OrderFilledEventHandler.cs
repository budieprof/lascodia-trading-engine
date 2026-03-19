using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Commands.OpenPosition;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Reacts to <see cref="OrderFilledIntegrationEvent"/> by opening a corresponding
/// <see cref="Position"/> record. This closes the Order→Position lifecycle gap:
/// without this handler filled orders never produce positions, leaving
/// <see cref="PositionWorker"/>, <see cref="TrailingStopWorker"/>, and all
/// position-level risk logic with nothing to act on.
/// </summary>
public sealed class OrderFilledEventHandler : IIntegrationEventHandler<OrderFilledIntegrationEvent>
{
    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<OrderFilledEventHandler>  _logger;

    public OrderFilledEventHandler(
        IServiceScopeFactory             scopeFactory,
        ILogger<OrderFilledEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task Handle(OrderFilledIntegrationEvent @event)
    {
        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load the filled order to retrieve lot size, direction, SL/TP, and paper flag.
        var order = await readContext.GetDbContext()
            .Set<Order>()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == @event.OrderId && !o.IsDeleted);

        if (order is null)
        {
            _logger.LogWarning(
                "OrderFilledEventHandler: order {OrderId} not found — skipping position open",
                @event.OrderId);
            return;
        }

        string  direction = order.OrderType == OrderType.Buy ? "Long" : "Short";
        decimal lots      = order.FilledQuantity ?? order.Quantity;

        var result = await mediator.Send(new OpenPositionCommand
        {
            Symbol            = order.Symbol,
            Direction         = direction,
            OpenLots          = lots,
            AverageEntryPrice = @event.FilledPrice,
            StopLoss          = order.StopLoss,
            TakeProfit        = order.TakeProfit,
            IsPaper           = order.IsPaper
        });

        if (result.responseCode != "00")
        {
            _logger.LogError(
                "OrderFilledEventHandler: failed to open position for order {OrderId} — {Message}",
                @event.OrderId, result.message);
            return;
        }

        long positionId = result.data;

        _logger.LogInformation(
            "OrderFilledEventHandler: opened {Direction} position {PositionId} for order {OrderId} " +
            "({Symbol} @ {Price:F5}, lots={Lots:F2})",
            direction, positionId, @event.OrderId, order.Symbol, @event.FilledPrice, lots);

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Order",
            EntityId     = @event.OrderId,
            DecisionType = "PositionOpened",
            Outcome      = $"Position {positionId} opened",
            Reason       = $"{order.OrderType} {order.Symbol} filled at {(@event.FilledPrice):F5} — " +
                           $"{direction} position opened (lots={lots:F2})",
            Source       = "OrderFilledEventHandler"
        });
    }
}
