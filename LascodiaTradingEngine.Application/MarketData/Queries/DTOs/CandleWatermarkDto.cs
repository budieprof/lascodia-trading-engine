namespace LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

/// <summary>
/// Represents the latest candle timestamp for a specific symbol/timeframe combination.
/// Used by EA instances on startup to determine the backfill starting point.
/// </summary>
public class CandleWatermarkDto
{
    /// <summary>Instrument symbol.</summary>
    public string   Symbol          { get; set; } = string.Empty;

    /// <summary>Bar timeframe.</summary>
    public string   Timeframe       { get; set; } = string.Empty;

    /// <summary>Timestamp of the most recent candle stored in the engine for this symbol/timeframe.</summary>
    public DateTime LatestTimestamp { get; set; }
}
