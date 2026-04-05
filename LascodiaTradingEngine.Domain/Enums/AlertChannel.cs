namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Specifies the delivery channel for an alert notification.
/// </summary>
public enum AlertChannel
{
    /// <summary>Send alert via email.</summary>
    Email = 0,

    /// <summary>Send alert to a configured webhook endpoint.</summary>
    Webhook = 1,

    /// <summary>Send alert via Telegram bot message.</summary>
    Telegram = 2
}
