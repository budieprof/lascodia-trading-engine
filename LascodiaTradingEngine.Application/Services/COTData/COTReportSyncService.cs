using System.Collections.Concurrent;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Sentiment.Commands.IngestCOTReport;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.COTData;

/// <summary>
/// Synchronizes the latest published CFTC Commitment of Traders reports for active currencies.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICOTReportSyncService))]
public sealed class COTReportSyncService : ICOTReportSyncService
{
    private const int MaxConcurrentFeedRequests = 4;

    private readonly IReadApplicationDbContext _readContext;
    private readonly IMediator _mediator;
    private readonly ICOTDataFeed _cotFeed;
    private readonly ILogger<COTReportSyncService> _logger;

    public COTReportSyncService(
        IReadApplicationDbContext readContext,
        IMediator mediator,
        ICOTDataFeed cotFeed,
        ILogger<COTReportSyncService> logger)
    {
        _readContext = readContext;
        _mediator = mediator;
        _cotFeed = cotFeed;
        _logger = logger;
    }

    public async Task<COTReportSyncResult> SyncLatestPublishedReportsAsync(CancellationToken ct)
    {
        var db = _readContext.GetDbContext();

        var activePairs = await db.Set<CurrencyPair>()
            .AsNoTracking()
            .Where(x => x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        if (activePairs.Count == 0)
        {
            _logger.LogDebug("COTReportSyncService: no active currency pairs found, skipping");
            return EmptyResult();
        }

        var allCurrencies = ExtractCurrencies(activePairs);
        if (allCurrencies.Count == 0)
        {
            _logger.LogDebug("COTReportSyncService: no valid currencies resolved from active pairs, skipping");
            return EmptyResult(activePairs.Count);
        }

        var supportedCurrencies = allCurrencies
            .Where(_cotFeed.SupportsCurrency)
            .OrderBy(c => c)
            .ToList();

        var unsupportedCurrencies = allCurrencies
            .Except(supportedCurrencies, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        if (unsupportedCurrencies.Count > 0)
        {
            _logger.LogDebug(
                "COTReportSyncService: skipping unsupported currencies with no CFTC mapping: {Currencies}",
                string.Join(", ", unsupportedCurrencies));
        }

        if (supportedCurrencies.Count == 0)
        {
            _logger.LogDebug("COTReportSyncService: no supported COT currencies found among active pairs, skipping");
            return new COTReportSyncResult(
                ActivePairCount: activePairs.Count,
                CurrencyCount: allCurrencies.Count,
                SupportedCurrencyCount: 0,
                UnsupportedCurrencyCount: unsupportedCurrencies.Count,
                PublishedReportCount: 0,
                CreatedCount: 0,
                RepairedCount: 0,
                UnchangedCount: 0,
                UnavailableCount: 0,
                FetchFailedCount: 0,
                PersistFailedCount: 0);
        }

        var fetchResult = await FetchLatestPublishedReportsAsync(supportedCurrencies, ct);
        var latestPublishedReports = fetchResult.Reports;

        if (latestPublishedReports.Count == 0)
        {
            _logger.LogDebug(
                "COTReportSyncService: no published reports available for supported currencies ({Count})",
                supportedCurrencies.Count);

            return new COTReportSyncResult(
                ActivePairCount: activePairs.Count,
                CurrencyCount: allCurrencies.Count,
                SupportedCurrencyCount: supportedCurrencies.Count,
                UnsupportedCurrencyCount: unsupportedCurrencies.Count,
                PublishedReportCount: 0,
                CreatedCount: 0,
                RepairedCount: 0,
                UnchangedCount: 0,
                UnavailableCount: fetchResult.UnavailableCount,
                FetchFailedCount: fetchResult.FetchFailedCount,
                PersistFailedCount: 0);
        }

        var reportDates = latestPublishedReports
            .Select(x => x.Data.ReportDate.Date)
            .Distinct()
            .ToList();

        var existingReports = await db.Set<COTReport>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted
                     && supportedCurrencies.Contains(x.Currency)
                     && reportDates.Contains(x.ReportDate))
            .ToListAsync(ct);

        var existingByKey = existingReports.ToDictionary(
            x => BuildReportKey(x.Currency, x.ReportDate),
            StringComparer.OrdinalIgnoreCase);

        int createdCount = 0;
        int repairedCount = 0;
        int unchangedCount = 0;
        int persistFailedCount = 0;

        foreach (var report in latestPublishedReports)
        {
            try
            {
                existingByKey.TryGetValue(
                    BuildReportKey(report.Currency, report.Data.ReportDate),
                    out var existing);

                if (existing != null && Matches(existing, report.Data))
                {
                    unchangedCount++;
                    continue;
                }

                await _mediator.Send(ToCommand(report.Currency, report.Data), ct);

                decimal netNonComm = report.Data.NonCommercialLong - report.Data.NonCommercialShort;
                if (existing == null)
                {
                    createdCount++;
                    _logger.LogInformation(
                        "COTReportSyncService: ingested new COT report for {Currency} dated {Date:yyyy-MM-dd} - NetNonComm={Net:+#;-#;0}, OI={OI:N0}",
                        report.Currency, report.Data.ReportDate, netNonComm, report.Data.TotalOpenInterest);
                }
                else
                {
                    repairedCount++;
                    _logger.LogInformation(
                        "COTReportSyncService: refreshed stored COT report for {Currency} dated {Date:yyyy-MM-dd} - NetNonComm={Net:+#;-#;0}, OI={OI:N0}",
                        report.Currency, report.Data.ReportDate, netNonComm, report.Data.TotalOpenInterest);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                persistFailedCount++;
                _logger.LogError(
                    ex,
                    "COTReportSyncService: failed to persist COT report for {Currency} dated {Date:yyyy-MM-dd}",
                    report.Currency,
                    report.Data.ReportDate);
            }
        }

        return new COTReportSyncResult(
            ActivePairCount: activePairs.Count,
            CurrencyCount: allCurrencies.Count,
            SupportedCurrencyCount: supportedCurrencies.Count,
            UnsupportedCurrencyCount: unsupportedCurrencies.Count,
            PublishedReportCount: latestPublishedReports.Count,
            CreatedCount: createdCount,
            RepairedCount: repairedCount,
            UnchangedCount: unchangedCount,
            UnavailableCount: fetchResult.UnavailableCount,
            FetchFailedCount: fetchResult.FetchFailedCount,
            PersistFailedCount: persistFailedCount);
    }

    private async Task<FetchReportsResult> FetchLatestPublishedReportsAsync(
        IReadOnlyCollection<string> supportedCurrencies,
        CancellationToken ct)
    {
        var reports = new ConcurrentBag<CurrencyCOTReport>();
        int unavailableCount = 0;
        int fetchFailedCount = 0;

        await Parallel.ForEachAsync(
            supportedCurrencies,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Min(MaxConcurrentFeedRequests, supportedCurrencies.Count)
            },
            async (currency, token) =>
            {
                try
                {
                    var data = await _cotFeed.GetLatestPublishedReportAsync(currency, token);
                    if (data == null)
                    {
                        Interlocked.Increment(ref unavailableCount);
                        _logger.LogDebug(
                            "COTReportSyncService: no published COT data currently available for {Currency}",
                            currency);
                        return;
                    }

                    reports.Add(new CurrencyCOTReport(currency, data));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref fetchFailedCount);
                    _logger.LogWarning(
                        ex,
                        "COTReportSyncService: failed to fetch latest published COT report for {Currency}",
                        currency);
                }
            });

        return new FetchReportsResult(
            reports.OrderBy(x => x.Currency, StringComparer.OrdinalIgnoreCase).ToList(),
            unavailableCount,
            fetchFailedCount);
    }

