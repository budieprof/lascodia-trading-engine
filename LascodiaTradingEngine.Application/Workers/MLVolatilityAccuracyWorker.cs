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
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    // All config is read live from the EngineConfig table each poll cycle.
    private const string CK_PollSecs       = "MLVolatilityAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLVolatilityAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLVolatilityAccuracy:MinPredictions";
    private const string CK_AtrPeriod      = "MLVolatilityAccuracy:AtrPeriod";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLVolatilityAccuracyWorker> _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory for creating per-poll scoped service lifetimes, ensuring DbContexts
    /// are cleanly disposed after each computation cycle.
    /// </param>
    /// <param name="logger">Structured logger for computation diagnostics.</param>
    public MLVolatilityAccuracyWorker(
        IServiceScopeFactory                scopeFactory,
        ILogger<MLVolatilityAccuracyWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope for scoped DB context lifetimes.</item>
    ///   <item>Reads the current poll interval from <see cref="EngineConfig"/>.</item>
    ///   <item>Delegates to <see cref="ComputeVolatilityAccuracyAsync"/> for the computation.</item>
    ///   <item>Sleeps for <c>pollSecs</c> before the next cycle.</item>
    /// </list>
    /// Non-cancellation exceptions are caught and logged to keep the worker alive
    /// through transient DB or network errors.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLVolatilityAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval used when the config key is absent.
            int pollSecs = 3600;

            try
            {
                // Fresh scope per iteration keeps EF change tracking isolated.
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read interval live so operators can tune without restart.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await ComputeVolatilityAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Transient errors must not crash the watchdog permanently.
                _logger.LogError(ex, "MLVolatilityAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLVolatilityAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads global config parameters for the current poll cycle, then iterates
    /// all active ML models and calls <see cref="ComputeForModelAsync"/> for each,
    /// isolating failures per model so that one bad model cannot block the rest.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for fetching models, logs, and candles.</param>
    /// <param name="writeCtx">Write DbContext for upserting volatility accuracy rows.</param>
    /// <param name="ct">Cancellation token checked between model iterations.</param>
    private async Task ComputeVolatilityAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load config once per cycle to avoid repeated DB round-trips in the loop.
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
            // Allow fast shutdown between model computations.
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
                // Isolate per-model failures.
                _logger.LogWarning(ex,
                    "VolatilityAccuracy: computation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Computes per-ATR-quantile direction accuracy for a single ML model:
    /// <list type="number">
    ///   <item>Loads resolved prediction logs within the look-back window.</item>
    ///   <item>Loads the matching candle series (extended back by ATR warm-up period).</item>
    ///   <item>Computes a Wilder ATR series from the candle data (via <see cref="ComputeAtrMap"/>).</item>
    ///   <item>Matches each prediction to the ATR of the most recent closed candle
    ///         at or before <c>PredictedAt</c>.</item>
    ///   <item>Splits predictions into three equal-count tertile buckets
    ///         (Low / Medium / High ATR) using the 33rd and 67th percentile ATR values.</item>
    ///   <item>Computes accuracy per bucket and upserts one
    ///         <see cref="MLModelVolatilityAccuracy"/> row per bucket.</item>
    /// </list>
    /// </summary>
    /// <param name="modelId">Primary key of the ML model being evaluated.</param>
    /// <param name="symbol">Instrument symbol (e.g., "EUR_USD").</param>
    /// <param name="timeframe">Candle timeframe for this model.</param>
    /// <param name="windowStart">Inclusive UTC start of the look-back window.</param>
    /// <param name="minPredictions">Minimum predictions required per volatility bucket.</param>
    /// <param name="atrPeriod">Number of candles used in the Wilder ATR calculation (default 14).</param>
    /// <param name="readCtx">Read DbContext for logs and candle data.</param>
    /// <param name="writeCtx">Write DbContext for upserting accuracy rows.</param>
    /// <param name="ct">Cancellation token.</param>
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
        // Load resolved prediction logs in the window.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId      &&
                        l.DirectionCorrect != null          &&
                        l.PredictedAt      >= windowStart   &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, l.DirectionCorrect })
            .ToListAsync(ct);

        // Need enough data for all three tertile buckets — early exit prevents
        // degenerate bucket sizes that produce misleading accuracy values.
        if (logs.Count < minPredictions * 3) // need enough for 3 buckets
        {
            _logger.LogDebug(
                "VolatilityAccuracy: model {Id} ({Symbol}/{Tf}) — only {N} resolved logs, skipping.",
                modelId, symbol, timeframe, logs.Count);
            return;
        }

        // Load candles for the window plus a buffer sufficient for the ATR warm-up period.
        // Without this buffer, the ATR series would start too late and fail to cover
        // predictions at the very beginning of the accuracy window.
        var candleBufferStart = windowStart.AddDays(-1).AddHours(-atrPeriod);
        var candles = await readCtx.Set<Candle>()
            .Where(c => c.Symbol    == symbol    &&
                        c.Timeframe == timeframe  &&
                        c.Timestamp >= candleBufferStart &&
                        c.IsClosed  && !c.IsDeleted)
            .OrderBy(c => c.Timestamp)   // ascending order required for ATR computation
            .AsNoTracking()
            .ToListAsync(ct);

        if (candles.Count < atrPeriod + 1) return;

        // Compute ATR series using Wilder smoothing.
        // Returns a dictionary of candle ID → ATR value.
        var atrMap = ComputeAtrMap(candles, atrPeriod);

        // Match each prediction log to the ATR of its closest preceding closed candle.
        // We iterate the candle list backwards from the end for each log to find
        // the latest closed candle that preceded the prediction timestamp.
        var logsWithAtr = new List<(decimal Atr, bool Correct)>(logs.Count);
        foreach (var log in logs)
        {
            // Find the closest closed candle at or before PredictedAt.
            decimal? atr = null;
            for (int i = candles.Count - 1; i >= 0; i--)
            {
                if (candles[i].Timestamp <= log.PredictedAt && atrMap.TryGetValue(candles[i].Id, out var a))
                {
                    atr = a;
                    break;
                }
            }
            // Only include logs where an ATR value could be resolved.
            if (atr.HasValue)
                logsWithAtr.Add((atr.Value, log.DirectionCorrect == true));
        }

        // Re-check bucket size after ATR matching (some logs may have been dropped).
        if (logsWithAtr.Count < minPredictions * 3) return;

        // Compute tertile (33rd / 67th percentile) ATR thresholds from the matched set.
        // Tertile bucketing ensures each bucket contains roughly equal numbers of
        // predictions, preventing small-sample accuracy distortion.
        var sortedAtrs = logsWithAtr.Select(x => x.Atr).OrderBy(a => a).ToList();
        decimal thresholdLow    = sortedAtrs[sortedAtrs.Count / 3];       // 33rd percentile
        decimal thresholdHigh   = sortedAtrs[sortedAtrs.Count * 2 / 3];  // 67th percentile

        // Define bucket name and ATR range tuples.
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
            // Initial bucket filter (will be overridden by strict boundary logic below).
            var inBucket = logsWithAtr
                .Where(x => x.Atr >= atrMin && (bucket == "High" ? x.Atr >= atrMin : x.Atr < atrMax))
                .ToList();

            // Re-apply strict, non-overlapping boundaries to guarantee each prediction
            // falls in exactly one bucket. The initial filter above can produce overlaps
            // at the boundary values, so we override it with explicit predicates.
            if (bucket == "Low")    inBucket = logsWithAtr.Where(x => x.Atr < thresholdLow).ToList();
            if (bucket == "Medium") inBucket = logsWithAtr.Where(x => x.Atr >= thresholdLow && x.Atr < thresholdHigh).ToList();
            if (bucket == "High")   inBucket = logsWithAtr.Where(x => x.Atr >= thresholdHigh).ToList();

            if (inBucket.Count < minPredictions) continue;

            int    total    = inBucket.Count;
            int    correct  = inBucket.Count(x => x.Correct);
            double accuracy = (double)correct / total;

            // Derive the canonical ATR range boundaries for this bucket to store in the row.
            decimal bucketAtrLow  = bucket == "Low"    ? 0m           : (bucket == "Medium" ? thresholdLow  : thresholdHigh);
            decimal bucketAtrHigh = bucket == "High"   ? decimal.MaxValue : (bucket == "Medium" ? thresholdHigh : thresholdLow);

            // Upsert strategy: update first; insert on miss.
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
                // First time computing this bucket for this model — insert.
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
    /// Computes a Wilder-smoothed ATR series for the given sorted candle list and
    /// returns a dictionary mapping candle ID → ATR value.
    ///
    /// <b>True Range formula:</b>
    /// <c>TR = max(High − Low, |High − PrevClose|, |Low − PrevClose|)</c>
    ///
    /// <b>Initialisation:</b> The first ATR value is the simple arithmetic mean of the
    /// first <paramref name="period"/> true ranges. This is Wilder's standard bootstrap
    /// approach and avoids the cold-start distortion of a full EMA initialised at zero.
    ///
    /// <b>Wilder smoothing recurrence:</b>
    /// <c>ATR_t = (ATR_{t-1} × (period − 1) + TR_t) / period</c>
    ///
    /// This is mathematically equivalent to an EMA with α = 1/period, but expressed in
    /// the "period" form that Wilder originally defined.
    ///
    /// Only candles at index ≥ <paramref name="period"/> will appear in the result map,
    /// as earlier candles are consumed by the warm-up average.
    /// </summary>
    /// <param name="sorted">
    /// Candle list sorted ascending by timestamp. Must contain at least
    /// <paramref name="period"/> + 1 entries.
    /// </param>
    /// <param name="period">
    /// Number of periods for the ATR calculation. Default is 14 (Wilder's original).
    /// </param>
    /// <returns>
    /// Dictionary mapping each candle's <c>Id</c> to its ATR value.
    /// Only candles from index <paramref name="period"/> onwards are included.
    /// </returns>
    private static Dictionary<long, decimal> ComputeAtrMap(List<Candle> sorted, int period)
    {
        var result = new Dictionary<long, decimal>(sorted.Count);
        if (sorted.Count < period + 1) return result;

        // Bootstrap: compute the simple mean of the first `period` true ranges.
        // Requires pairs of consecutive candles (previous close is needed for TR).
        decimal sumTr = 0m;
        for (int i = 1; i <= period; i++)
        {
            var prev = sorted[i - 1];
            var curr = sorted[i];

            // True Range is the maximum of three range measures:
            // 1. Intra-bar range (High − Low)
            // 2. Gap-up measure  (|High − PrevClose|)
            // 3. Gap-down measure (|Low − PrevClose|)
            decimal tr = Math.Max(curr.High - curr.Low,
                         Math.Max(Math.Abs(curr.High - prev.Close),
                                  Math.Abs(curr.Low  - prev.Close)));
            sumTr += tr;
        }

        // First ATR value = simple mean of the warm-up true ranges.
        decimal atr = sumTr / period;
        result[sorted[period].Id] = atr;

        // Wilder smoothing for all candles after the warm-up period.
        // ATR_t = (ATR_{t-1} * (period - 1) + TR_t) / period
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

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// This allows live tuning of worker thresholds without a service restart.
    /// </summary>
    /// <typeparam name="T">Target CLR type (e.g. <c>int</c>, <c>double</c>).</typeparam>
    /// <param name="ctx">DbContext to query.</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> to look up.</param>
    /// <param name="defaultValue">Fallback value used when the key is missing or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed config value or <paramref name="defaultValue"/>.</returns>
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
