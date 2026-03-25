using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.RiskProfiles.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background worker that computes a rolling Pearson correlation matrix from daily close
/// prices stored in the <see cref="Candle"/> table. Updates an in-memory dictionary that
/// the risk checker reads via <see cref="ICorrelationMatrixProvider"/>.
///
/// Runs once daily (configurable). Uses the last 60 daily closes per symbol pair.
/// Only computes for symbols with active currency pair records.
/// </summary>
public class CorrelationMatrixWorker : BackgroundService, ICorrelationMatrixProvider
{
    private const int LookbackDays = 60;
    private static readonly TimeSpan ComputeInterval = TimeSpan.FromHours(6);

    private readonly ILogger<CorrelationMatrixWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly ConcurrentDictionary<string, decimal> _correlations = new();
    private DateTime _lastComputedAtUtc;

    public CorrelationMatrixWorker(
        ILogger<CorrelationMatrixWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _metrics      = metrics;
    }

    // ── ICorrelationMatrixProvider ────────────────────────────────────────

    public IReadOnlyDictionary<string, decimal> GetCorrelations() => _correlations;
    public DateTime LastComputedAtUtc => _lastComputedAtUtc;

    // ── Worker loop ──────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CorrelationMatrixWorker starting (lookback={Lookback}d, interval={Interval}h)",
            LookbackDays, ComputeInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ComputeCorrelationsAsync(stoppingToken);
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

            await Task.Delay(ComputeInterval, stoppingToken);
        }

        _logger.LogInformation("CorrelationMatrixWorker stopped");
    }

    private async Task ComputeCorrelationsAsync(CancellationToken ct)
    {
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
            _logger.LogDebug("CorrelationMatrixWorker: fewer than 2 active symbols — skipping");
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-LookbackDays);

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
                g => g.Select(c => (Date: c.Timestamp.Date, Price: c.Close)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        // Only consider symbols with at least 20 daily closes
        var eligibleSymbols = closesBySymbol
            .Where(kvp => kvp.Value.Count >= 20)
            .Select(kvp => kvp.Key)
            .OrderBy(s => s)
            .ToList();

        if (eligibleSymbols.Count < 2)
        {
            _logger.LogDebug("CorrelationMatrixWorker: fewer than 2 eligible symbols with enough history — skipping");
            return;
        }

        int pairsComputed = 0;
        var newCorrelations = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        // Compute pairwise Pearson correlations on aligned daily returns
        for (int i = 0; i < eligibleSymbols.Count; i++)
        {
            for (int j = i + 1; j < eligibleSymbols.Count; j++)
            {
                try
                {
                    var symA = eligibleSymbols[i];
                    var symB = eligibleSymbols[j];

                    var returnsA = ComputeDailyReturns(closesBySymbol[symA]);
                    var returnsB = ComputeDailyReturns(closesBySymbol[symB]);

                    // Align on common dates
                    var (alignedA, alignedB) = AlignReturns(returnsA, returnsB);

                    if (alignedA.Count < 15)
                        continue; // Not enough overlapping data points

                    decimal corr = PearsonCorrelation(alignedA, alignedB);
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

        // Atomic swap — replace all entries
        _correlations.Clear();
        foreach (var kvp in newCorrelations)
            _correlations[kvp.Key] = kvp.Value;

        _lastComputedAtUtc = DateTime.UtcNow;

        _logger.LogInformation(
            "CorrelationMatrixWorker: computed {Pairs} correlation pairs across {Symbols} symbols (lookback={Days}d)",
            pairsComputed, eligibleSymbols.Count, LookbackDays);
    }

    // ── Math helpers ─────────────────────────────────────────────────────

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
        var dateMapB = returnsB.ToDictionary(r => r.Date, r => r.Return);
        var alignedA = new List<decimal>();
        var alignedB = new List<decimal>();

        foreach (var (date, ret) in returnsA)
        {
            if (dateMapB.TryGetValue(date, out decimal retB))
            {
                alignedA.Add(ret);
                alignedB.Add(retB);
            }
        }

        return (alignedA, alignedB);
    }

    /// <summary>
    /// Exponential decay half-life in data points. With 60 daily closes, a half-life of 20
    /// means returns from 20 days ago carry half the weight of today's return, and returns
    /// from 40 days ago carry one quarter. This biases the correlation towards recent
    /// market behaviour without discarding older data entirely.
    /// </summary>
    private const double DecayHalfLife = 20.0;

    /// <summary>
    /// Weighted Pearson correlation with exponential decay. The most recent observation
    /// (index = n-1) receives weight 1.0; older observations decay exponentially with
    /// <see cref="DecayHalfLife"/>. Falls back to unweighted correlation when all weights
    /// are equal (e.g. very short series).
    /// </summary>
    private static decimal PearsonCorrelation(List<decimal> x, List<decimal> y)
    {
        int n = x.Count;
        if (n == 0) return 0;

        double lambda = Math.Log(2.0) / DecayHalfLife;

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

        if (denominator == 0) return 0;

        return Math.Round((decimal)(numerator / denominator), 4);
    }
}
