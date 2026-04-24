namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Abstraction over a CFTC Commitment of Traders data provider.
/// Implementations may source data from CFTC bulk CSV files, commercial vendors
/// (e.g. Quandl/NASDAQ Data Link, Barchart), or a local cache.
/// </summary>
public interface ICOTDataFeed
{
    /// <summary>
    /// Returns <c>true</c> when the feed can source COT data for the supplied currency.
    /// Allows callers to filter unsupported instruments up front instead of repeatedly
    /// polling for currencies that will never have a matching CFTC contract.
    /// </summary>
    bool SupportsCurrency(string currency);

    /// <summary>
    /// Fetches the latest COT positioning snapshot that has actually been published by
    /// the CFTC for the supplied currency. The returned <see cref="COTPositioningData.ReportDate"/>
    /// is the report's data cutoff date from the archive itself (usually Tuesday, but
    /// occasionally Monday on holiday weeks).
    /// Returns null if no published data is available for the currency.
    /// </summary>
    /// <param name="currency">Three-letter currency code (e.g. "EUR", "GBP", "JPY").</param>
    /// <param name="ct">Cancellation token.</param>
    Task<COTPositioningData?> GetLatestPublishedReportAsync(string currency, CancellationToken ct);
}

/// <summary>
/// Raw COT positioning data for a single currency from the CFTC report.
/// Contract counts represent the number of outstanding futures contracts.
/// </summary>
/// <param name="ReportDate">The report's actual CFTC cutoff date from the published archive.</param>
/// <param name="CommercialLong">Long contracts held by commercial hedgers (banks, exporters).</param>
/// <param name="CommercialShort">Short contracts held by commercial hedgers.</param>
/// <param name="NonCommercialLong">Long contracts held by large speculative funds (CTAs, hedge funds).</param>
/// <param name="NonCommercialShort">Short contracts held by large speculative funds.</param>
/// <param name="RetailLong">Long contracts held by non-reportable (small retail) traders.</param>
/// <param name="RetailShort">Short contracts held by non-reportable (small retail) traders.</param>
/// <param name="TotalOpenInterest">Total outstanding contracts across all participant categories.</param>
public record COTPositioningData(
    DateTime ReportDate,
    long CommercialLong,
    long CommercialShort,
    long NonCommercialLong,
    long NonCommercialShort,
    long RetailLong,
    long RetailShort,
    long TotalOpenInterest);
