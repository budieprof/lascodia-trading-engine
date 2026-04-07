using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persists alert dispatch history for dashboard display and delivery SLA tracking.
/// Created by <c>AlertDispatcher</c> after each send attempt (success or failure).
/// </summary>
public class AlertDispatchLog : Entity<long>
{
    /// <summary>Alert that was dispatched.</summary>
    public long AlertId { get; set; }

    /// <summary>Channel used for dispatch (Webhook, Email, Telegram).</summary>
    public AlertChannel Channel { get; set; }

    /// <summary>Dispatch outcome: Sent, Failed, Retrying.</summary>
    public string Status { get; set; } = "Sent";

    /// <summary>Alert message content (truncated to 2000 chars).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>UTC timestamp when dispatch was attempted.</summary>
    public DateTime DispatchedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Error message if dispatch failed (null on success).</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Navigation property to the parent alert.</summary>
    public virtual Alert Alert { get; set; } = null!;

    public bool IsDeleted { get; set; }
}
