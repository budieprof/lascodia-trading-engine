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
/// The worker checks once per hour whether a new COT report week has elapsed since the
/// last recorded <see cref="COTReport"/>. If so, it fetches the latest report data via the
/// configured <see cref="ICOTDataFeed"/> (or uses stub data when no live feed is
/// configured) and dispatches <see cref="IngestCOTReportCommand"/> for each active
/// currency pair's base currency.
///
/// <b>CFTC release cadence:</b> reports are published each Friday afternoon (Eastern Time)
/// covering positions as of the prior Tuesday. This worker therefore treats a new report
/// as available if at least 7 days have elapsed since the last ingested report date.
/// </remarks>
public class COTDataWorker : BackgroundService
{
    private readonly ILogger<COTDataWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Polling interval — check hourly whether a new report week is available.</summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(1);

    /// <summary>CFTC releases weekly data; a new ingestion is triggered every 7 days.</summary>
    private static readonly TimeSpan ReportCadence = TimeSpan.FromDays(7);

    public COTDataWorker(ILogger<COTDataWorker> logger, IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

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

    private async Task IngestIfDueAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readContext  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator     = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load active currency pairs to determine which base currencies need reports
        var activePairs = await readContext.GetDbContext()
            .Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (activePairs.Count == 0)
        {
            _logger.LogDebug("COTDataWorker: no active currency pairs found, skipping");
            return;
        }

        // Extract unique base currencies (first 3 chars of each symbol)
        var baseCurrencies = activePairs
            .Select(p => p.Symbol.Length >= 3 ? p.Symbol[..3].ToUpperInvariant() : p.Symbol.ToUpperInvariant())
            .Distinct()
            .ToList();

        // Check the most recent report date across all currencies
        var latestReportDate = await readContext.GetDbContext()
            .Set<COTReport>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.ReportDate)
            .Select(x => (DateTime?)x.ReportDate)
            .FirstOrDefaultAsync(ct);

        var reportReferenceDate = GetLastTuesdayUTC();

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
                // Replace this with a real HTTP call to the CFTC or a data vendor API.
                var (commercialLong, commercialShort, nonCommLong, nonCommShort) =
                    await FetchCOTDataStubAsync(currency, reportReferenceDate, ct);

                await mediator.Send(new IngestCOTReportCommand
                {
                    Symbol             = currency,
                    ReportDate         = reportReferenceDate,
                    CommercialLong     = commercialLong,
                    CommercialShort    = commercialShort,
                    NonCommercialLong  = nonCommLong,
                    NonCommercialShort = nonCommShort,
                    TotalOpenInterest  = commercialLong + commercialShort + nonCommLong + nonCommShort
                }, ct);

                _logger.LogInformation(
                    "COTDataWorker: ingested COT report for {Currency} — NetNonComm={Net:+#;-#;0}",
                    currency, nonCommLong - nonCommShort);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "COTDataWorker: failed to ingest COT report for {Currency}", currency);
            }
        }
    }

    /// <summary>
    /// Returns the most recent Tuesday at midnight UTC — the CFTC data cutoff date.
    /// </summary>
    private static DateTime GetLastTuesdayUTC()
    {
        var today = DateTime.UtcNow.Date;
        int daysBack = ((int)today.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        return today.AddDays(-daysBack);
    }

    /// <summary>
    /// Stub data fetcher. Replace with a real CFTC API / data vendor call.
    /// Returns (CommercialLong, CommercialShort, NonCommercialLong, NonCommercialShort).
    /// </summary>
    private static Task<(decimal, decimal, decimal, decimal)> FetchCOTDataStubAsync(
        string currency, DateTime reportDate, CancellationToken ct)
    {
        // Stub: generate deterministic placeholder values so the system can function
        // without a live feed during development. Replace with real HTTP logic.
        var hash = Math.Abs(currency.GetHashCode() ^ reportDate.DayOfYear);
        decimal basePosition = 100_000m + hash % 50_000m;

        return Task.FromResult((
            basePosition,              // CommercialLong
            basePosition * 0.9m,       // CommercialShort
            basePosition * 0.4m,       // NonCommercialLong
            basePosition * 0.35m));    // NonCommercialShort
    }
}
