using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores a weekly Commitment of Traders (COT) positioning snapshot for a single currency
/// as published by the CFTC (Commodity Futures Trading Commission).
/// </summary>
/// <remarks>
/// COT data is released every Friday and covers futures contract positions as of the
/// preceding Tuesday. The report distinguishes between three participant categories:
/// <list type="bullet">
///   <item><description><b>Commercial</b> — hedgers (banks, exporters) who use futures to offset real business risk.</description></item>
///   <item><description><b>Non-Commercial</b> — large speculators (hedge funds, CTAs) whose positioning is the primary sentiment signal.</description></item>
///   <item><description><b>Retail</b> — small non-reportable speculators.</description></item>
/// </list>
/// The <c>SentimentWorker</c> ingests COT data to derive <see cref="SentimentSnapshot"/>
/// records that strategies can use as a macro sentiment filter.
/// </remarks>
public class COTReport : Entity<long>
{
    /// <summary>
    /// Three-letter currency code this report covers (e.g. "USD", "EUR", "GBP").
    /// Matches the currency futures contract tracked by the CFTC.
    /// </summary>
    public string  Currency                     { get; set; } = string.Empty;

    /// <summary>
    /// The Friday date on which this CFTC report was published.
    /// Data reflects positions as of the previous Tuesday.
    /// </summary>
    public DateTime ReportDate                  { get; set; }

    /// <summary>Number of long contracts held by commercial hedgers.</summary>
    public long    CommercialLong               { get; set; }

    /// <summary>Number of short contracts held by commercial hedgers.</summary>
    public long    CommercialShort              { get; set; }

    /// <summary>
    /// Number of long contracts held by large non-commercial speculators (e.g. hedge funds).
    /// Rising non-commercial longs indicate bullish institutional sentiment.
    /// </summary>
    public long    NonCommercialLong            { get; set; }

    /// <summary>
    /// Number of short contracts held by large non-commercial speculators.
    /// Rising non-commercial shorts indicate bearish institutional sentiment.
    /// </summary>
    public long    NonCommercialShort           { get; set; }

    /// <summary>Number of long contracts held by small retail speculators (non-reportable).</summary>
    public long    RetailLong                   { get; set; }

    /// <summary>Number of short contracts held by small retail speculators (non-reportable).</summary>
    public long    RetailShort                  { get; set; }

    /// <summary>
    /// Net non-commercial positioning = <see cref="NonCommercialLong"/> − <see cref="NonCommercialShort"/>.
    /// Positive values signal net bullish institutional bias; negative values signal net bearish bias.
    /// This is the primary sentiment indicator derived from COT data.
    /// </summary>
    public decimal NetNonCommercialPositioning  { get; set; }

    /// <summary>
    /// Week-over-week change in <see cref="NetNonCommercialPositioning"/>.
    /// A large positive change signals accelerating bullish momentum; a large negative change
    /// signals accelerating bearish momentum among speculative traders.
    /// </summary>
    public decimal NetPositioningChangeWeekly   { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted                    { get; set; }
}
