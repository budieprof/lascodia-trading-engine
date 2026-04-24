using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Realtime;

namespace LascodiaTradingEngine.Application.EventHandlers.RealtimeRelays;

// Per the design doc (E1) every relay handler is a thin wrapper that forwards a curated
// integration event to the SignalR group for the target trading account. Handlers are
// colocated so the relay surface is reviewable in one file. Events that don't carry a
// trading-account id broadcast to all connected clients (`tradingAccountId: null`).

/// <summary>Pushes <see cref="OrderCreatedIntegrationEvent"/> to the originating account's group.</summary>
public sealed class OrderCreatedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<OrderCreatedIntegrationEvent>
{
    public Task Handle(OrderCreatedIntegrationEvent @event) =>
        notifier.NotifyAsync(@event.TradingAccountId, "orderCreated", @event);
}

/// <summary>Pushes <see cref="OrderFilledIntegrationEvent"/> — broadcast (event lacks TradingAccountId).</summary>
public sealed class OrderFilledRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<OrderFilledIntegrationEvent>
{
    public Task Handle(OrderFilledIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "orderFilled", @event);
}

/// <summary>Pushes <see cref="PositionOpenedIntegrationEvent"/> — broadcast (event lacks TradingAccountId).</summary>
public sealed class PositionOpenedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<PositionOpenedIntegrationEvent>
{
    public Task Handle(PositionOpenedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "positionOpened", @event);
}

/// <summary>Pushes <see cref="PositionClosedIntegrationEvent"/> — broadcast (event lacks TradingAccountId).</summary>
public sealed class PositionClosedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<PositionClosedIntegrationEvent>
{
    public Task Handle(PositionClosedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "positionClosed", @event);
}

/// <summary>Pushes <see cref="TradeSignalCreatedIntegrationEvent"/> — broadcast (signal isn't account-scoped).</summary>
public sealed class TradeSignalCreatedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<TradeSignalCreatedIntegrationEvent>
{
    public Task Handle(TradeSignalCreatedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "tradeSignalCreated", @event);
}

/// <summary>Pushes <see cref="MLModelActivatedIntegrationEvent"/> — affects the whole platform.</summary>
public sealed class MLModelActivatedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<MLModelActivatedIntegrationEvent>
{
    public Task Handle(MLModelActivatedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "mlModelActivated", @event);
}

/// <summary>Pushes <see cref="VaRBreachIntegrationEvent"/> to the at-risk account's group.</summary>
public sealed class VaRBreachRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<VaRBreachIntegrationEvent>
{
    public Task Handle(VaRBreachIntegrationEvent @event) =>
        notifier.NotifyAsync(@event.TradingAccountId, "vaRBreach", @event);
}

/// <summary>
/// Pushes <see cref="EmergencyFlattenIntegrationEvent"/> — broadcast so every operator UI
/// flags the platform-wide event regardless of which account triggered it.
/// </summary>
public sealed class EmergencyFlattenRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<EmergencyFlattenIntegrationEvent>
{
    public Task Handle(EmergencyFlattenIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "emergencyFlatten", @event);
}

/// <summary>Pushes <see cref="OptimizationCompletedIntegrationEvent"/> — broadcast (analyst panel).</summary>
public sealed class OptimizationCompletedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<OptimizationCompletedIntegrationEvent>
{
    public Task Handle(OptimizationCompletedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "optimizationCompleted", @event);
}

/// <summary>Pushes <see cref="BacktestCompletedIntegrationEvent"/> — broadcast (analyst panel).</summary>
public sealed class BacktestCompletedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<BacktestCompletedIntegrationEvent>
{
    public Task Handle(BacktestCompletedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "backtestCompleted", @event);
}

/// <summary>Pushes <see cref="SentimentSnapshotCreatedIntegrationEvent"/> — broadcast (sentiment page).</summary>
public sealed class SentimentSnapshotCreatedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<SentimentSnapshotCreatedIntegrationEvent>
{
    public Task Handle(SentimentSnapshotCreatedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "sentimentSnapshotCreated", @event);
}

/// <summary>Pushes <see cref="AuditDecisionLoggedIntegrationEvent"/> — broadcast (audit-trail page).</summary>
public sealed class AuditDecisionLoggedRealtimeRelay(IRealtimeNotifier notifier)
    : IIntegrationEventHandler<AuditDecisionLoggedIntegrationEvent>
{
    public Task Handle(AuditDecisionLoggedIntegrationEvent @event) =>
        notifier.NotifyAsync(null, "auditDecisionLogged", @event);
}
