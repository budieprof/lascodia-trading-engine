using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Reacts to <see cref="PositionClosedIntegrationEvent"/> by writing a structured
/// <see cref="Domain.Entities.DecisionLog"/> entry that captures realised P&amp;L,
/// close reason, and pip movement without requiring downstream components to poll
/// the Positions table.
/// </summary>
public sealed class PositionClosedEventHandler : IIntegrationEventHandler<PositionClosedIntegrationEvent>
{
    private readonly IServiceScopeFactory               _scopeFactory;
    private readonly ILogger<PositionClosedEventHandler> _logger;

    public PositionClosedEventHandler(
        IServiceScopeFactory              scopeFactory,
        ILogger<PositionClosedEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task Handle(PositionClosedIntegrationEvent @event)
    {
        _logger.LogInformation(
            "PositionClosedEventHandler: position {PositionId} closed " +
            "({Symbol} {Direction}) — Reason={Reason}, PnL={PnL:F2}, Pips={Pips:F1}, Profitable={Profitable}",
            @event.PositionId, @event.Symbol, @event.Direction,
            @event.CloseReason, @event.RealisedPnL, @event.ActualMagnitudePips, @event.WasProfitable);

        using var scope = _scopeFactory.CreateScope();
        var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Position",
            EntityId     = @event.PositionId,
            DecisionType = "PositionClosed",
            Outcome      = @event.WasProfitable ? "Profitable" : "Loss",
            Reason       = $"{@event.CloseReason}: {@event.Direction} {@event.Symbol} " +
                           $"entry={@event.EntryPrice:F5} close={@event.ClosePrice:F5} " +
                           $"PnL={@event.RealisedPnL:F2} Pips={@event.ActualMagnitudePips:F1}",
            Source       = "PositionClosedEventHandler"
        });
    }
}
