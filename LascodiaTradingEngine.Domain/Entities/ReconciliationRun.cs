using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persisted outcome of a single <c>ProcessReconciliationCommand</c> invocation.
/// One row per EA snapshot, recording how many discrepancies were detected
/// between the engine's open positions/orders and the broker's state at the
/// moment the EA submitted its snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Populated by <c>ProcessReconciliationCommandHandler</c>; consumed by the
/// <c>EaReconciliationMonitorWorker</c> (and operator dashboards). The worker
/// aggregates the last N minutes of runs and alerts when drift exceeds a
/// configurable threshold — the table is the source of truth that drives that
/// alert rule, so it must never be silently truncated. Retention is handled by
/// a separate housekeeping worker, not via soft-delete.
/// </para>
/// </remarks>
public class ReconciliationRun : Entity<long>
{
    /// <summary>The EA instance that submitted the snapshot.</summary>
    public string InstanceId               { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the reconciliation was computed.</summary>
    public DateTime RunAt                  { get; set; } = DateTime.UtcNow;

    /// <summary>Count of engine positions whose BrokerPositionId was missing from the broker snapshot.</summary>
    public int OrphanedEnginePositions     { get; set; }

    /// <summary>Count of broker positions that the engine did not know about.</summary>
    public int UnknownBrokerPositions      { get; set; }

    /// <summary>Count of positions present on both sides but with diverging volume / SL / TP.</summary>
    public int MismatchedPositions         { get; set; }

    /// <summary>Count of engine orders with a BrokerOrderId not found on the broker.</summary>
    public int OrphanedEngineOrders        { get; set; }

    /// <summary>Count of broker orders not tracked in the engine.</summary>
    public int UnknownBrokerOrders         { get; set; }

    /// <summary>Convenience aggregate: total drift across every category.</summary>
    public int TotalDrift                  { get; set; }

    /// <summary>Number of broker positions reported in the submitted snapshot.</summary>
    public int BrokerPositionCount         { get; set; }

    /// <summary>Number of broker orders reported in the submitted snapshot.</summary>
    public int BrokerOrderCount            { get; set; }
}
