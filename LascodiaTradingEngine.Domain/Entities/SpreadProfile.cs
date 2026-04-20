using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores aggregated bid/ask spread statistics for a currency pair, bucketed by UTC
/// hour and optionally by day of week or trading session.
/// </summary>
/// <remarks>
/// <para>
/// <c>SpreadProfileWorker</c> builds these rows from recent <c>TickRecord</c> data by
/// calculating spread percentiles and mean spread over a rolling aggregation window.
/// Current profiles are rebuilt per symbol by soft-deleting the previous active rows
/// and inserting fresh aggregates, so consumers should query non-deleted rows only.
/// </para>
/// <para>
/// These profiles provide realistic, time-varying transaction-cost assumptions for
/// backtesting, walk-forward validation, optimization screening, smart order routing,
/// transaction-cost analysis, and spread-aware ML features. Consumers typically prefer
/// the exact <see cref="DayOfWeek"/> bucket and fall back to the all-day hourly bucket
/// where <see cref="DayOfWeek"/> is <c>null</c>.
/// </para>
/// </remarks>
public class SpreadProfile : Entity<long>
{
    /// <summary>The currency pair or traded symbol this spread profile applies to (for example, "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// UTC hour of day for this spread bucket, in the range 0-23. Consumers match this
    /// against the candle or tick timestamp hour when selecting a time-varying spread.
    /// </summary>
    public int HourUtc { get; set; }

    /// <summary>
    /// Optional day-of-week bucket for the profile. When <c>null</c>, the row represents
    /// an all-day aggregate for the same <see cref="Symbol"/> and <see cref="HourUtc"/>.
    /// </summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>
    /// Optional trading-session label, such as "London", "NewYork", or "Asian".
    /// The current spread aggregation worker leaves this null, but the column supports
    /// session-specific spread profiles if a future aggregator enables them.
    /// </summary>
    public string? SessionName { get; set; }

    /// <summary>
    /// 25th percentile spread for this bucket, in price units. This represents tighter
    /// than typical liquidity conditions for the symbol and time bucket.
    /// </summary>
    public decimal SpreadP25 { get; set; }

    /// <summary>
    /// Median spread for this bucket, in price units. This is the default spread used by
    /// spread-profile consumers when simulating expected transaction costs.
    /// </summary>
    public decimal SpreadP50 { get; set; }

    /// <summary>
    /// 75th percentile spread for this bucket, in price units. Useful for moderately
    /// conservative cost assumptions during volatile or lower-liquidity periods.
    /// </summary>
    public decimal SpreadP75 { get; set; }

    /// <summary>
    /// 95th percentile spread for this bucket, in price units. Useful for stress-testing
    /// strategies against adverse but historically observed spread widening.
    /// </summary>
    public decimal SpreadP95 { get; set; }

    /// <summary>
    /// Arithmetic mean spread for this bucket, in price units. Used as a fallback or
    /// summary statistic when percentile-specific behavior is not required.
    /// </summary>
    public decimal SpreadMean { get; set; }

    /// <summary>
    /// Number of tick observations that contributed to the percentile and mean
    /// calculations. Low values indicate a sparse bucket and less reliable estimates.
    /// </summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// Inclusive UTC lower bound of the tick-data aggregation window used to compute this profile.
    /// </summary>
    public DateTime AggregatedFrom { get; set; }

    /// <summary>
    /// UTC upper bound of the tick-data aggregation window used to compute this profile.
    /// Usually the worker run time.
    /// </summary>
    public DateTime AggregatedTo { get; set; }

    /// <summary>UTC timestamp when this spread profile row was computed and inserted.</summary>
    public DateTime ComputedAt { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Database-managed optimistic concurrency token used to detect conflicting updates
    /// to the same spread profile row.
    /// </summary>
    public uint RowVersion { get; set; }
}
