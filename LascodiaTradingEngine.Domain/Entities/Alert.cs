using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a configurable notification alert that fires when a defined condition is
/// met for a given instrument (e.g. price level crossed, volatility spike, signal generated).
/// </summary>
/// <remarks>
/// Alert conditions are evaluated by the alert-checking worker on every relevant price
/// update or event. When triggered, the engine dispatches notifications to all configured
/// channels (Webhook, Email, Telegram). Channel destinations are configured in appsettings.
/// <see cref="LastTriggeredAt"/> prevents duplicate firings during the same price level crossing.
/// </remarks>
public class Alert : Entity<long>
{
    /// <summary>
    /// The category of event this alert monitors.
    /// e.g. <c>PriceLevel</c> fires when price crosses a threshold;
    /// <c>SignalGenerated</c> fires when a strategy produces a new signal.
    /// </summary>
    public AlertType  AlertType      { get; set; } = AlertType.PriceLevel;

    /// <summary>
    /// The currency pair or instrument this alert relates to (e.g. "EURUSD").
    /// Null for system-wide alerts that are not specific to an instrument.
    /// </summary>
    public string?  Symbol         { get; set; }

    /// <summary>
    /// JSON object encoding the specific trigger condition for this alert.
    /// Schema varies by <see cref="AlertType"/>:
    /// e.g. <c>{"Price": 1.0850, "Direction": "Above"}</c> for a price-level alert.
    /// Evaluated by the alert condition parser at runtime.
    /// </summary>
    public string  ConditionJson  { get; set; } = "{}";

    /// <summary>
    /// When <c>true</c>, this alert is evaluated on every applicable event.
    /// Set to <c>false</c> to temporarily disable without deleting.
    /// </summary>
    public bool    IsActive       { get; set; } = true;

    /// <summary>
    /// UTC timestamp of the most recent successful delivery of this alert.
    /// Null if the alert has never fired. Used to implement cooldown logic
    /// and prevent duplicate notifications for sustained condition breaches.
    /// </summary>
    public DateTime? LastTriggeredAt { get; set; }

    // ── Alert severity & escalation (Improvement 15.2) ──────────────────────

    /// <summary>
    /// Severity tier for categorizing alert urgency. All alerts are broadcast to
    /// every configured channel; severity is informational for triage and filtering.
    /// </summary>
    public AlertSeverity Severity { get; set; } = AlertSeverity.Medium;

    /// <summary>
    /// Deduplication key to prevent the same alert firing repeatedly during a sustained condition.
    /// Alerts with the same key within the cooldown window are suppressed. Null disables dedup.
    /// </summary>
    public string? DeduplicationKey { get; set; }

    /// <summary>
    /// Minimum seconds between firings for the same deduplication key.
    /// Prevents alert storms during sustained breaches.
    /// </summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>
    /// When the alert condition cleared and the alert auto-resolved. Null if still active or
    /// not yet auto-resolved. Enables "alert cleared" notifications.
    /// </summary>
    public DateTime? AutoResolvedAt { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted      { get; set; }
}
