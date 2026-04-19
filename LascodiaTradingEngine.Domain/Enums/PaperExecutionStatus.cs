namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Lifecycle states for a <see cref="Entities.PaperExecution"/> row.
/// </summary>
public enum PaperExecutionStatus
{
    /// <summary>Position open; monitoring worker tracks SL/TP on each tick.</summary>
    Open     = 0,
    /// <summary>SL or TP hit; terminal P&amp;L recorded.</summary>
    Closed   = 1,
    /// <summary>No bracket hit within the per-signal timeout; force-closed at last tick.</summary>
    Expired  = 2,
    /// <summary>Simulator failure (e.g. missing price data); row preserved for audit.</summary>
    Failed   = 3,
}
