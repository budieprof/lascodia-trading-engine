using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a configurable notification alert that fires when a defined condition is
/// met for a given instrument (e.g. price level crossed, volatility spike, signal generated).
/// </summary>
/// <remarks>
/// Alert conditions are evaluated by the alert-checking worker on every relevant price
/// update or event. When triggered, the engine dispatches a notification to the configured
/// <see cref="Destination"/> using the chosen <see cref="Channel"/> (Webhook, Email, SMS, etc.).
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

    /// <summary>The currency pair or instrument this alert monitors (e.g. "EURUSD").</summary>
    public string  Symbol         { get; set; } = string.Empty;

    /// <summary>
    /// Delivery channel for notifications when this alert fires.
    /// e.g. <c>Webhook</c> (HTTP POST), <c>Email</c>, <c>Sms</c>, <c>Telegram</c>.
    /// </summary>
    public AlertChannel  Channel        { get; set; } = AlertChannel.Webhook;

    /// <summary>
    /// The target address for delivery — a URL for webhooks, an email address,
    /// a phone number for SMS, or a chat ID for Telegram.
    /// </summary>
    public string  Destination    { get; set; } = string.Empty;

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

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted      { get; set; }
}
