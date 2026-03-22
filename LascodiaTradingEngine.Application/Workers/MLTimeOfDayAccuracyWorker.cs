using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes per-UTC-hour direction accuracy for each active ML model and persists
/// the results as <see cref="MLModelHourlyAccuracy"/> rows (24 buckets per model).
///
/// <b>Motivation:</b> The four-bucket <c>MLSessionAccuracyWorker</c> (Asian / London /
/// LondonNYOverlap / NewYork) is too coarse for intraday signal scheduling.
/// A model may perform well during the London session overall (07:00–12:59 UTC)
/// but be consistently wrong during the first hour (07:00 UTC) due to gap-open
/// volatility — an effect that is diluted in the session average. At hourly
/// granularity this weakness is directly visible and can be acted on.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLHourlyAccuracy:PollIntervalSeconds</c> — default 3600 (1 h)</item>
///   <item><c>MLHourlyAccuracy:WindowDays</c>          — look-back window, default 30</item>
///   <item><c>MLHourlyAccuracy:MinPredictions</c>      — minimum per hour bucket, default 10</item>
/// </list>
/// </summary>
public sealed class MLTimeOfDayAccuracyWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    // Config key prefix is "MLHourlyAccuracy" (distinct from "MLSessionAccuracy")
    // to allow independent tuning of window and minimum sample requirements.
    private const string CK_PollSecs       = "MLHourlyAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLHourlyAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLHourlyAccuracy:MinPredictions";

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLTimeOfDayAccuracyWorker>    _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory for creating per-poll scoped service lifetimes, ensuring DbContexts
    /// are cleanly disposed after each computation cycle.
    /// </param>
    /// <param name="logger">Structured logger for computation diagnostics.</param>
    public MLTimeOfDayAccuracyWorker(
        IServiceScopeFactory                   scopeFactory,
        ILogger<MLTimeOfDayAccuracyWorker>     logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope for scoped DB context lifetimes.</item>
    ///   <item>Reads the current poll interval from <see cref="EngineConfig"/>.</item>
    ///   <item>Delegates to <see cref="ComputeHourlyAccuracyAsync"/> for the computation.</item>
    ///   <item>Sleeps for <c>pollSecs</c> before the next cycle.</item>
    /// </list>
    /// Non-cancellation exceptions are caught and logged to keep the worker alive
    /// through transient DB or network errors.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTimeOfDayAccuracyWorker started.");

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

                await ComputeHourlyAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Transient errors must not crash the watchdog permanently.
                _logger.LogError(ex, "MLTimeOfDayAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLTimeOfDayAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads global config parameters for the current poll cycle, then iterates
    /// all active ML models and calls <see cref="ComputeForModelAsync"/> for each,
    /// isolating failures per model so that one bad model cannot block the rest.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for fetching models and prediction logs.</param>
    /// <param name="writeCtx">Write DbContext for upserting hourly accuracy rows.</param>
    /// <param name="ct">Cancellation token checked between model iterations.</param>
    private async Task ComputeHourlyAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load config once per cycle to avoid repeated DB round-trips in the loop.
        int windowDays     = await GetConfigAsync<int>(readCtx, CK_WindowDays,     30, ct);
        int minPredictions = await GetConfigAsync<int>(readCtx, CK_MinPredictions, 10, ct);

        // Window start is an inclusive lower-bound timestamp for prediction log queries.
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Only compute for actively deployed models.
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
                    windowStart, minPredictions,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-model failures.
                _logger.LogWarning(ex,
                    "HourlyAccuracy: computation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Computes per-UTC-hour direction accuracy for a single ML model:
    /// <list type="number">
    ///   <item>Loads all resolved prediction logs for the model within the look-back window.</item>
    ///   <item>Groups predictions by their UTC hour (0–23).</item>
    ///   <item>For each hour bucket meeting the minimum sample threshold,
    ///         computes accuracy = correct / total.</item>
    ///   <item>Upserts one <see cref="MLModelHourlyAccuracy"/> row per qualifying hour bucket.</item>
    /// </list>
    /// Up to 24 rows can be produced per model (one per UTC hour), but hours with
    /// fewer than <paramref name="minPredictions"/> resolved outcomes are skipped to
    /// avoid statistically unreliable estimates. A 30-day window typically yields
    /// 10–30 predictions per hour for active models trading multiple sessions.
    ///
    /// <b>Relationship to <c>MLSessionAccuracyWorker</c>:</b>
    /// Session-level rows aggregate hours 0–6, 7–12, 13–16, and 17–23. This worker
    /// provides finer granularity — for example, exposing that hour 07:00 UTC (London
    /// open gap-volatility) is systematically worse than hours 08:00–12:00.
    /// </summary>
    /// <param name="modelId">Primary key of the ML model being evaluated.</param>
    /// <param name="symbol">Instrument symbol (e.g., "EUR_USD").</param>
    /// <param name="timeframe">Candle timeframe for this model.</param>
    /// <param name="windowStart">Inclusive UTC start of the look-back window.</param>
    /// <param name="minPredictions">Minimum resolved predictions per hour before persisting a row.</param>
    /// <param name="readCtx">Read DbContext for prediction log queries.</param>
    /// <param name="writeCtx">Write DbContext for accuracy row upserts.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ComputeForModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                windowStart,
        int                                     minPredictions,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load resolved prediction logs for this model in the window.
        // Only PredictedAt and DirectionCorrect are selected to keep the dataset minimal.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId      &&
                        l.DirectionCorrect != null          &&
                        l.PredictedAt      >= windowStart   &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, l.DirectionCorrect })
            .ToListAsync(ct);

        if (logs.Count == 0) return;

        // Group by UTC hour (0–23). Each group becomes one row in MLModelHourlyAccuracy.
        // DateTime.Hour returns the UTC hour because PredictedAt is always stored as UTC.
        var byHour = logs
            .GroupBy(l => l.PredictedAt.Hour)
            .ToList();

        var now      = DateTime.UtcNow;
        int upserted = 0;

        foreach (var group in byHour)
        {
            int hourUtc = group.Key;  // 0–23 UTC
            int total   = group.Count();

            // Skip hours with insufficient sample size — avoids misleading 100% / 0%
            // accuracy estimates from just 1 or 2 predictions.
            if (total < minPredictions) continue;

            int    correct  = group.Count(l => l.DirectionCorrect == true);
            double accuracy = (double)correct / total;

            // Upsert strategy: update first; insert on miss.
            // ExecuteUpdateAsync issues a bulk SQL UPDATE with no tracked entity overhead.
            int rows = await writeCtx.Set<MLModelHourlyAccuracy>()
                .Where(r => r.MLModelId == modelId && r.HourUtc == hourUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TotalPredictions,   total)
                    .SetProperty(r => r.CorrectPredictions, correct)
                    .SetProperty(r => r.Accuracy,           accuracy)
                    .SetProperty(r => r.WindowStart,        windowStart)
                    .SetProperty(r => r.ComputedAt,         now),
                    ct);

            if (rows == 0)
            {
                // First time computing this hour for this model — insert.
                writeCtx.Set<MLModelHourlyAccuracy>().Add(new MLModelHourlyAccuracy
                {
                    MLModelId          = modelId,
                    Symbol             = symbol,
                    Timeframe          = timeframe,
                    HourUtc            = hourUtc,
                    TotalPredictions   = total,
                    CorrectPredictions = correct,
                    Accuracy           = accuracy,
                    WindowStart        = windowStart,
                    ComputedAt         = now,
                });
                await writeCtx.SaveChangesAsync(ct);
            }

            upserted++;

            _logger.LogDebug(
                "HourlyAccuracy: model {Id} {Symbol}/{Tf} hour={H:D2}:00 UTC — acc={Acc:P1} n={N}",
                modelId, symbol, timeframe, hourUtc, accuracy, total);
        }

        if (upserted > 0)
            _logger.LogInformation(
                "HourlyAccuracy: model {Id} ({Symbol}/{Tf}) — computed {N} hour bucket(s).",
                modelId, symbol, timeframe, upserted);
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
