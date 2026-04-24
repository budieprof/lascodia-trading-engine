using Microsoft.AspNetCore.SignalR;
using LascodiaTradingEngine.Application.Common.Realtime;

namespace LascodiaTradingEngine.API.Realtime;

/// <summary>
/// SignalR-backed implementation of <see cref="IRealtimeNotifier"/>. Resolves
/// <see cref="IHubContext{THub}"/> rather than holding a hub instance directly so the
/// notifier survives server restarts and scale-out without leaking connection handles
/// (per DESIGN_DOCS.md §E1 "what not to do").
/// </summary>
public sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<TradingEngineRealtimeHub> _hub;
    private readonly ILogger<SignalRRealtimeNotifier> _logger;

    public SignalRRealtimeNotifier(
        IHubContext<TradingEngineRealtimeHub> hub,
        ILogger<SignalRRealtimeNotifier>      logger)
    {
        _hub    = hub;
        _logger = logger;
    }

    public async Task NotifyAsync(long? tradingAccountId, string eventName, object payload)
    {
        try
        {
            if (tradingAccountId is long id)
                await _hub.Clients.Group(TradingEngineRealtimeHub.GroupForAccount(id))
                    .SendAsync(eventName, payload);
            else
                await _hub.Clients.All.SendAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            // Never throw out of the relay path — the event-bus dispatch loop is shared
            // and a hub blip must not break unrelated handlers. See DESIGN_DOCS §E1.
            _logger.LogWarning(ex,
                "Realtime push failed (event={Event} accountId={AccountId})", eventName, tradingAccountId);
        }
    }
}
