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
/// ingestion is triggered whenever a currency has no report for the current week's
/// cutoff date. The reference date used for each report is the most recent Tuesday
/// (<see cref="GetLastTuesdayUTC"/>), matching the CFTC data cutoff.
///
/// <b>Polling cadence:</b>
/// The worker polls hourly (<see cref="PollingInterval"/>) but only performs ingestion
/// when a new report week is due. This hourly polling ensures the engine picks up the new
/// report relatively quickly after the Friday release without requiring exact scheduling.
///
/// <b>Currency extraction:</b>
/// COT data is published per currency (e.g. "EUR", "GBP") rather than per pair
/// (e.g. "EURUSD"). The worker extracts both base and quote currencies from each
/// active pair and deduplicates the list to avoid fetching the same currency twice
/// when multiple pairs share currencies.
///
/// <b>Per-currency staleness:</b>
/// Unlike a global max-date check, the worker tracks which currencies already have
/// current-week data. This ensures that if one currency's ingestion fails, it is
/// retried on the next polling cycle without being skipped due to another currency's
/// successful ingestion.
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
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="logger">Structured logger for operational and diagnostic messages.</param>
    /// <param name="scopeFactory">
    /// Factory used to create a short-lived DI scope per ingestion pass,
    /// ensuring scoped services (MediatR, EF Core DbContext, ICOTDataFeed) are properly disposed.
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
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in COTDataWorker polling loop");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("COTDataWorker stopped");
    }

    /// <summary>
    /// Checks whether new COT reports are due on a per-currency basis and ingests
    /// data for all currencies that are missing the current week's report.
    /// </summary>
    /// <param name="ct">Propagated cancellation token.</param>
    /// <remarks>
    /// Per-currency staleness tracking ensures that a partial batch failure (e.g. EUR
    /// succeeds but GBP fails) does not prevent GBP from being retried on the next cycle.
    /// </remarks>
    private async Task IngestIfDueAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readContext   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator      = scope.ServiceProvider.GetRequiredService<IMediator>();
        var cotFeed       = scope.ServiceProvider.GetRequiredService<ICOTDataFeed>();

        // Load active currency pairs to determine which currencies need reports.
        var activePairs = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (activePairs.Count == 0)
        {
            _logger.LogDebug("COTDataWorker: no active currency pairs found, skipping");
            return;
        }

        // Extract both base (chars 0-2) and quote (chars 3-5) currencies from each pair.
        // COT data covers individual currencies, and both sides of a pair carry
        // valuable positioning information (e.g. USDJPY needs both USD and JPY data).
        var allCurrencies = activePairs
            .SelectMany(p =>
            {
                var symbols = new List<string>();
                if (p.Symbol.Length >= 3)
                    symbols.Add(p.Symbol[..3].ToUpperInvariant());
                if (p.Symbol.Length >= 6)
                    symbols.Add(p.Symbol[3..6].ToUpperInvariant());
                return symbols;
            })
            .Distinct()
            .ToList();

        // The CFTC data cutoff is always the most recent Tuesday (data as of Tuesday's close).
        var reportReferenceDate = GetLastTuesdayUTC();

        // Per-currency staleness check: find which currencies already have a report
        // for this week's cutoff date. Only ingest those that are missing.
        var currenciesWithCurrentReport = await readContext.GetDbContext()
            .Set<COTReport>()
            .Where(x => !x.IsDeleted && x.ReportDate == reportReferenceDate)
            .Select(x => x.Currency)
            .ToListAsync(ct);

        var currenciesWithCurrentReportSet = new HashSet<string>(
            currenciesWithCurrentReport, StringComparer.OrdinalIgnoreCase);

        var currenciesNeedingIngestion = allCurrencies
            .Where(c => !currenciesWithCurrentReportSet.Contains(c))
            .ToList();

        if (currenciesNeedingIngestion.Count == 0)
        {
            _logger.LogDebug(
                "COTDataWorker: all {Count} currencies have current-week reports for {Date:yyyy-MM-dd}",
                allCurrencies.Count, reportReferenceDate);
            return;
        }

        _logger.LogInformation(
            "COTDataWorker: ingesting COT reports for {NeedCount}/{TotalCount} currencies, reference date {Date:yyyy-MM-dd}",
            currenciesNeedingIngestion.Count, allCurrencies.Count, reportReferenceDate);

        int successCount = 0;
        int failCount = 0;

        foreach (var currency in currenciesNeedingIngestion)
        {
            try
            {
                var data = await cotFeed.GetReportAsync(currency, reportReferenceDate, ct);

                if (data == null)
                {
                    _logger.LogDebug(
                        "COTDataWorker: no COT data available for {Currency} on {Date:yyyy-MM-dd} (may not be released yet)",
                        currency, reportReferenceDate);
                    continue;
                }

                // Dispatch through MediatR so validation, upsert logic, and
                // NetPositioningChangeWeekly computation are applied by the handler.
                await mediator.Send(new IngestCOTReportCommand
                {
                    Symbol             = currency,
                    ReportDate         = reportReferenceDate,
                    CommercialLong     = data.CommercialLong,
                    CommercialShort    = data.CommercialShort,
                    NonCommercialLong  = data.NonCommercialLong,
                    NonCommercialShort = data.NonCommercialShort,
                    RetailLong         = data.RetailLong,
                    RetailShort        = data.RetailShort,
                    TotalOpenInterest  = data.TotalOpenInterest
                }, ct);

                decimal netNonComm = data.NonCommercialLong - data.NonCommercialShort;
                _logger.LogInformation(
                    "COTDataWorker: ingested COT report for {Currency} — NetNonComm={Net:+#;-#;0}, OI={OI:N0}",
                    currency, netNonComm, data.TotalOpenInterest);

                successCount++;
            }
            catch (Exception ex)
            {
                // Isolate per-currency failures so one bad currency does not abort the batch.
                _logger.LogError(ex, "COTDataWorker: failed to ingest COT report for {Currency}", currency);
                failCount++;
            }
        }

        _logger.LogInformation(
            "COTDataWorker: ingestion complete — {Success} succeeded, {Failed} failed, {Skipped} unavailable",
            successCount, failCount, currenciesNeedingIngestion.Count - successCount - failCount);
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
        int daysBack = ((int)today.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        return today.AddDays(-daysBack);
    }
}
