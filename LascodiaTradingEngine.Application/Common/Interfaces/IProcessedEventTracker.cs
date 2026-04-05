namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Tracks integration event processing to prevent duplicate handling in multi-instance
/// deployments. Uses the event's <c>Id</c> (Guid) as the natural idempotency key.
///
/// Consumers call <see cref="TryMarkAsProcessedAsync"/> before handling an event.
/// If it returns <c>false</c>, another instance already processed it.
///
/// Processed event IDs are retained for <paramref name="ttl"/> hours (default 24) before
/// cleanup to bound storage growth.
/// </summary>
public interface IProcessedEventTracker
{
    /// <summary>
    /// Attempts to mark an event as processed. Returns <c>true</c> if this is the first
    /// processor (caller should proceed), or <c>false</c> if already processed (caller should skip).
    /// Thread-safe and safe for concurrent calls with the same eventId.
    /// </summary>
    /// <param name="eventId">The integration event's unique Id (Guid).</param>
    /// <param name="handlerName">Name of the consuming handler (for audit/debugging).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> TryMarkAsProcessedAsync(Guid eventId, string handlerName, CancellationToken ct);
}
