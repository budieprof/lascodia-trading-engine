using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Aggregates multi-horizon direction accuracy for each active ML model using
/// the <c>HorizonCorrect3</c>, <c>HorizonCorrect6</c>, and <c>HorizonCorrect12</c>
/// fields on <see cref="MLModelPredictionLog"/>, and persists the results as
/// <see cref="MLModelHorizonAccuracy"/> rows (3 rows per model × horizon).
///
/// <b>Motivation:</b> The primary <c>DirectionCorrect</c> metric measures accuracy
/// at the 1-bar (next-candle) horizon. A model may score 60 % at 1 bar but only
/// 52 % at 3 bars, revealing that its edge decays rapidly. Conversely, a model that
/// scores 55 % at 1 bar but 62 % at 12 bars may be better suited to longer-hold
/// strategies. This worker makes that multi-horizon profile visible.
///
/// <b>Alert condition:</b> When the 3-bar accuracy is more than
/// <c>HorizonGapThreshold</c> below the primary direction accuracy, the model's
/// temporal edge is shallow — it is right about direction but wrong about
/// <i>when</i> the move occurs. An <see cref="AlertType.MLModelDegraded"/> alert
/// is raised with reason <c>"horizon_accuracy_gap"</c>.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLHorizon:PollIntervalSeconds</c>   — default 3600 (1 h)</item>
///   <item><c>MLHorizon:WindowDays</c>            — look-back window, default 30</item>
///   <item><c>MLHorizon:MinPredictions</c>        — minimum per horizon, default 20</item>
///   <item><c>MLHorizon:HorizonGapThreshold</c>   — gap alert floor (0-1), default 0.10</item>
///   <item><c>MLHorizon:WilsonZ</c>               — confidence-bound z score, default 1.96</item>
///   <item><c>MLHorizon:AlertDestination</c>      — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLHorizonAccuracyWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    // All config is read live from the EngineConfig table each poll cycle.
    private const string CK_PollSecs  = "MLHorizon:PollIntervalSeconds";
    private const string CK_Window    = "MLHorizon:WindowDays";
    private const string CK_MinPreds  = "MLHorizon:MinPredictions";
    private const string CK_GapThr    = "MLHorizon:HorizonGapThreshold";
    private const string CK_WilsonZ   = "MLHorizon:WilsonZ";
    private const string CK_AlertDest = "MLHorizon:AlertDestination";

    /// <summary>
    /// The three forward-look horizons tracked in <see cref="MLModelPredictionLog"/>.
    /// Each entry maps a bar-count horizon to its corresponding field name for documentation
    /// purposes. The actual field selection in <see cref="ComputeForModelAsync"/> is done
    /// via a switch on <c>horizonBars</c>.
    /// <list type="bullet">
    ///   <item>3 bars  — short-term: "did the move happen within 3 candles?"</item>
    ///   <item>6 bars  — medium-term: "within 6 candles?"</item>
    ///   <item>12 bars — longer-term: "within 12 candles?"</item>
    /// </list>
    /// </summary>
    private static readonly (int Bars, string Field)[] Horizons =
    [
        (3,  "HorizonCorrect3"),
        (6,  "HorizonCorrect6"),
        (12, "HorizonCorrect12"),
    ];

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLHorizonAccuracyWorker>  _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory for creating per-poll scoped service lifetimes, ensuring DbContexts
    /// are cleanly disposed after each computation cycle.
    /// </param>
    /// <param name="logger">Structured logger for computation diagnostics and alerts.</param>
    public MLHorizonAccuracyWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLHorizonAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope for scoped DB context lifetimes.</item>
    ///   <item>Reads the current poll interval from <see cref="EngineConfig"/>.</item>
    ///   <item>Delegates to <see cref="ComputeAllModelsAsync"/> for the computation.</item>
    ///   <item>Sleeps for <c>pollSecs</c> before the next cycle.</item>
    /// </list>
    /// Non-cancellation exceptions are caught and logged to keep the worker alive
    /// through transient DB or network errors.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLHorizonAccuracyWorker started.");

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
                pollSecs = NormalizePollSeconds(
                    await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken));

                await ComputeAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Transient errors must not crash the watchdog permanently.
                _logger.LogError(ex, "MLHorizonAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLHorizonAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads global config parameters for the current poll cycle, then iterates
    /// all active ML models and calls <see cref="ComputeForModelAsync"/> for each,
    /// isolating failures per model so that one bad model cannot block the rest.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for fetching models and prediction logs.</param>
    /// <param name="writeCtx">Write DbContext for upserting horizon accuracy rows and alerts.</param>
    /// <param name="ct">Cancellation token checked between model iterations.</param>
    internal async Task ComputeAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load all parameters once per cycle to avoid repeated DB round-trips in the loop.
        int    windowDays  = NormalizeWindowDays(await GetConfigAsync<int>(readCtx, CK_Window, 30, ct));
        int    minPreds    = NormalizeMinPredictions(await GetConfigAsync<int>(readCtx, CK_MinPreds, 20, ct));
        double gapThr      = NormalizeProbability(await GetConfigAsync<double>(readCtx, CK_GapThr, 0.10, ct), 0.10);
        double wilsonZ     = NormalizeWilsonZ(await GetConfigAsync<double>(readCtx, CK_WilsonZ, 1.96, ct));
        string alertDest   = NormalizeDestination(await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct));

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
                    windowStart, minPreds, gapThr, wilsonZ, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-model failures.
                _logger.LogWarning(ex,
                    "HorizonAccuracy: compute failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Computes multi-horizon direction accuracy for a single ML model:
    /// <list type="number">
    ///   <item>Loads all resolved prediction logs (where <c>DirectionCorrect</c> is not null)
    ///         within the look-back window, selecting the primary direction outcome and
    ///         all three horizon-specific correctness flags.</item>
    ///   <item>Computes the primary 1-bar direction accuracy as the baseline for
    ///         horizon-gap detection.</item>
    ///   <item>For each of the three tracked horizons (3, 6, 12 bars), filters logs where
    ///         that horizon's field is resolved (not null), computes accuracy, and upserts
    ///         a <see cref="MLModelHorizonAccuracy"/> row.</item>
    ///   <item>Fires a <see cref="AlertType.MLModelDegraded"/> alert with reason
    ///         <c>"horizon_accuracy_gap"</c> when the 3-bar accuracy is more than
    ///         <paramref name="horizonGapThreshold"/> below the primary accuracy.
    ///         This indicates a model with a shallow temporal edge: it predicts the
    ///         correct direction but is wrong about the timing of the move.</item>
    /// </list>
    ///
    /// <b>Why the gap check uses only horizon 3?</b>
    /// The 3-bar horizon is the most proximate forward look. If the model's edge decays
    /// significantly between 1 bar and 3 bars, it is effectively a 1-bar scalp model
    /// being misused for strategies that hold for multiple bars. The 6-bar and 12-bar
    /// horizons are informational but do not trigger alerts independently.
    /// </summary>
    /// <param name="modelId">Primary key of the ML model being evaluated.</param>
    /// <param name="symbol">Instrument symbol (e.g., "EUR_USD").</param>
    /// <param name="timeframe">Candle timeframe for this model.</param>
    /// <param name="windowStart">Inclusive UTC start of the look-back window.</param>
    /// <param name="minPredictions">
    /// Minimum resolved predictions required per horizon before an accuracy row is written.
    /// Note: horizon fields are resolved later than the primary direction field, so horizon
    /// sample sizes are typically smaller than the total log count.
    /// </param>
    /// <param name="horizonGapThreshold">
    /// Maximum tolerated accuracy gap between primary (1-bar) and 3-bar horizon before
    /// an alert is raised. Default 0.10 (10 percentage points).
    /// </param>
    /// <param name="alertDest">Alert destination identifier (e.g., "ml-ops").</param>
    /// <param name="readCtx">Read DbContext for prediction logs and existing alert state.</param>
    /// <param name="writeCtx">Write DbContext for accuracy rows and alert inserts.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ComputeForModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                windowStart,
        int                                     minPredictions,
        double                                  horizonGapThreshold,
        double                                  wilsonZ,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load served champion prediction logs with all primary and horizon correctness fields.
        // Horizon rows are computed from their own resolved fields, even if the primary
        // trade-level outcome has not been recorded yet.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId     &&
                        l.ModelRole        == ModelRole.Champion &&
                        l.PredictedAt      >= windowStart  &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new
            {
                l.DirectionCorrect,
                l.HorizonCorrect3,
                l.HorizonCorrect6,
                l.HorizonCorrect12,
            })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        // Compute primary (1-bar) direction accuracy from all resolved logs.
        // This is the baseline against which each horizon accuracy is compared.
        int    primaryTotal   = logs.Count(l => l.DirectionCorrect != null);
        int    primaryCorrect = logs.Count(l => l.DirectionCorrect == true);
        double primaryAcc     = primaryTotal > 0 ? (double)primaryCorrect / primaryTotal : 0.0;
        bool   primaryReliable = primaryTotal >= minPredictions;

        // Compute and upsert each horizon independently.
        foreach (var (horizonBars, _) in Horizons)
        {
            // Filter to logs where this specific horizon field has been resolved.
            // HorizonCorrectN is populated by MLPredictionOutcomeWorker after N bars
            // have elapsed since the prediction — so fewer logs will have resolved
            // values for longer horizons, especially for recent predictions.
            var resolved = horizonBars switch
            {
                3  => logs.Where(l => l.HorizonCorrect3  != null).Select(l => l.HorizonCorrect3!.Value).ToList(),
                6  => logs.Where(l => l.HorizonCorrect6  != null).Select(l => l.HorizonCorrect6!.Value).ToList(),
                12 => logs.Where(l => l.HorizonCorrect12 != null).Select(l => l.HorizonCorrect12!.Value).ToList(),
                _  => new List<bool>(),
            };

            int    total    = resolved.Count;
            int    correct  = resolved.Count(v => v);
            double accuracy = total > 0 ? (double)correct / total : 0.0;
            double lowerBound = WilsonLowerBound(correct, total, wilsonZ);
            double primaryGap = primaryReliable ? Math.Max(0.0, primaryAcc - accuracy) : 0.0;
            bool isReliable = primaryReliable && total >= minPredictions;
            string status = isReliable
                ? "Computed"
                : total < minPredictions
                    ? "InsufficientHorizonSamples"
                    : "InsufficientPrimarySamples";

            await UpsertHorizonAccuracyAsync(
                writeCtx,
                modelId,
                symbol,
                timeframe,
                horizonBars,
                total,
                correct,
                accuracy,
                lowerBound,
                primaryTotal,
                primaryCorrect,
                primaryAcc,
                primaryGap,
                isReliable,
                status,
                windowStart,
                now,
                ct);

            _logger.LogDebug(
                "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) h={H}bar - acc={Acc:P1} lb={Lb:P1} n={N} status={Status}",
                modelId, symbol, timeframe, horizonBars, accuracy, lowerBound, total, status);

            // ── Horizon gap alert (3-bar only) ────────────────────────────────
            // Check whether the short-horizon (3-bar) accuracy lags the primary
            // 1-bar accuracy by more than the configured gap threshold.
            // A large gap signals that the model's directional edge is valid at 1 bar
            // but decays too quickly — a "shallow temporal edge" that may be unsuitable
            // for multi-bar holding strategies.
            if (horizonBars == 3 && isReliable && primaryAcc - accuracy > horizonGapThreshold)
            {
                string dedupKey = $"MLHorizon:{modelId}:{symbol}:{timeframe}:3";

                // Deduplicate this exact horizon breach without hiding unrelated ML
                // degradation alerts for another model, timeframe, or worker.
                bool alertExists = await readCtx.Set<Alert>()
                    .AnyAsync(a => a.DeduplicationKey == dedupKey &&
                                   a.IsActive && !a.IsDeleted, ct);

                if (!alertExists)
                {
                    _logger.LogWarning(
                        "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) — primary={P:P1} h3={H3:P1} " +
                        "gap={Gap:P1} exceeds threshold {Thr:P0}. Model has shallow temporal edge.",
                        modelId, symbol, timeframe, primaryAcc, accuracy,
                        primaryAcc - accuracy, horizonGapThreshold);

                    // Persist alert with full diagnostic context.
                    writeCtx.Set<Alert>().Add(new Alert
                    {
                        AlertType     = AlertType.MLModelDegraded,
                        Symbol        = symbol,
                        ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            reason                = "horizon_accuracy_gap",
                            severity              = "warning",
                            symbol,
                            timeframe             = timeframe.ToString(),
                            modelId,
                            primaryDirectionAcc   = primaryAcc,   // 1-bar accuracy
                            horizon3BarAcc        = accuracy,     // 3-bar accuracy
                            horizon3BarLowerBound = lowerBound,
                            gap                   = primaryAcc - accuracy,  // absolute gap
                            horizonGapThreshold,
                            alertDestination      = alertDest,
                            sampleCount           = total,
                            primarySampleCount    = primaryTotal,
                        }),
                        Severity         = AlertSeverity.Medium,
                        DeduplicationKey = dedupKey,
                        CooldownSeconds  = 3600,
                        IsActive         = true,
                    });
                    await writeCtx.SaveChangesAsync(ct);
                }
            }
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// This allows live tuning of worker thresholds without a service restart.
    /// </summary>
    /// <typeparam name="T">Target CLR type (e.g. <c>int</c>, <c>double</c>, <c>string</c>).</typeparam>
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

    private static async Task UpsertHorizonAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        int horizonBars,
        int total,
        int correct,
        double accuracy,
        double lowerBound,
        int primaryTotal,
        int primaryCorrect,
        double primaryAccuracy,
        double primaryGap,
        bool isReliable,
        string status,
        DateTime windowStart,
        DateTime computedAt,
        CancellationToken ct)
    {
        int rows = await writeCtx.Set<MLModelHorizonAccuracy>()
            .IgnoreQueryFilters()
            .Where(r => r.MLModelId == modelId && r.HorizonBars == horizonBars)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Symbol,                    symbol)
                .SetProperty(r => r.Timeframe,                 timeframe)
                .SetProperty(r => r.TotalPredictions,          total)
                .SetProperty(r => r.CorrectPredictions,        correct)
                .SetProperty(r => r.Accuracy,                  accuracy)
                .SetProperty(r => r.AccuracyLowerBound,        lowerBound)
                .SetProperty(r => r.PrimaryTotalPredictions,   primaryTotal)
                .SetProperty(r => r.PrimaryCorrectPredictions, primaryCorrect)
                .SetProperty(r => r.PrimaryAccuracy,           primaryAccuracy)
                .SetProperty(r => r.PrimaryAccuracyGap,        primaryGap)
                .SetProperty(r => r.IsReliable,                isReliable)
                .SetProperty(r => r.Status,                    status)
                .SetProperty(r => r.WindowStart,               windowStart)
                .SetProperty(r => r.ComputedAt,                computedAt)
                .SetProperty(r => r.IsDeleted,                 false),
                ct);

        if (rows > 0) return;

        writeCtx.Set<MLModelHorizonAccuracy>().Add(new MLModelHorizonAccuracy
        {
            MLModelId                  = modelId,
            Symbol                     = symbol,
            Timeframe                  = timeframe,
            HorizonBars                = horizonBars,
            TotalPredictions           = total,
            CorrectPredictions         = correct,
            Accuracy                   = accuracy,
            AccuracyLowerBound         = lowerBound,
            PrimaryTotalPredictions    = primaryTotal,
            PrimaryCorrectPredictions  = primaryCorrect,
            PrimaryAccuracy            = primaryAccuracy,
            PrimaryAccuracyGap         = primaryGap,
            IsReliable                 = isReliable,
            Status                     = status,
            WindowStart                = windowStart,
            ComputedAt                 = computedAt,
        });
        await writeCtx.SaveChangesAsync(ct);
    }

    internal static double WilsonLowerBound(int successes, int total, double z)
    {
        if (total <= 0) return 0.0;

        double p = (double)successes / total;
        double z2 = z * z;
        double denominator = 1.0 + z2 / total;
        double centre = p + z2 / (2.0 * total);
        double margin = z * Math.Sqrt((p * (1.0 - p) + z2 / (4.0 * total)) / total);
        return Math.Clamp((centre - margin) / denominator, 0.0, 1.0);
    }

    internal static int NormalizePollSeconds(int value)
        => value is >= 1 and <= 86_400 ? value : 3600;

    internal static int NormalizeWindowDays(int value)
        => value is >= 1 and <= 3650 ? value : 30;

    internal static int NormalizeMinPredictions(int value)
        => value >= 1 ? value : 20;

    internal static double NormalizeProbability(double value, double defaultValue)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0 ? value : defaultValue;

    internal static double NormalizeWilsonZ(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 5.0 ? value : 1.96;

    internal static string NormalizeDestination(string? value)
        => string.IsNullOrWhiteSpace(value) ? "ml-ops" : value.Trim();
}
