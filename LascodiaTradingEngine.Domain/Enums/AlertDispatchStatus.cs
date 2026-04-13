namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Outcome of an alert dispatch attempt.
/// </summary>
public enum AlertDispatchStatus
{
    /// <summary>Notification delivered successfully.</summary>
    Sent = 0,

    /// <summary>Delivery failed (see ErrorMessage for details).</summary>
    Failed = 1,

    /// <summary>Delivery is being retried.</summary>
    Retrying = 2
}
