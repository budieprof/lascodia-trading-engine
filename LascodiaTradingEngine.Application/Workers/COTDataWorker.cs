using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Sentiment.Commands.IngestCOTReport;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that automatically ingests CFTC Commitment of Traders (COT) data
/// for all active currency pairs on the CFTC's weekly release schedule (every Friday,
/// covering positions as of the preceding Tuesday).
/// </summary>
/// <remarks>
/// <b>Role in the trading engine:</b>
/// COT reports reveal the net directional positioning of three key market participant
/// groups: Commercial hedgers, Non-Commercial speculators (large funds), and small
/// retail traders. These positioning extremes are among the most reliable leading
/// indicators in forex markets. The engine uses COT data as a macro-level filter:
/// strategies may require non-commercial positioning to align with the trade direction,
/// or use extreme commercial hedging as a contrarian signal.
///
/// <b>CFTC release cadence:</b>
/// Reports are published each Friday afternoon (Eastern Time) covering open interest
/// as of the prior Tuesday's close. Each report covers a one-week period, so a new
/// ingestion is triggered whenever the most recently stored report is more than 7 days old
/// (<see cref="ReportCadence"/>). The reference date used for each report is the
/// most recent Tuesday (<see cref="GetLastTuesdayUTC"/>), matching the CFTC data cutoff.
///
/// <b>Polling cadence:</b>
/// The worker polls hourly (<see cref="PollingInterval"/>) but only performs ingestion
/// when a new report week is due. This hourly polling ensures the engine picks up the new
/// report relatively quickly after the Friday release without requiring exact scheduling.
///
/// <b>Base currency extraction:</b>
/// COT data is published per currency (e.g. "EUR", "GBP") rather than per pair
/// (e.g. "EURUSD"). The worker extracts the base currency from the first 3 characters of
/// each pair's symbol and deduplicates the list to avoid fetching the same currency twice
/// when multiple pairs share the same base (e.g. EURUSD and EURGBP both need EUR data).
///
/// <b>Data source:</b>
/// Stub values are returned by <see cref="FetchCOTDataStubAsync"/> during development.
/// Replace with a real HTTP call to the CFTC public API or a commercial data vendor
/// (e.g. Quandl/NASDAQ Data Link, Barchart) to receive live data.
///
/// <b>Key COT metrics:</b>
/// <list type="bullet">
///   <item><c>CommercialLong / CommercialShort</c> — producers and banks hedging real exposures;
///         often act as smart money in trending markets.</item>
///   <item><c>NonCommercialLong / NonCommercialShort</c> — large speculative funds (CTAs, hedge
///         funds); their positioning tends to be trend-following.</item>
///   <item><c>NetNonCommercial = NonCommercialLong − NonCommercialShort</c> — the most widely
///         watched COT metric; logged on each ingestion for quick monitoring.</item>
///   <item><c>TotalOpenInterest</c> — total outstanding contracts; rising OI in a trend confirms
///         participation; falling OI suggests a weakening move.</item>
/// </list>
/// </remarks>
public class COTDataWorker : BackgroundService
{
    private readonly ILogger<COTDataWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Polling interval — check hourly whether a new report week is available.
    /// Hourly polling ensures the engine ingests a new Friday release within ~1 hour
    /// of publication without requiring a precise cron-style schedule.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// CFTC releases weekly data; a new ingestion is triggered every 7 days.
    /// Compared against the gap between the most recent stored report date and the
    /// current CFTC cutoff date (last Tuesday).
    /// </summary>
    private static readonly TimeSpan ReportCadence = TimeSpan.FromDays(7);

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for operational and diagnostic messages.</param>
    /// <param name="scopeFactory">
    /// Factory used to create a short-lived DI scope per ingestion pass,
    /// ensuring scoped services (MediatR, EF Core DbContext) are properly disposed.
    /// </param>
    public COTDataWorker(ILogger<COTDataWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure.
    /// Runs a continuous polling loop, calling <see cref="IngestIfDueAsync"/> on each cycle.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("COTDataWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IngestIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during graceful shutdown — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Catch-all so a transient error (e.g. network timeout to CFTC API)
                // does not kill the worker. Next cycle will retry after PollingInterval.
                _logger.LogError(ex, "Unexpected error in COTDataWorker polling loop");
            }

