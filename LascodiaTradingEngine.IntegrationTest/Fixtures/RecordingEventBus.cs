using System.Collections.Concurrent;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.IntegrationTest.Fixtures;

public sealed class RecordingEventBus : IEventBus
{
    private readonly ConcurrentQueue<IntegrationEvent> _publishedEvents = new();

    public IReadOnlyCollection<IntegrationEvent> PublishedEvents => _publishedEvents.ToArray();

    public void Publish(IntegrationEvent @event)
        => _publishedEvents.Enqueue(@event);

    public void Subscribe<T, TH>()
        where T : IntegrationEvent
        where TH : IIntegrationEventHandler<T>
    {
    }

    public void Subscribe(Type handler)
    {
    }

    public void SubscribeDynamic<TH>(string eventName)
        where TH : IDynamicIntegrationEventHandler
    {
    }

    public void UnsubscribeDynamic<TH>(string eventName)
        where TH : IDynamicIntegrationEventHandler
    {
    }

    public void Unsubscribe<T, TH>()
        where TH : IIntegrationEventHandler<T>
        where T : IntegrationEvent
    {
    }

    public void Unsubscribe(Type handlerType)
    {
    }
}