    private static COTReportSyncResult EmptyResult(int activePairCount = 0) =>
        new(
            ActivePairCount: activePairCount,
            CurrencyCount: 0,
            SupportedCurrencyCount: 0,
            UnsupportedCurrencyCount: 0,
            PublishedReportCount: 0,
            CreatedCount: 0,
            RepairedCount: 0,
            UnchangedCount: 0,
            UnavailableCount: 0,
            FetchFailedCount: 0,
            PersistFailedCount: 0);

    private static List<string> ExtractCurrencies(IEnumerable<CurrencyPair> activePairs)
    {
        var currencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in activePairs)
        {
            bool baseAdded = TryAddCurrency(currencies, pair.BaseCurrency);
            bool quoteAdded = TryAddCurrency(currencies, pair.QuoteCurrency);

            var normalizedSymbol = SymbolNormalizer.Normalize(pair.Symbol);

            if (!baseAdded && normalizedSymbol.Length >= 3)
                TryAddCurrency(currencies, normalizedSymbol[..3]);

            if (!quoteAdded && normalizedSymbol.Length >= 6)
                TryAddCurrency(currencies, normalizedSymbol[3..6]);
        }

        return currencies
            .OrderBy(c => c)
            .ToList();
    }

    private static bool TryAddCurrency(HashSet<string> currencies, string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return false;

        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsLetter))
            return false;

        currencies.Add(normalized);
        return true;
    }

    private static bool Matches(COTReport existing, COTPositioningData data)
    {
        return existing.ReportDate.Date == data.ReportDate.Date
            && existing.CommercialLong == data.CommercialLong
            && existing.CommercialShort == data.CommercialShort
            && existing.NonCommercialLong == data.NonCommercialLong
            && existing.NonCommercialShort == data.NonCommercialShort
            && existing.RetailLong == data.RetailLong
            && existing.RetailShort == data.RetailShort
            && existing.TotalOpenInterest == data.TotalOpenInterest;
    }

    private static IngestCOTReportCommand ToCommand(string currency, COTPositioningData data)
    {
        return new IngestCOTReportCommand
        {
            Symbol = currency,
            ReportDate = data.ReportDate.Date,
            CommercialLong = data.CommercialLong,
            CommercialShort = data.CommercialShort,
            NonCommercialLong = data.NonCommercialLong,
            NonCommercialShort = data.NonCommercialShort,
            RetailLong = data.RetailLong,
            RetailShort = data.RetailShort,
            TotalOpenInterest = data.TotalOpenInterest
        };
    }

    private static string BuildReportKey(string currency, DateTime reportDate)
        => $"{currency.ToUpperInvariant()}|{reportDate:yyyyMMdd}";

    private sealed record CurrencyCOTReport(string Currency, COTPositioningData Data);

    private sealed record FetchReportsResult(
        IReadOnlyList<CurrencyCOTReport> Reports,
        int UnavailableCount,
        int FetchFailedCount);
}
