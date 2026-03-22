using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes per-trading-session direction accuracy for each active ML model and persists
/// the results as <see cref="MLModelSessionAccuracy"/> rows.
///
/// <b>Motivation:</b> Forex markets behave structurally differently across sessions.
/// London is typically trending, Asian sessions are often ranging, and the London/New York
/// overlap sees the highest volatility. A model with 57% aggregate accuracy may perform
/// at 63% during London but only 51% during Asian hours. Without session-level granularity,
/// this weakness is invisible until significant live P&amp;L damage has occurred.
/// These rows allow <c>MLSignalScorer</c> to apply per-session confidence adjustments
/// (or suppress signals entirely in chronically underperforming sessions).
///
/// <b>Session classification (UTC):</b>
/// <list type="bullet">
///   <item><b>Asian</b>       — 00:00–06:59 UTC</item>
///   <item><b>London</b>      — 07:00–12:59 UTC</item>
///   <item><b>LondonNYOverlap</b> — 13:00–16:59 UTC</item>
///   <item><b>NewYork</b>     — 17:00–23:59 UTC</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLSessionAccuracy:PollIntervalSeconds</c> — default 3600 (1 h)</item>
///   <item><c>MLSessionAccuracy:WindowDays</c>          — look-back window, default 30</item>
///   <item><c>MLSessionAccuracy:MinPredictions</c>      — minimum per-session sample, default 20</item>
/// </list>
/// </summary>
public sealed class MLSessionAccuracyWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    // All config is read live from the EngineConfig table each poll cycle.
    private const string CK_PollSecs       = "MLSessionAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays     = "MLSessionAccuracy:WindowDays";
    private const string CK_MinPredictions = "MLSessionAccuracy:MinPredictions";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLSessionAccuracyWorker>  _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory for creating per-poll scoped service lifetimes, ensuring DbContexts
    /// are cleanly disposed after each computation cycle.
    /// </param>
    /// <param name="logger">Structured logger for computation diagnostics.</param>
    public MLSessionAccuracyWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLSessionAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope for scoped DB context lifetimes.</item>
    ///   <item>Reads the current poll interval from <see cref="EngineConfig"/>.</item>
    ///   <item>Delegates to <see cref="ComputeSessionAccuracyAsync"/> for the computation.</item>
    ///   <item>Sleeps for <c>pollSecs</c> before the next cycle.</item>
    /// </list>
    /// Non-cancellation exceptions are caught and logged to keep the worker alive
    /// through transient DB or network errors.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSessionAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval used when the config key is absent.
            int pollSecs = 3600;

            try
            {
                // Fresh scope per iteration keeps EF change tracking isolated.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read interval live so operators can tune without restart.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await ComputeSessionAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Transient errors must not crash the watchdog permanently.
                _logger.LogError(ex, "MLSessionAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSessionAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads global config parameters for the current poll cycle, then iterates
    /// all active ML models and calls <see cref="ComputeForModelAsync"/> for each,
    /// isolating failures per model so that one bad model cannot block the rest.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for fetching models and logs.</param>
    /// <param name="writeCtx">Write DbContext for upserting session accuracy rows.</param>
    /// <param name="ct">Cancellation token checked between model iterations.</param>
    private async Task ComputeSessionAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load config once per cycle to avoid repeated DB round-trips in the loop.
        int windowDays     = await GetConfigAsync<int>(readCtx, CK_WindowDays,     30, ct);
        int minPredictions = await GetConfigAsync<int>(readCtx, CK_MinPredictions, 20, ct);

        // Window start is an inclusive lower-bound timestamp for prediction log queries.
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Process each active model independently.
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
                    "SessionAccuracy: computation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Computes per-session direction accuracy for a single ML model:
    /// <list type="number">
    ///   <item>Loads all resolved prediction logs for the model within the window.</item>
    ///   <item>Classifies each prediction's timestamp into a <see cref="TradingSession"/>
    ///         using <see cref="ClassifySession"/>.</item>
    ///   <item>Groups by session, computes accuracy = correct / total per group.</item>
    ///   <item>Upserts one <see cref="MLModelSessionAccuracy"/> row per session bucket
    ///         that meets the <paramref name="minPredictions"/> threshold.</item>
    /// </list>
    /// Session buckets with fewer than <paramref name="minPredictions"/> resolved
    /// predictions are skipped to avoid noisy accuracy estimates (e.g., a session
    /// with only 3 predictions is statistically meaningless).
    /// </summary>
    /// <param name="modelId">Primary key of the ML model being evaluated.</param>
    /// <param name="symbol">Instrument symbol (e.g., "EUR_USD").</param>
    /// <param name="timeframe">Candle timeframe for this model.</param>
    /// <param name="windowStart">Inclusive UTC start of the accuracy look-back window.</param>
    /// <param name="minPredictions">Minimum resolved predictions per session before persisting a row.</param>
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
        // Load resolved prediction logs in the window.
        // Only selecting PredictedAt and DirectionCorrect keeps the projected dataset small.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId       == modelId       &&
                        l.DirectionCorrect != null         &&
                        l.PredictedAt      >= windowStart  &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, l.DirectionCorrect })
            .ToListAsync(ct);

        if (logs.Count == 0) return;

        // Classify each log entry into its trading session and group.
        // ClassifySession is a pure UTC-hour mapping — no timezone conversion needed.
        var bySession = logs
            .GroupBy(l => ClassifySession(l.PredictedAt))
            .ToList();

        var now = DateTime.UtcNow;
        int upserted = 0;

        foreach (var group in bySession)
        {
            var session = group.Key;
            int total   = group.Count();

            // Skip buckets with insufficient data for a reliable accuracy estimate.
            if (total < minPredictions) continue;

            int    correct  = group.Count(l => l.DirectionCorrect == true);
            double accuracy = (double)correct / total;

            // Upsert strategy: try update first; insert if no row exists yet.
            // ExecuteUpdateAsync issues a single SQL UPDATE with no tracked entity overhead.
            int rows = await writeCtx.Set<MLModelSessionAccuracy>()
                .Where(r => r.MLModelId == modelId && r.Session == session)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TotalPredictions,   total)
                    .SetProperty(r => r.CorrectPredictions, correct)
                    .SetProperty(r => r.Accuracy,           accuracy)
                    .SetProperty(r => r.WindowStart,        windowStart)
                    .SetProperty(r => r.ComputedAt,         now),
                    ct);

            if (rows == 0)
            {
                // First time we've seen this model/session combination — insert.
                writeCtx.Set<MLModelSessionAccuracy>().Add(new MLModelSessionAccuracy
                {
                    MLModelId          = modelId,
                    Symbol             = symbol,
                    Timeframe          = timeframe,
                    Session            = session,
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
                "SessionAccuracy: model {Id} {Symbol}/{Tf} {Session} — acc={Acc:P1} n={N}",
                modelId, symbol, timeframe, session, accuracy, total);
        }

        if (upserted > 0)
            _logger.LogInformation(
                "SessionAccuracy: model {Id} ({Symbol}/{Tf}) — computed {N} session bucket(s).",
                modelId, symbol, timeframe, upserted);
    }

    // ── Session classification ────────────────────────────────────────────────

    /// <summary>
    /// Maps a UTC timestamp to the dominant <see cref="TradingSession"/> using
    /// standard forex session open/close hours:
    /// <list type="bullet">
    ///   <item>Asian       — 00:00–06:59 UTC (Tokyo/Sydney dominant)</item>
    ///   <item>London      — 07:00–12:59 UTC (European open through US pre-market)</item>
    ///   <item>LondonNYOverlap — 13:00–16:59 UTC (highest liquidity window)</item>
    ///   <item>NewYork     — 17:00–23:59 UTC (US afternoon through close)</item>
    /// </list>
    /// The boundaries are aligned to whole UTC hours for simplicity; minute-level
    /// precision is unnecessary given that accuracy aggregates are computed hourly.
    /// </summary>
    /// <param name="utc">UTC timestamp of the prediction to classify.</param>
    /// <returns>The <see cref="TradingSession"/> active at the given time.</returns>
    private static TradingSession ClassifySession(DateTime utc)
    {
        int hour = utc.Hour;
        return hour switch
        {
            >= 0  and <= 6  => TradingSession.Asian,
            >= 7  and <= 12 => TradingSession.London,
            >= 13 and <= 16 => TradingSession.LondonNYOverlap,
            _               => TradingSession.NewYork,   // 17–23
        };
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
