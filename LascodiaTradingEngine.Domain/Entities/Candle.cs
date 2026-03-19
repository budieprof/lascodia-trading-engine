using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a single OHLCV (Open, High, Low, Close, Volume) candle bar for a
/// currency pair on a specific timeframe.
/// </summary>
/// <remarks>
/// Candles are the primary data source for all strategy evaluators and the backtesting engine.
/// Live candles arrive with <see cref="IsClosed"/> = <c>false</c> while the bar is still
/// forming; the flag is set to <c>true</c> once the bar's period elapses.
/// Strategy evaluators only consume closed candles to avoid acting on incomplete bars.
/// </remarks>
public class Candle : Entity<long>
{
    /// <summary>The currency pair or instrument this candle belongs to (e.g. "EURUSD").</summary>
    public string  Symbol    { get; set; } = string.Empty;

    /// <summary>The chart timeframe this candle represents (M1, M5, M15, H1, H4, D1, etc.).</summary>
    public Timeframe  Timeframe { get; set; } = Timeframe.H1;

    /// <summary>Price at which the bar opened — the first traded price in the period.</summary>
    public decimal Open      { get; set; }

    /// <summary>Highest price reached during the bar's period.</summary>
    public decimal High      { get; set; }

    /// <summary>Lowest price reached during the bar's period.</summary>
    public decimal Low       { get; set; }

    /// <summary>
    /// Final price at the end of the bar's period.
    /// This is the primary value used by indicators such as SMA, RSI, and Bollinger Bands.
    /// </summary>
    public decimal Close     { get; set; }

    /// <summary>
    /// Traded volume during this bar period.
    /// For FX data this typically represents tick volume (number of price changes)
    /// rather than true market volume, as spot FX is an OTC market.
    /// </summary>
    public decimal Volume    { get; set; }

    /// <summary>
    /// UTC open-time of this candle bar.
    /// Combined with <see cref="Symbol"/> and <see cref="Timeframe"/>, this forms
    /// the natural unique key for a candle.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// <c>true</c> when this bar's time period has elapsed and no further OHLCV updates
    /// are expected. Strategy evaluators filter on this flag to exclude the live forming bar.
    /// </summary>
    public bool IsClosed     { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted    { get; set; }
}
