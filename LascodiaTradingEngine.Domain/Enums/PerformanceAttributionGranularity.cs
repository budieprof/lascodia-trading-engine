namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Distinguishes end-of-day performance attribution snapshots from intraday
/// running snapshots stored in the same table.
/// </summary>
public enum PerformanceAttributionGranularity
{
    /// <summary>
    /// One daily attribution record representing a completed calendar day.
    /// </summary>
    Daily = 0,

    /// <summary>
    /// A running intraday snapshot for the current hour bucket.
    /// </summary>
    Hourly = 1
}
