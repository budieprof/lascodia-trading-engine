namespace LascodiaTradingEngine.Application.Common.Realtime;

/// <summary>
/// Pushes a JSON-serialisable payload to every browser session connected to the realtime
/// hub for a given trading account. Implemented in the API layer over SignalR so the
/// Application layer doesn't take a Web framework dependency.
/// </summary>
/// <remarks>
/// Methods are fire-and-forget from the perspective of the caller — failures are logged
/// inside the implementation rather than thrown, because event handlers must not abort
/// the event-bus dispatch loop on a hub-side blip.
/// </remarks>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Send <paramref name="payload"/> to every browser client subscribed under
    /// <paramref name="tradingAccountId"/>. The hub's group convention is
    /// <c>account:{TradingAccountId}</c>; consumers don't need to know that.
    /// </summary>
    /// <param name="tradingAccountId">Account scope. Pass <c>null</c> to broadcast to the global group instead.</param>
    /// <param name="eventName">Hub method name the client listens for (e.g. <c>"orderCreated"</c>).</param>
    /// <param name="payload">JSON-serialisable payload — typically the integration event itself.</param>
    Task NotifyAsync(long? tradingAccountId, string eventName, object payload);
}
