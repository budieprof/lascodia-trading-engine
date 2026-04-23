using System.Globalization;
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
    private const int    AlertCooldownSeconds       = 3600;
    private const int    MaxAlertDestinationLength  = 100;
    private const string HorizonAccuracyGapReason   = "horizon_accuracy_gap";
    private const string HorizonAccuracyUniqueIndex  = "IX_MLModelHorizonAccuracy_MLModelId_HorizonBars";
    private const string AlertDeduplicationIndex     = "IX_Alert_DeduplicationKey";

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
    private readonly IDatabaseExceptionClassifier?     _dbExceptionClassifier;

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
        ILogger<MLHorizonAccuracyWorker>   logger,
        IDatabaseExceptionClassifier?      dbExceptionClassifier = null)
    {
        _scopeFactory          = scopeFactory;
        _logger                = logger;
        _dbExceptionClassifier = dbExceptionClassifier;
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

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate per-model failures.
                _logger.LogWarning(ex,
                    "HorizonAccuracy: compute failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
            finally
            {
                writeCtx.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Computes multi-horizon direction accuracy for a single ML model:
    /// <list type="number">
    ///   <item>Aggregates champion prediction logs within the look-back window in a
    ///         single SQL statement, producing primary, 3-bar, 6-bar, and 12-bar
    ///         resolved/correct counts from one database snapshot.</item>
    ///   <item>Computes primary 1-bar direction accuracy as the baseline for horizon-gap
    ///         detection.</item>
    ///   <item>For each tracked horizon (3, 6, 12 bars), computes accuracy, conservative
    ///         Wilson lower bound, reliability state, and upserts one
    ///         <see cref="MLModelHorizonAccuracy"/> row.</item>
    ///   <item>Synchronizes the 3-bar gap alert: create/update while breached, auto-resolve
    ///         once the breach clears or the row no longer has enough current samples.</item>
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
    /// <param name="readCtx">Read DbContext for prediction logs.</param>
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
        // Aggregate in one SQL statement instead of materialising prediction logs or issuing
        // separate total/correct counts. This keeps the worker stable for long look-back
        // windows and prevents count skew while horizon outcomes are being resolved.
        var logs = readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId     &&
                        l.ModelRole        == ModelRole.Champion &&
                        l.PredictedAt      >= windowStart  &&
                        !l.IsDeleted)
            .AsNoTracking();

        var now = DateTime.UtcNow;
        var aggregate = await LoadAggregateStatsAsync(logs, ct);

        // Compute primary (1-bar) direction accuracy from all resolved logs.
        // This is the baseline against which each horizon accuracy is compared.
        var primary = aggregate.Primary;
        int    primaryTotal   = primary.Total;
        int    primaryCorrect = primary.Correct;
        double primaryAcc     = primaryTotal > 0 ? (double)primaryCorrect / primaryTotal : 0.0;
        bool   primaryReliable = primaryTotal >= minPredictions;

        // Compute and upsert each horizon independently.
        foreach (var (horizonBars, _) in Horizons)
        {
            // Count logs where this specific horizon field has been resolved.
            // HorizonCorrectN is populated by MLMultiHorizonOutcomeWorker after N bars
            // have elapsed since the prediction — so fewer logs will have resolved
            // values for longer horizons, especially for recent predictions.
            var horizon = aggregate.ForHorizon(horizonBars);

            int    total    = horizon.Total;
            int    correct  = horizon.Correct;
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
                _dbExceptionClassifier,
                ct);

            _logger.LogDebug(
                "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) h={H}bar - acc={Acc:P1} lb={Lb:P1} n={N} status={Status}",
                modelId, symbol, timeframe, horizonBars, accuracy, lowerBound, total, status);

            if (horizonBars == 3)
            {
                await SyncHorizonGapAlertAsync(
                    writeCtx,
                    modelId,
                    symbol,
                    timeframe,
                    primaryAcc,
                    accuracy,
                    lowerBound,
                    primaryGap,
                    horizonGapThreshold,
                    alertDest,
                    total,
                    primaryTotal,
                    isReliable,
                    now,
                    ct);
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

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T), CultureInfo.InvariantCulture); }
        catch { return defaultValue; }
    }

    private static async Task<HorizonAggregateStats> LoadAggregateStatsAsync(
        IQueryable<MLModelPredictionLog> logs,
        CancellationToken ct)
    {
        var row = await logs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PrimaryTotal   = g.Count(l => l.DirectionCorrect != null),
                PrimaryCorrect = g.Count(l => l.DirectionCorrect == true),
                H3Total        = g.Count(l => l.HorizonCorrect3 != null),
                H3Correct      = g.Count(l => l.HorizonCorrect3 == true),
                H6Total        = g.Count(l => l.HorizonCorrect6 != null),
                H6Correct      = g.Count(l => l.HorizonCorrect6 == true),
                H12Total       = g.Count(l => l.HorizonCorrect12 != null),
                H12Correct     = g.Count(l => l.HorizonCorrect12 == true),
            })
            .SingleOrDefaultAsync(ct);

        return row is null
            ? HorizonAggregateStats.Empty
            : new HorizonAggregateStats(
                new OutcomeStats(row.PrimaryTotal, row.PrimaryCorrect),
                new OutcomeStats(row.H3Total, row.H3Correct),
                new OutcomeStats(row.H6Total, row.H6Correct),
                new OutcomeStats(row.H12Total, row.H12Correct));
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
        IDatabaseExceptionClassifier? dbExceptionClassifier,
        CancellationToken ct)
    {
        int rows = await UpdateHorizonAccuracyAsync(
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
            primaryAccuracy,
            primaryGap,
            isReliable,
            status,
            windowStart,
            computedAt,
            ct);

        if (rows > 0) return;

        var row = new MLModelHorizonAccuracy
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
        };

        writeCtx.Set<MLModelHorizonAccuracy>().Add(row);

        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsExpectedUniqueConstraintViolation(
                   ex,
                   HorizonAccuracyUniqueIndex,
                   dbExceptionClassifier,
                   "MLModelHorizonAccuracy",
                   "MLModelId",
                   "HorizonBars"))
        {
            Detach(writeCtx, row);

            rows = await UpdateHorizonAccuracyAsync(
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
                primaryAccuracy,
                primaryGap,
                isReliable,
                status,
                windowStart,
                computedAt,
                ct);

            if (rows > 0) return;
            throw;
        }
    }

    private static Task<int> UpdateHorizonAccuracyAsync(
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
        => writeCtx.Set<MLModelHorizonAccuracy>()
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
                .SetProperty(r => r.ComputedAt,                computedAt),
                ct);

    private async Task SyncHorizonGapAlertAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        double primaryAccuracy,
        double horizon3Accuracy,
        double horizon3LowerBound,
        double gap,
        double horizonGapThreshold,
        string alertDestination,
        int sampleCount,
        int primarySampleCount,
        bool isReliable,
        DateTime computedAt,
        CancellationToken ct)
    {
        string dedupKey = HorizonGapDedupKey(modelId, symbol, timeframe);
        bool isBreached = isReliable && gap > horizonGapThreshold;

        if (!isBreached)
        {
            int resolved = await writeCtx.Set<Alert>()
                .Where(a => a.DeduplicationKey == dedupKey &&
                            a.IsActive &&
                            !a.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.IsActive, false)
                    .SetProperty(a => a.AutoResolvedAt, computedAt),
                    ct);

            if (resolved > 0)
            {
                _logger.LogInformation(
                    "HorizonAccuracy: resolved horizon gap alert for model {Id} ({Symbol}/{Tf}).",
                    modelId, symbol, timeframe);
            }

            return;
        }

        string conditionJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            reason                = HorizonAccuracyGapReason,
            severity              = "warning",
            symbol,
            timeframe             = timeframe.ToString(),
            modelId,
            primaryDirectionAcc   = primaryAccuracy,
            horizon3BarAcc        = horizon3Accuracy,
            horizon3BarLowerBound = horizon3LowerBound,
            gap,
            horizonGapThreshold,
            alertDestination,
            sampleCount,
            primarySampleCount,
            computedAt,
        });

        int updated = await writeCtx.Set<Alert>()
            .Where(a => a.DeduplicationKey == dedupKey &&
                        a.IsActive &&
                        !a.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Symbol, symbol)
                .SetProperty(a => a.ConditionJson, conditionJson)
                .SetProperty(a => a.Severity, AlertSeverity.Medium)
                .SetProperty(a => a.CooldownSeconds, AlertCooldownSeconds)
                .SetProperty(a => a.AutoResolvedAt, (DateTime?)null),
                ct);

        if (updated > 0) return;

        _logger.LogWarning(
            "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) — primary={P:P1} h3={H3:P1} " +
            "gap={Gap:P1} exceeds threshold {Thr:P0}. Model has shallow temporal edge.",
            modelId, symbol, timeframe, primaryAccuracy, horizon3Accuracy,
            gap, horizonGapThreshold);

        var alert = new Alert
        {
            AlertType        = AlertType.MLModelDegraded,
            Symbol           = symbol,
            ConditionJson    = conditionJson,
            Severity         = AlertSeverity.Medium,
            DeduplicationKey = dedupKey,
            CooldownSeconds  = AlertCooldownSeconds,
            IsActive         = true,
        };

        writeCtx.Set<Alert>().Add(alert);

        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsExpectedUniqueConstraintViolation(
                   ex,
                   AlertDeduplicationIndex,
                   _dbExceptionClassifier,
                   "Alert",
                   "DeduplicationKey"))
        {
            Detach(writeCtx, alert);

            bool alertExists = await writeCtx.Set<Alert>()
                .AsNoTracking()
                .AnyAsync(a => a.DeduplicationKey == dedupKey &&
                               a.IsActive &&
                               !a.IsDeleted,
                               ct);

            if (alertExists) return;
            throw;
        }
    }

    internal static double WilsonLowerBound(int successes, int total, double z)
    {
        if (total <= 0) return 0.0;

        successes = Math.Clamp(successes, 0, total);
        z = NormalizeWilsonZ(z);

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
        => value is >= 1 and <= 1_000_000 ? value : 20;

    internal static double NormalizeProbability(double value, double defaultValue)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0 ? value : defaultValue;

    internal static double NormalizeWilsonZ(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 5.0 ? value : 1.96;

    internal static string NormalizeDestination(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "ml-ops";

        var trimmed = value.Trim();
        return trimmed.Length <= MaxAlertDestinationLength
            ? trimmed
            : trimmed[..MaxAlertDestinationLength];
    }

    private static string HorizonGapDedupKey(long modelId, string symbol, Timeframe timeframe)
        => $"MLHorizon:{modelId}:{symbol}:{timeframe}:3";

    internal static bool IsExpectedUniqueConstraintViolation(
        DbUpdateException ex,
        string expectedConstraintName,
        IDatabaseExceptionClassifier? dbExceptionClassifier = null,
        params string[] requiredMessageTokens)
    {
        ArgumentNullException.ThrowIfNull(ex);

        bool isUnique = dbExceptionClassifier?.IsUniqueConstraintViolation(ex) == true
                        || LooksLikeUniqueConstraintViolation(ex);

        if (!isUnique) return false;

        string? constraintName = TryGetProviderConstraintName(ex);
        if (!string.IsNullOrWhiteSpace(constraintName))
        {
            return string.Equals(
                constraintName,
                expectedConstraintName,
                StringComparison.OrdinalIgnoreCase);
        }

        string message = FlattenExceptionMessages(ex);
        if (message.Contains(expectedConstraintName, StringComparison.OrdinalIgnoreCase))
            return true;

        return requiredMessageTokens.Length > 0 &&
               requiredMessageTokens.All(token =>
                   message.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeUniqueConstraintViolation(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            if (string.Equals(sqlState, "23505", StringComparison.Ordinal))
                return true;

            var sqliteErrorCode = current.GetType().GetProperty("SqliteErrorCode")?.GetValue(current);
            var sqliteExtendedErrorCode = current.GetType().GetProperty("SqliteExtendedErrorCode")?.GetValue(current);

            if (sqliteErrorCode is int code && code == 19)
                return true;

            if (sqliteExtendedErrorCode is int extendedCode && extendedCode == 2067)
                return true;

            string message = current.Message ?? string.Empty;
            if (message.Contains("23505", StringComparison.Ordinal) ||
                message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? TryGetProviderConstraintName(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var constraintName = current.GetType().GetProperty("ConstraintName")?.GetValue(current) as string;
            if (!string.IsNullOrWhiteSpace(constraintName))
                return constraintName;
        }

        return null;
    }

    private static string FlattenExceptionMessages(Exception ex)
    {
        var messages = new List<string>();

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                messages.Add(current.Message);
        }

        return string.Join(' ', messages);
    }

    private static void Detach<TEntity>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        TEntity entity)
        where TEntity : class
    {
        var entry = ctx.Entry(entity);
        if (entry.State != EntityState.Detached)
            entry.State = EntityState.Detached;
    }

    private readonly record struct OutcomeStats(int Total, int Correct);

    private readonly record struct HorizonAggregateStats(
        OutcomeStats Primary,
        OutcomeStats H3,
        OutcomeStats H6,
        OutcomeStats H12)
    {
        public static HorizonAggregateStats Empty { get; } = new(
            new OutcomeStats(0, 0),
            new OutcomeStats(0, 0),
            new OutcomeStats(0, 0),
            new OutcomeStats(0, 0));

        public OutcomeStats ForHorizon(int horizonBars)
            => horizonBars switch
            {
                3  => H3,
                6  => H6,
                12 => H12,
                _  => throw new ArgumentOutOfRangeException(nameof(horizonBars), horizonBars, "Unsupported horizon."),
            };
    }
}
