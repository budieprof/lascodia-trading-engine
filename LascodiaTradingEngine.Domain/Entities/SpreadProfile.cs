using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores aggregated spread statistics (percentiles) for a symbol, bucketed by hour of day,
/// day of week, and optionally trading session. Built daily by SpreadProfileWorker from
/// TickRecord data. Used by the backtesting engine for realistic time-varying spread simulation.
/// </summary>
public class SpreadProfile : Entity<long>
{
    public string Symbol { get; set; } = string.Empty;
    public int HourUtc { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public string? SessionName { get; set; }
    public decimal SpreadP25 { get; set; }
    public decimal SpreadP50 { get; set; }
    public decimal SpreadP75 { get; set; }
    public decimal SpreadP95 { get; set; }
    public decimal SpreadMean { get; set; }
    public int SampleCount { get; set; }
    public DateTime AggregatedFrom { get; set; }
    public DateTime AggregatedTo { get; set; }
    public DateTime ComputedAt { get; set; }
    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
