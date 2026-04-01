namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Alert severity tier controlling escalation and notification channels.</summary>
public enum AlertSeverity
{
    /// <summary>Informational — webhook only, no escalation.</summary>
    Info = 0,
    /// <summary>Medium — webhook, 15-minute escalation window.</summary>
    Medium = 1,
    /// <summary>High — Telegram + webhook, 5-minute escalation.</summary>
    High = 2,
    /// <summary>Critical — SMS + Telegram + webhook, immediate escalation.</summary>
    Critical = 3
}
