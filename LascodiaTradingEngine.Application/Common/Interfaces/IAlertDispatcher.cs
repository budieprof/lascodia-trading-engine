using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Coordinates alert dispatch across all registered channels (Webhook, Email, Telegram)
/// with deduplication.
/// </summary>
public interface IAlertDispatcher
{
    /// <summary>
    /// Broadcasts an alert to all registered <see cref="IAlertChannelSender"/> implementations
    /// with deduplication based on <see cref="Alert.DeduplicationKey"/> and <see cref="Alert.CooldownSeconds"/>.
    /// </summary>
    Task DispatchAsync(Alert alert, string message, CancellationToken ct);

    /// <summary>
    /// Auto-resolves an alert when the triggering condition clears. Sets AutoResolvedAt
    /// and sends a "resolved" notification if the alert was previously active.
    /// The caller is responsible for calling SaveChangesAsync after this method returns,
    /// as the dispatcher (Singleton) does not own a DbContext.
    /// </summary>
    Task TryAutoResolveAsync(Alert alert, bool conditionStillActive, CancellationToken ct);
}
