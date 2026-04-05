namespace LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

/// <summary>
/// Data transfer object representing the current live bid/ask price for a symbol,
/// as held in the in-memory price cache.
/// </summary>
public class LivePriceDto
{
    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public string  Symbol    { get; set; } = string.Empty;

    /// <summary>Current best bid price (broker's buy price from the trader's perspective).</summary>
    public decimal Bid       { get; set; }

    /// <summary>Current best ask price (broker's sell price from the trader's perspective).</summary>
    public decimal Ask       { get; set; }

    /// <summary>Current spread (Ask - Bid) in price units.</summary>
    public decimal Spread    { get; set; }

    /// <summary>UTC time when this price was last updated.</summary>
    public DateTime Timestamp { get; set; }
}
