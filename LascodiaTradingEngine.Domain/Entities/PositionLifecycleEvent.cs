using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Immutable audit record tracking every significant state change in a position's lifecycle.
/// Created by PositionWorker, ReconciliationWorker, ClosePositionCommand, and EA snapshot handlers.
/// </summary>
public class PositionLifecycleEvent : Entity<long>
{
    /// <summary>Position this event belongs to.</summary>
    public long PositionId { get; set; }

    /// <summary>Type of lifecycle event: Opened, Modified, PartialClose, Closed, ForceClosed, Reconciled, StaleClose.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Subsystem or actor that triggered this event: EA, PositionWorker, ReconciliationWorker, Broker, Manual.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Lot size before this event (null for opens).</summary>
    public decimal? PreviousLots { get; set; }

    /// <summary>Lot size after this event (null for closes).</summary>
    public decimal? NewLots { get; set; }

    /// <summary>Accumulated swap at the time of this event.</summary>
    public decimal? SwapAccumulated { get; set; }

    /// <summary>Accumulated commission at the time of this event.</summary>
    public decimal? CommissionAccumulated { get; set; }

    /// <summary>Human-readable description of the event.</summary>
    public string? Description { get; set; }

    /// <summary>UTC timestamp when this event occurred.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Navigation property to the parent position.</summary>
    public virtual Position Position { get; set; } = null!;

    public bool IsDeleted { get; set; }
}
