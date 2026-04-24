using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.RiskProfiles.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that computes a rolling Pearson correlation matrix from daily close
/// prices stored in the <see cref="Candle"/> table. Updates an in-memory dictionary that
/// the risk checker reads via <see cref="ICorrelationMatrixProvider"/>.
///
/// Runs every 6 hours and recomputes a snapshot from the last 60 calendar days of closed
/// daily candles for active symbols. Correlations are calculated from aligned daily returns
/// with exponential decay so recent observations carry more weight.
/// </summary>
public class CorrelationMatrixWorker : BackgroundService, ICorrelationMatrixProvider
{
    private static readonly IReadOnlyDictionary<string, decimal> EmptyCorrelations =
        new ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));

    private readonly ILogger<CorrelationMatrixWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly CorrelationMatrixOptions _options;
    private readonly TimeProvider _timeProvider;
    private IReadOnlyDictionary<string, decimal> _correlations = EmptyCorrelations;
    private long _lastComputedAtUtcTicks;
    private long _lastAttemptedAtUtcTicks;

    public CorrelationMatrixWorker(
        ILogger<CorrelationMatrixWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        CorrelationMatrixOptions options,
        TimeProvider timeProvider)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _metrics      = metrics;
        _options      = options;
        _timeProvider = timeProvider;
    }

    // ── ICorrelationMatrixProvider ────────────────────────────────────────

    public IReadOnlyDictionary<string, decimal> GetCorrelations() => Volatile.Read(ref _correlations);
    public DateTime LastComputedAtUtc => new(Interlocked.Read(ref _lastComputedAtUtcTicks), DateTimeKind.Utc);
    public DateTime LastAttemptedAtUtc => new(Interlocked.Read(ref _lastAttemptedAtUtcTicks), DateTimeKind.Utc);

    // ── Worker loop ──────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CorrelationMatrixWorker starting (lookback={Lookback}d, interval={Interval}h)",
            _options.LookbackDays, _options.PollIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CorrelationMatrixWorker: error computing correlation matrix");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "CorrelationMatrix"),
                    new KeyValuePair<string, object?>("reason", "unhandled"));
            }

            await Task.Delay(TimeSpan.FromHours(_options.PollIntervalHours), stoppingToken);
        }

        _logger.LogInformation("CorrelationMatrixWorker stopped");
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        var attemptUtc = _timeProvider.GetUtcNow().UtcDateTime;
        Interlocked.Exchange(ref _lastAttemptedAtUtcTicks, attemptUtc.Ticks);

        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readContext.GetDbContext();

        // Get all active symbols
        var activeSymbols = await db.Set<CurrencyPair>()
            .Where(x => x.IsActive && !x.IsDeleted)
            .Select(x => x.Symbol)
            .ToListAsync(ct);

        if (activeSymbols.Count < 2)
        {
            PublishSnapshot(EmptyCorrelations, attemptUtc);
            _logger.LogDebug("CorrelationMatrixWorker: fewer than 2 active symbols — skipping");
            return 0;
        }

        var cutoff = attemptUtc.AddDays(-_options.LookbackDays);

        // Load daily closes for all active symbols in one query
        var dailyCloses = await db.Set<Candle>()
            .Where(c => !c.IsDeleted
                      && c.Timeframe == Timeframe.D1
                      && c.IsClosed
                      && c.Timestamp >= cutoff
                      && activeSymbols.Contains(c.Symbol))
            .Select(c => new { c.Symbol, c.Timestamp, c.Close })
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        // Group by symbol → sorted list of (date, close)
        var closesBySymbol = dailyCloses
            .GroupBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key.ToUpperInvariant(),
                g => NormalizeDailyCloses(g.Select(c => (c.Timestamp, c.Close))),
                StringComparer.OrdinalIgnoreCase);

        // Only consider symbols with at least 20 daily closes
        var eligibleSymbols = closesBySymbol
            .Where(kvp => kvp.Value.Count >= _options.MinClosesPerSymbol)
            .Select(kvp => kvp.Key)
            .OrderBy(s => s)
            .ToList();

        if (eligibleSymbols.Count < 2)
        {
            PublishSnapshot(EmptyCorrelations, attemptUtc);
            _logger.LogDebug("CorrelationMatrixWorker: fewer than 2 eligible symbols with enough history — skipping");
            return 0;
        }

        int pairsComputed = 0;
        var newCorrelations = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var returnsBySymbol = eligibleSymbols.ToDictionary(
            sym => sym,
            sym => ComputeDailyReturns(closesBySymbol[sym]),
            StringComparer.OrdinalIgnoreCase);

        // Compute pairwise Pearson correlations on aligned daily returns
        for (int i = 0; i < eligibleSymbols.Count; i++)
        {
            for (int j = i + 1; j < eligibleSymbols.Count; j++)
            {
                try
                {
                    var symA = eligibleSymbols[i];
                    var symB = eligibleSymbols[j];

                    var returnsA = returnsBySymbol[symA];
                    var returnsB = returnsBySymbol[symB];

                    // Align on common dates
                    var (alignedA, alignedB) = AlignReturns(returnsA, returnsB);

                    if (alignedA.Count < _options.MinOverlapPoints)
                        continue; // Not enough overlapping data points

                    decimal corr = PearsonCorrelation(alignedA, alignedB, _options.DecayHalfLife);
                    string key = RiskChecker.BuildCorrelationKey(symA, symB);
                    newCorrelations[key] = corr;
                    pairsComputed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "CorrelationMatrixWorker: error computing correlation for {SymA}/{SymB} — skipping pair",
                        eligibleSymbols[i], eligibleSymbols[j]);
                }
            }
        }

        PublishSnapshot(ToReadOnlySnapshot(newCorrelations), attemptUtc);

        _logger.LogInformation(
            "CorrelationMatrixWorker: computed {Pairs} correlation pairs across {Symbols} symbols (lookback={Days}d)",
            pairsComputed, eligibleSymbols.Count, _options.LookbackDays);

        return pairsComputed;
    }

    // ── Math helpers ─────────────────────────────────────────────────────

    private static List<(DateTime Date, decimal Price)> NormalizeDailyCloses(
        IEnumerable<(DateTime Timestamp, decimal Price)> closes)
    {
        var latestCloseByDate = new SortedDictionary<DateTime, (DateTime Timestamp, decimal Price)>();
        foreach (var (timestamp, price) in closes)
        {
            var date = timestamp.Date;
            if (!latestCloseByDate.TryGetValue(date, out var current) || timestamp >= current.Timestamp)
                latestCloseByDate[date] = (timestamp, price);
        }

        return latestCloseByDate
            .Select(kvp => (kvp.Key, kvp.Value.Price))
            .ToList();
    }

    private static List<(DateTime Date, decimal Return)> ComputeDailyReturns(
        List<(DateTime Date, decimal Price)> closes)
    {
        var returns = new List<(DateTime, decimal)>(closes.Count - 1);
        for (int i = 1; i < closes.Count; i++)
        {
            if (closes[i - 1].Price != 0)
            {
                decimal ret = (closes[i].Price - closes[i - 1].Price) / closes[i - 1].Price;
                returns.Add((closes[i].Date, ret));
            }
        }
        return returns;
    }

    private static (List<decimal> A, List<decimal> B) AlignReturns(
        List<(DateTime Date, decimal Return)> returnsA,
        List<(DateTime Date, decimal Return)> returnsB)
    {
        var dateMapA = ToLastWinsReturnMap(returnsA);
        var dateMapB = ToLastWinsReturnMap(returnsB);
        var alignedA = new List<decimal>();
        var alignedB = new List<decimal>();

        foreach (var date in dateMapA.Keys.Intersect(dateMapB.Keys).OrderBy(date => date))
        {
            alignedA.Add(dateMapA[date]);
            alignedB.Add(dateMapB[date]);
        }

        return (alignedA, alignedB);
    }

    private static Dictionary<DateTime, decimal> ToLastWinsReturnMap(
        List<(DateTime Date, decimal Return)> returns)
    {
        var map = new Dictionary<DateTime, decimal>();
        foreach (var (date, value) in returns)
            map[date] = value;

        return map;
    }

    /// <summary>
    /// Weighted Pearson correlation with exponential decay. The most recent observation
    /// (index = n-1) receives weight 1.0; older observations decay exponentially with
    /// the supplied half-life.
    /// </summary>
    private static decimal PearsonCorrelation(List<decimal> x, List<decimal> y, double decayHalfLife)
    {
        int n = x.Count;
        if (n == 0 || n != y.Count) return 0;

        double lambda = Math.Log(2.0) / decayHalfLife;

        // Compute exponential weights: newest observation (i = n-1) has weight 1.0
        double sumW = 0, sumWx = 0, sumWy = 0, sumWxy = 0, sumWx2 = 0, sumWy2 = 0;
        for (int i = 0; i < n; i++)
        {
            double w = Math.Exp(-lambda * (n - 1 - i));
            double xi = (double)x[i];
            double yi = (double)y[i];

            sumW   += w;
            sumWx  += w * xi;
            sumWy  += w * yi;
            sumWxy += w * xi * yi;
            sumWx2 += w * xi * xi;
            sumWy2 += w * yi * yi;
        }

        if (sumW == 0) return 0;

        double numerator = sumW * sumWxy - sumWx * sumWy;
        double denominator = Math.Sqrt(
            (sumW * sumWx2 - sumWx * sumWx) *
            (sumW * sumWy2 - sumWy * sumWy));

        if (denominator <= 0) return 0;

        double correlation = numerator / denominator;
        correlation = Math.Clamp(correlation, -1.0d, 1.0d);
        return Math.Round((decimal)correlation, 4);
    }

    private static IReadOnlyDictionary<string, decimal> ToReadOnlySnapshot(Dictionary<string, decimal> correlations)
        => correlations.Count == 0
            ? EmptyCorrelations
            : new ReadOnlyDictionary<string, decimal>(
                new Dictionary<string, decimal>(correlations, StringComparer.OrdinalIgnoreCase));

    private void PublishSnapshot(IReadOnlyDictionary<string, decimal> correlations, DateTime? computedAtUtc = null)
    {
        Volatile.Write(ref _correlations, correlations);

        if (computedAtUtc is { } value)
            Interlocked.Exchange(ref _lastComputedAtUtcTicks, value.Ticks);
    }
}
