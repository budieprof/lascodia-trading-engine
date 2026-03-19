using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes per-ATR-quantile direction accuracy for each active ML model and persists
/// the results as <see cref="MLModelVolatilityAccuracy"/> rows.
///
/// <b>Motivation:</b> Models calibrated on mixed-volatility data often perform well during
/// normal conditions but degrade sharply in high-volatility spikes or unusually quiet
/// sessions. A model might show 59% aggregate accuracy, yet only 48% in high-ATR bars —
/// a dangerous profile that's invisible until live P&amp;L damage occurs.
/// These rows let <c>MLSignalScorer</c> apply per-bucket confidence adjustments.
///
/// <b>Volatility bucketing:</b>
/// Prediction logs in the look-back window are grouped into three tertile buckets
/// (Low / Medium / High) based on the ATR of the closest preceding closed candle at
/// each prediction's timestamp. ATR is computed as the 14-period Wilder-smoothed
/// true range of the candle series in the window.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLVolatilityAccuracy:PollIntervalSeconds</c> — default 3600 (1 h)</item>
///   <item><c>MLVolatilityAccuracy:WindowDays</c>          — look-back window, default 30</item>
///   <item><c>MLVolatilityAccuracy:MinPredictions</c>      — minimum per bucket, default 10</item>
///   <item><c>MLVolatilityAccuracy:AtrPeriod</c>           — ATR period, default 14</item>
/// </list>
/// </summary>
public sealed class MLVolatilityAccuracyWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLVolatilityAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLVolatilityAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLVolatilityAccuracy:MinPredictions";
    private const string CK_AtrPeriod      = "MLVolatilityAccuracy:AtrPeriod";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLVolatilityAccuracyWorker> _logger;

    public MLVolatilityAccuracyWorker(
        IServiceScopeFactory                scopeFactory,
        ILogger<MLVolatilityAccuracyWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLVolatilityAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await ComputeVolatilityAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLVolatilityAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLVolatilityAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    private async Task ComputeVolatilityAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int windowDays     = await GetConfigAsync<int>(readCtx, CK_WindowDays,     30, ct);
        int minPredictions = await GetConfigAsync<int>(readCtx, CK_MinPredictions, 10, ct);
        int atrPeriod      = await GetConfigAsync<int>(readCtx, CK_AtrPeriod,      14, ct);

        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ComputeForModelAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    windowStart, minPredictions, atrPeriod,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "VolatilityAccuracy: computation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task ComputeForModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                windowStart,
        int                                     minPredictions,
        int                                     atrPeriod,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load resolved prediction logs in the window
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId      &&
                        l.DirectionCorrect != null          &&
                        l.PredictedAt      >= windowStart   &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, l.DirectionCorrect })
            .ToListAsync(ct);

        if (logs.Count < minPredictions * 3) // need enough for 3 buckets
        {
            _logger.LogDebug(
                "VolatilityAccuracy: model {Id} ({Symbol}/{Tf}) — only {N} resolved logs, skipping.",
                modelId, symbol, timeframe, logs.Count);
            return;
        }

        // Load candles for the window (include buffer for ATR calculation)
        var candleBufferStart = windowStart.AddDays(-1).AddHours(-atrPeriod);
        var candles = await readCtx.Set<Candle>()
            .Where(c => c.Symbol    == symbol    &&
                        c.Timeframe == timeframe  &&
                        c.Timestamp >= candleBufferStart &&
                        c.IsClosed  && !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .AsNoTracking()
            .ToListAsync(ct);

        if (candles.Count < atrPeriod + 1) return;

        // Compute ATR series (Wilder smoothing)
        var atrMap = ComputeAtrMap(candles, atrPeriod);

        // Match each prediction log to its closest preceding candle's ATR
        var logsWithAtr = new List<(decimal Atr, bool Correct)>(logs.Count);
        foreach (var log in logs)
        {
            // Find the closest closed candle at or before PredictedAt
            decimal? atr = null;
            for (int i = candles.Count - 1; i >= 0; i--)
            {
                if (candles[i].Timestamp <= log.PredictedAt && atrMap.TryGetValue(candles[i].Id, out var a))
                {
                    atr = a;
                    break;
                }
            }
            if (atr.HasValue)
                logsWithAtr.Add((atr.Value, log.DirectionCorrect == true));
        }

        if (logsWithAtr.Count < minPredictions * 3) return;

        // Compute tertile ATR thresholds
        var sortedAtrs = logsWithAtr.Select(x => x.Atr).OrderBy(a => a).ToList();
        decimal thresholdLow    = sortedAtrs[sortedAtrs.Count / 3];
        decimal thresholdHigh   = sortedAtrs[sortedAtrs.Count * 2 / 3];

        var buckets = new[]
        {
            ("Low",    (decimal)0m,            thresholdLow),
            ("Medium", thresholdLow,            thresholdHigh),
            ("High",   thresholdHigh,           decimal.MaxValue),
        };

        var now = DateTime.UtcNow;
        int upserted = 0;

        foreach (var (bucket, atrMin, atrMax) in buckets)
        {
            var inBucket = logsWithAtr
                .Where(x => x.Atr >= atrMin && (bucket == "High" ? x.Atr >= atrMin : x.Atr < atrMax))
                .ToList();

            // Re-apply strict boundaries
            if (bucket == "Low")    inBucket = logsWithAtr.Where(x => x.Atr < thresholdLow).ToList();
            if (bucket == "Medium") inBucket = logsWithAtr.Where(x => x.Atr >= thresholdLow && x.Atr < thresholdHigh).ToList();
            if (bucket == "High")   inBucket = logsWithAtr.Where(x => x.Atr >= thresholdHigh).ToList();

            if (inBucket.Count < minPredictions) continue;

            int    total    = inBucket.Count;
            int    correct  = inBucket.Count(x => x.Correct);
            double accuracy = (double)correct / total;

            decimal bucketAtrLow  = bucket == "Low"    ? 0m           : (bucket == "Medium" ? thresholdLow  : thresholdHigh);
            decimal bucketAtrHigh = bucket == "High"   ? decimal.MaxValue : (bucket == "Medium" ? thresholdHigh : thresholdLow);

            int rows = await writeCtx.Set<MLModelVolatilityAccuracy>()
                .Where(r => r.MLModelId == modelId && r.VolatilityBucket == bucket)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TotalPredictions,   total)
                    .SetProperty(r => r.CorrectPredictions, correct)
                    .SetProperty(r => r.Accuracy,           accuracy)
                    .SetProperty(r => r.AtrThresholdLow,    bucketAtrLow)
                    .SetProperty(r => r.AtrThresholdHigh,   bucketAtrHigh)
                    .SetProperty(r => r.WindowStart,        windowStart)
                    .SetProperty(r => r.ComputedAt,         now),
                    ct);

            if (rows == 0)
            {
                writeCtx.Set<MLModelVolatilityAccuracy>().Add(new MLModelVolatilityAccuracy
                {
                    MLModelId          = modelId,
                    Symbol             = symbol,
                    Timeframe          = timeframe,
                    VolatilityBucket   = bucket,
                    TotalPredictions   = total,
                    CorrectPredictions = correct,
                    Accuracy           = accuracy,
                    AtrThresholdLow    = bucketAtrLow,
                    AtrThresholdHigh   = bucketAtrHigh,
                    WindowStart        = windowStart,
                    ComputedAt         = now,
                });
                await writeCtx.SaveChangesAsync(ct);
            }

            upserted++;

            _logger.LogDebug(
                "VolatilityAccuracy: model {Id} {Symbol}/{Tf} bucket={Bucket} — " +
                "acc={Acc:P1} n={N} atr=[{Lo:F5},{Hi:F5}]",
                modelId, symbol, timeframe, bucket, accuracy, total, bucketAtrLow, bucketAtrHigh);
        }

        if (upserted > 0)
            _logger.LogInformation(
                "VolatilityAccuracy: model {Id} ({Symbol}/{Tf}) — computed {N} volatility bucket(s).",
                modelId, symbol, timeframe, upserted);
    }

    // ── ATR computation ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes a Wilder-smoothed 14-period ATR series for the given sorted candle list.
    /// Returns a map of candle ID → ATR value (only populated where ATR is available).
    /// </summary>
    private static Dictionary<long, decimal> ComputeAtrMap(List<Candle> sorted, int period)
    {
        var result = new Dictionary<long, decimal>(sorted.Count);
        if (sorted.Count < period + 1) return result;

        // Initialise with simple average of first `period` true ranges
        decimal sumTr = 0m;
        for (int i = 1; i <= period; i++)
        {
            var prev = sorted[i - 1];
            var curr = sorted[i];
            decimal tr = Math.Max(curr.High - curr.Low,
                         Math.Max(Math.Abs(curr.High - prev.Close),
                                  Math.Abs(curr.Low  - prev.Close)));
            sumTr += tr;
        }

        decimal atr = sumTr / period;
        result[sorted[period].Id] = atr;

        // Wilder smoothing for the rest
        for (int i = period + 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1];
            var curr = sorted[i];
            decimal tr = Math.Max(curr.High - curr.Low,
                         Math.Max(Math.Abs(curr.High - prev.Close),
                                  Math.Abs(curr.Low  - prev.Close)));
            atr = (atr * (period - 1) + tr) / period;
            result[curr.Id] = atr;
        }

        return result;
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
