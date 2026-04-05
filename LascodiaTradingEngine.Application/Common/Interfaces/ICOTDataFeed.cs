namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Abstraction over a CFTC Commitment of Traders data provider.
/// Implementations may source data from CFTC bulk CSV files, commercial vendors
/// (e.g. Quandl/NASDAQ Data Link, Barchart), or a local cache.
/// </summary>
public interface ICOTDataFeed
{
    /// <summary>
    /// Fetches the COT positioning data for a specific currency and report date.
    /// Returns null if no data is available for the requested currency/date combination.
    /// </summary>
    /// <param name="currency">Three-letter currency code (e.g. "EUR", "GBP", "JPY").</param>
    /// <param name="reportDate">The CFTC data cutoff date (always a Tuesday).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<COTPositioningData?> GetReportAsync(string currency, DateTime reportDate, CancellationToken ct);
}

/// <summary>
/// Raw COT positioning data for a single currency from the CFTC report.
/// Contract counts represent the number of outstanding futures contracts.
/// </summary>
/// <param name="CommercialLong">Long contracts held by commercial hedgers (banks, exporters).</param>
/// <param name="CommercialShort">Short contracts held by commercial hedgers.</param>
/// <param name="NonCommercialLong">Long contracts held by large speculative funds (CTAs, hedge funds).</param>
/// <param name="NonCommercialShort">Short contracts held by large speculative funds.</param>
/// <param name="RetailLong">Long contracts held by non-reportable (small retail) traders.</param>
/// <param name="RetailShort">Short contracts held by non-reportable (small retail) traders.</param>
/// <param name="TotalOpenInterest">Total outstanding contracts across all participant categories.</param>
public record COTPositioningData(
    long CommercialLong,
    long CommercialShort,
    long NonCommercialLong,
    long NonCommercialShort,
    long RetailLong,
    long RetailShort,
    long TotalOpenInterest);