            // Wait before the next readiness check. Task.Delay respects cancellation
            // so the worker shuts down promptly even mid-wait.
            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("COTDataWorker stopped");
    }

    /// <summary>
    /// Checks whether a new COT report week has elapsed and, if so, fetches and ingests
    /// data for all unique base currencies derived from active currency pairs.
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <remarks>
    /// The method is idempotent with respect to the weekly cycle: it exits early
    /// (with a debug log) on all but the first hourly poll after a new report is due.
    /// This prevents duplicate ingestion while allowing the worker to remain on its
    /// simple hourly schedule without external coordination.
    /// </remarks>
    private async Task IngestIfDueAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load active currency pairs to determine which base currencies need reports.
        // EF global filter excludes soft-deleted rows; IsActive is a domain-level toggle.
        var activePairs = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (activePairs.Count == 0)
        {
            _logger.LogDebug("COTDataWorker: no active currency pairs found, skipping");
            return;
        }

        // Extract unique base currencies (first 3 chars of each symbol, e.g. "EUR" from "EURUSD").
        // Deduplication prevents fetching EUR data twice when both EURUSD and EURGBP are active.
        var baseCurrencies = activePairs
            .Select(p => p.Symbol.Length >= 3 ? p.Symbol[..3].ToUpperInvariant() : p.Symbol.ToUpperInvariant())
            .Distinct()
            .ToList();

        // Check the most recent report date across all stored COT records.
        // Using a single global max-date query avoids per-currency staleness checks
        // and is sufficient because CFTC releases all currencies simultaneously.
        var latestReportDate = await readContext.GetDbContext()
            .Set<COTReport>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.ReportDate)
            .Select(x => (DateTime?)x.ReportDate)
            .FirstOrDefaultAsync(ct);

        // The CFTC data cutoff is always the most recent Tuesday (data as of Tuesday's close).
        var reportReferenceDate = GetLastTuesdayUTC();

        // Skip ingestion if the stored report is from this week's cutoff or later.
        // The gap check uses strict less-than so the exact 7-day boundary triggers ingestion.
        if (latestReportDate.HasValue &&
            reportReferenceDate - latestReportDate.Value < ReportCadence)
        {
            _logger.LogDebug(
                "COTDataWorker: last report date {LastDate:yyyy-MM-dd} is current, no ingestion needed",
                latestReportDate.Value);
            return;
        }

        _logger.LogInformation(
            "COTDataWorker: ingesting COT reports for {Count} base currencies, reference date {Date:yyyy-MM-dd}",
            baseCurrencies.Count, reportReferenceDate);

        foreach (var currency in baseCurrencies)
        {
            try
            {
                // Fetch COT data — stub values used until a live CFTC feed is wired up.
                // Replace FetchCOTDataStubAsync with a real HTTP call to the CFTC API
                // (https://www.cftc.gov/MarketReports/CommitmentsofTraders/index.htm)
                // or a data vendor such as Quandl/NASDAQ Data Link or Barchart.
                var (commercialLong, commercialShort, nonCommLong, nonCommShort) =
                    await FetchCOTDataStubAsync(currency, reportReferenceDate, ct);

                // Dispatch through MediatR so validation and event publishing are applied.
                // TotalOpenInterest is computed here from the four component positions rather
                // than returned by the feed to keep the command self-consistent.
                await mediator.Send(new IngestCOTReportCommand
                {
                    Symbol             = currency,
                    ReportDate         = reportReferenceDate,
                    CommercialLong     = commercialLong,
                    CommercialShort    = commercialShort,
                    NonCommercialLong  = nonCommLong,
                    NonCommercialShort = nonCommShort,
                    // Total open interest = sum of all four position legs across participant groups.
                    TotalOpenInterest  = commercialLong + commercialShort + nonCommLong + nonCommShort
                }, ct);

                // Log the net speculative position as the primary headline metric.
                // Positive = large specs net long; negative = net short.
                _logger.LogInformation(
                    "COTDataWorker: ingested COT report for {Currency} — NetNonComm={Net:+#;-#;0}",
                    currency, nonCommLong - nonCommShort);
            }
            catch (Exception ex)
            {
                // Isolate per-currency failures so one bad currency does not abort the batch.
                _logger.LogError(ex, "COTDataWorker: failed to ingest COT report for {Currency}", currency);
            }
        }
    }

    /// <summary>
    /// Returns the most recent Tuesday at midnight UTC — the CFTC data cutoff date.
    /// </summary>
    /// <remarks>
    /// The modular arithmetic <c>((currentDay - Tuesday + 7) % 7)</c> computes the number
    /// of days to subtract to land on the immediately preceding Tuesday regardless of the
    /// current day of the week. When today <em>is</em> Tuesday, <c>daysBack</c> is 0, so
    /// the current day is returned — this is correct because the Tuesday cutoff corresponds
    /// to Tuesday's close, which has already passed by the time this runs.
    /// </remarks>
    private static DateTime GetLastTuesdayUTC()
    {
        var today = DateTime.UtcNow.Date;
        // DayOfWeek enum: Sunday=0, Monday=1, Tuesday=2, ..., Saturday=6.
        // Subtracting Tuesday (2) and taking mod 7 gives days since the last Tuesday.
        int daysBack = ((int)today.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        return today.AddDays(-daysBack);
    }

    /// <summary>
    /// Stub data fetcher used during development when no live CFTC feed is configured.
    /// Returns a tuple of <c>(CommercialLong, CommercialShort, NonCommercialLong, NonCommercialShort)</c>
    /// as contract counts in thousands.
    /// </summary>
    /// <param name="currency">The base currency code (e.g. <c>"EUR"</c>) used to seed deterministic values.</param>
    /// <param name="reportDate">The CFTC cutoff date used as an additional seed component so values differ per week.</param>
    /// <param name="ct">Unused in the stub; present for interface compatibility with a real async HTTP call.</param>
    /// <returns>
    /// Four position values where:
    /// <list type="bullet">
    ///   <item><c>CommercialLong</c> — long contracts held by commercial hedgers.</item>
    ///   <item><c>CommercialShort</c> — short contracts held by commercial hedgers (typically ~10% less than long).</item>
    ///   <item><c>NonCommercialLong</c> — long contracts held by large speculative funds.</item>
    ///   <item><c>NonCommercialShort</c> — short contracts held by large speculative funds.</item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// Values are seeded deterministically using <c>currency.GetHashCode() XOR reportDate.DayOfYear</c>
    /// so the same currency/date combination always produces the same figures — useful for
    /// reproducible tests and development scenarios without a live API key.
    ///
    /// The ratio between commercial and non-commercial positions (roughly 2.5:1) reflects
    /// realistic CFTC data distributions for major currency pairs.
    ///
    /// <b>Replace this method</b> with a real HTTP integration before deploying to production.
    /// </remarks>
    private static Task<(decimal, decimal, decimal, decimal)> FetchCOTDataStubAsync(
        string currency, DateTime reportDate, CancellationToken ct)
    {
        // Seed with both the currency and the report date's day-of-year so different
        // currencies on different weeks produce distinct deterministic values.
        var hash = Math.Abs(currency.GetHashCode() ^ reportDate.DayOfYear);

        // Base position in the range 100,000–150,000 contracts (typical for major currency futures).
        decimal basePosition = 100_000m + hash % 50_000m;

        return Task.FromResult((
            basePosition,              // CommercialLong  — hedgers are approximately balanced
            basePosition * 0.9m,       // CommercialShort — slight net-long hedge (typical)
            basePosition * 0.4m,       // NonCommercialLong  — speculative longs
            basePosition * 0.35m));    // NonCommercialShort — speculative shorts (slight net-long spec)
    }
}
