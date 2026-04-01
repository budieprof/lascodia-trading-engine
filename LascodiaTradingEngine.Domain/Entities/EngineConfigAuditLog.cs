using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Immutable audit log entry recording every change to <see cref="EngineConfig"/> values.
/// Enables point-in-time reconstruction of configuration state for post-incident analysis
/// (e.g. "what was MaxRiskPerTradePct set to at 14:32 UTC on March 12?").
/// </summary>
public class EngineConfigAuditLog : Entity<long>
{
    /// <summary>Configuration key that was changed.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Value before the change (null for new keys).</summary>
    public string? OldValue { get; set; }

    /// <summary>Value after the change.</summary>
    public string NewValue { get; set; } = string.Empty;

    /// <summary>Account ID of the user who made the change.</summary>
    public long ChangedByAccountId { get; set; }

    /// <summary>Reason for the change (required for risk-related config).</summary>
    public string? Reason { get; set; }

    /// <summary>When the change was applied.</summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
