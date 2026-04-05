using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Coordinates alert dispatch across configured channels (Webhook, Email, Telegram)
/// with deduplication and severity-based routing.
/// </summary>
public interface IAlertDispatcher
{
    /// <summary>Dispatches an alert to the channel configured on the alert entity.</summary>
    Task DispatchAsync(Alert alert, string message, CancellationToken ct);

    /// <summary>
    /// Dispatches an alert to channels determined by severity tier with deduplication.
    /// Critical/High -> Telegram + Webhook; Medium -> Webhook; Info -> Webhook (low priority).
    /// </summary>
    Task DispatchBySeverityAsync(Alert alert, string message, CancellationToken ct);

    /// <summary>
    /// Auto-resolves an alert when the triggering condition clears. Sets AutoResolvedAt
    /// and sends a "resolved" notification if the alert was previously active.
    /// The caller is responsible for calling SaveChangesAsync after this method returns,
    /// as the dispatcher (Singleton) does not own a DbContext.
    /// </summary>
    Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct);
}
