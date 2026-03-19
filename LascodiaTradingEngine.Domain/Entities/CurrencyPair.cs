using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents the static metadata for a tradeable currency pair or instrument.
/// Defines contract specifications used by the position-sizing engine to convert
/// pip distances into monetary risk values.
/// </summary>
/// <remarks>
/// One row per instrument (e.g. EURUSD, GBPJPY). The risk engine reads these specs
/// to calculate pip value, margin requirements, and valid lot increments before
/// submitting an order to the broker.
/// </remarks>
public class CurrencyPair : Entity<long>
{
    /// <summary>
    /// The standard trading symbol (e.g. "EURUSD", "GBPJPY").
    /// Must match the symbol strings used in <see cref="Strategy"/>, <see cref="Candle"/>,
    /// and <see cref="TradeSignal"/> records exactly.
    /// </summary>
    public string Symbol        { get; set; } = string.Empty;

    /// <summary>The base currency of the pair (e.g. "EUR" in EURUSD).</summary>
    public string BaseCurrency  { get; set; } = string.Empty;

    /// <summary>The quote (counter) currency of the pair (e.g. "USD" in EURUSD).</summary>
    public string QuoteCurrency { get; set; } = string.Empty;

    /// <summary>
    /// Number of decimal places in the price quote for this instrument.
    /// Most major pairs use 5 (e.g. 1.08521); JPY pairs typically use 3 (e.g. 149.221).
    /// Used to convert pip distances to price differences: 1 pip = 10^-(DecimalPlaces-1).
    /// </summary>
    public int    DecimalPlaces { get; set; } = 5;

    /// <summary>
    /// Units of base currency per standard lot.
    /// For most FX pairs this is 100 000; micro-lot instruments may use 1 000.
    /// Used in pip value and margin calculations.
    /// </summary>
    public decimal ContractSize { get; set; } = 100_000m;

    /// <summary>
    /// Minimum tradeable lot size (e.g. 0.01 = 1 micro lot).
    /// Orders below this size are rejected by the risk checker before submission.
    /// </summary>
    public decimal MinLotSize   { get; set; } = 0.01m;

    /// <summary>
    /// Maximum tradeable lot size in a single order.
    /// Orders above this are capped or split by the order sizing logic.
    /// </summary>
    public decimal MaxLotSize   { get; set; } = 100m;

    /// <summary>
    /// Minimum increment between valid lot sizes (e.g. 0.01 means lots must be a
    /// multiple of 0.01). The position-sizing engine rounds calculated lot sizes
    /// down to the nearest valid step.
    /// </summary>
    public decimal LotStep      { get; set; } = 0.01m;

    /// <summary>
    /// When <c>true</c>, strategies and orders may reference this instrument.
    /// Inactive pairs are excluded from strategy evaluation and signal generation.
    /// </summary>
    public bool   IsActive      { get; set; } = true;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool   IsDeleted     { get; set; }
}
