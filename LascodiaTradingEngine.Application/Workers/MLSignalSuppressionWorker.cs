using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Kill-switch worker: sets <see cref="MLModel.IsSuppressed"/> on active models whose
/// rolling prediction accuracy falls below a hard floor, and clears the flag when
/// accuracy recovers.
///
/// Motivation: <see cref="MLDriftMonitorWorker"/> queues a retrain when drift is
/// detected, but the degraded model continues scoring signals for hours or days while
/// the retrain runs. This worker bridges that gap by suppressing scoring immediately
/// once accuracy crosses the hard floor — <see cref="Services.MLSignalScorer"/> returns
/// an empty result for suppressed models (same as "no model deployed").
///
/// The suppression is <b>self-healing</b>:
/// <list type="bullet">
///   <item>Suppress  when rolling accuracy &lt; <c>HardAccuracyFloor</c>.</item>
///   <item>Un-suppress when rolling accuracy ≥ <c>HardAccuracyFloor</c>.</item>
///   <item>Skip the model entirely when fewer than <c>MinPredictions</c> resolved
///         predictions are available (do not change suppression state).</item>
/// </list>
///
/// A retrain is queued on every suppression event (idempotent — no duplicate if one
/// is already pending).
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLSuppression:PollIntervalSeconds</c>  — default 600 (10 min)</item>
///   <item><c>MLSuppression:WindowDays</c>           — rolling accuracy window, default 7</item>
///   <item><c>MLSuppression:MinPredictions</c>       — minimum resolved predictions, default 20</item>
///   <item><c>MLSuppression:HardAccuracyFloor</c>    — suppression trigger, default 0.44</item>
/// </list>
/// </summary>
public sealed class MLSignalSuppressionWorker : BackgroundService
{
    // ── EngineConfig key constants ─────────────────────────────────────────────
    // All configuration is read from the EngineConfig table at runtime, allowing
    // hot-reload without restarting the service. Each constant maps to a database key.

    /// <summary>How many seconds to wait between suppression evaluation cycles.</summary>
    private const string CK_PollSecs          = "MLSuppression:PollIntervalSeconds";

    /// <summary>Number of calendar days to look back when computing rolling accuracy.</summary>
    private const string CK_WindowDays        = "MLSuppression:WindowDays";

    /// <summary>Minimum number of resolved predictions required before suppression can be evaluated.
    /// Guards against suppressing a new model that has too few data points to be statistically meaningful.</summary>
    private const string CK_MinPredictions    = "MLSuppression:MinPredictions";

    /// <summary>Rolling accuracy below this value triggers model suppression.
    /// A value of 0.44 means the model must be correct on fewer than 44 % of directional predictions
    /// before its signals are blocked — just below random chance for a binary classifier.</summary>
    private const string CK_HardAccuracyFloor = "MLSuppression:HardAccuracyFloor";

    private readonly IServiceScopeFactory                _scopeFactory;
    private readonly ILogger<MLSignalSuppressionWorker>  _logger;

    /// <summary>
    /// Initialises the worker with the DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">Used to create per-iteration DI scopes so that
    /// scoped services (EF DbContexts) are properly disposed after each poll cycle.</param>
    /// <param name="logger">Structured logger for suppression events and diagnostics.</param>
    public MLSignalSuppressionWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLSignalSuppressionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main background loop. Runs indefinitely until the host signals cancellation.
    /// On each iteration:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope to obtain scoped read/write DbContexts.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/> (supports hot-reload).</item>
    ///   <item>Delegates to <see cref="EvaluateSuppressionAsync"/> to evaluate every active model.</item>
    ///   <item>Sleeps for the configured poll interval before the next cycle.</item>
    /// </list>
    /// Errors within a single iteration are logged and swallowed so the loop never exits
    /// due to transient database or network failures.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalSuppressionWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval used if the EngineConfig key is missing or unparseable.
            int pollSecs = 600;

            try
            {
                // Create a new scope per iteration — EF DbContexts are scoped services
                // and must not be reused across long-running background loop iterations.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read poll interval on every cycle to support hot-reload configuration changes.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 600, stoppingToken);

                await EvaluateSuppressionAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly without logging an error.
                break;
            }
            catch (Exception ex)
            {
                // Log and continue — a transient DB error should not permanently stop suppression checks.
                _logger.LogError(ex, "MLSignalSuppressionWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalSuppressionWorker stopping.");
    }

    /// <summary>
    /// Loads all active (non-deleted) ML models and evaluates each one for suppression
    /// using the shared rolling accuracy window and floor configuration.
    /// Failures on individual models are caught and logged so one bad model cannot
    /// block evaluation of the remaining models.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for querying models and prediction logs.</param>
    /// <param name="writeCtx">Write DbContext for updating suppression flags and queuing retrains.</param>
    /// <param name="ct">Cancellation token propagated from the host shutdown signal.</param>
    private async Task EvaluateSuppressionAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load all suppression parameters upfront to avoid N+1 EngineConfig queries per model.
        int    windowDays       = await GetConfigAsync<int>   (readCtx, CK_WindowDays,        7,    ct);
        int    minPredictions   = await GetConfigAsync<int>   (readCtx, CK_MinPredictions,    20,   ct);
        double hardAccuracyFloor = await GetConfigAsync<double>(readCtx, CK_HardAccuracyFloor, 0.44, ct);

        // Only evaluate models that are currently active — suppressed but inactive models
        // are already blocked by the IsActive=false gate in MLSignalScorer.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Check for cancellation between models to ensure a responsive shutdown.
            ct.ThrowIfCancellationRequested();

            try
            {
                await EvaluateModelAsync(
                    model, readCtx, writeCtx,
                    windowDays, minPredictions, hardAccuracyFloor, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-model failures — log the offending symbol/timeframe/id and continue.
                _logger.LogWarning(ex,
                    "Suppression evaluation failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Core suppression decision for a single model:
    /// <list type="number">
    ///   <item>Fetches all resolved prediction logs within the rolling window (where
    ///         <c>DirectionCorrect</c> is not null — meaning the trade outcome is known).</item>
    ///   <item>Skips the model if fewer than <paramref name="minPredictions"/> resolved logs
    ///         exist (too few observations to be statistically reliable).</item>
    ///   <item>Computes rolling accuracy as <c>correct / total</c>.</item>
    ///   <item>Suppresses the model (and queues a retrain) when accuracy &lt; floor.</item>
    ///   <item>Un-suppresses the model when accuracy has recovered to or above the floor.</item>
    /// </list>
    /// The suppression state change is a no-op if the model is already in the correct state,
    /// so this method is safe to call on every poll cycle without generating spurious writes.
    /// </summary>
    /// <param name="model">The ML model entity to evaluate (loaded as AsNoTracking).</param>
    /// <param name="readCtx">Read-only DbContext for prediction log queries.</param>
    /// <param name="writeCtx">Write DbContext for updating MLModel.IsSuppressed and adding MLTrainingRun.</param>
    /// <param name="windowDays">How many calendar days of prediction logs to include in the accuracy window.</param>
    /// <param name="minPredictions">Minimum resolved predictions required to make a suppression decision.</param>
    /// <param name="hardAccuracyFloor">Rolling accuracy below this value triggers suppression.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EvaluateModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        double                                  hardAccuracyFloor,
        CancellationToken                       ct)
    {
        // Only look at prediction logs within the configured rolling window.
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Query only resolved logs (DirectionCorrect != null) — pending/unresolved logs
        // cannot contribute to accuracy measurement and must be excluded.
        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null      &&
                        !l.IsDeleted)
            .AsNoTracking()
            // Project to a bool list to avoid loading full entity payloads across the wire.
            .Select(l => l.DirectionCorrect!.Value)
            .ToListAsync(ct);

        // Guard: do not suppress/unsuppress based on insufficient data.
        // A new model deployed hours ago should not be killed because it has only 3 predictions.
        if (resolved.Count < minPredictions)
        {
            _logger.LogDebug(
                "Suppression: {Symbol}/{Tf} model {Id} only {N} resolved predictions (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minPredictions);
            return;
        }

        // Rolling accuracy: fraction of resolved predictions that were directionally correct.
        double rollingAccuracy = resolved.Count(c => c) / (double)resolved.Count;

        // shouldSuppress is true when accuracy falls below the hard floor.
        // This is a hard gate — unlike MLDriftMonitorWorker which uses statistical tests,
        // this threshold is absolute and deliberately conservative.
        bool shouldSuppress    = rollingAccuracy < hardAccuracyFloor;

        _logger.LogDebug(
            "Suppression: {Symbol}/{Tf} model {Id}: rollingAcc={Acc:P1} floor={Floor:P1} " +
            "currentlySuppressed={Supp}",
            model.Symbol, model.Timeframe, model.Id,
            rollingAccuracy, hardAccuracyFloor, model.IsSuppressed);

        if (shouldSuppress && !model.IsSuppressed)
        {
            // ── Suppress ──────────────────────────────────────────────────────
            // Transition: not suppressed → suppressed.
            // MLSignalScorer checks IsSuppressed before scoring and returns an empty result,
            // effectively blocking this model's signals from reaching order execution.
            _logger.LogWarning(
                "Suppression: SUPPRESSING {Symbol}/{Tf} model {Id} — rolling accuracy {Acc:P1} " +
                "< hard floor {Floor:P1}. Signals will be blocked until recovery or retrain.",
                model.Symbol, model.Timeframe, model.Id, rollingAccuracy, hardAccuracyFloor);

            // Bulk update — avoids loading and tracking the entity just to flip one boolean.
            await writeCtx.Set<MLModel>()
                .Where(m => m.Id == model.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsSuppressed, true), ct);

            // Queue retrain if not already pending — idempotent check prevents duplicate runs
            // when the worker fires multiple times while the model remains suppressed.
            bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
                .AnyAsync(r => r.Symbol    == model.Symbol    &&
                               r.Timeframe == model.Timeframe &&
                               (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

            if (!alreadyQueued)
            {
                // Capture diagnostic context in the hyperparam JSON so the retraining pipeline
                // can surface the suppression reason in dashboards and audit logs.
                writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol    = model.Symbol,
                    Timeframe = model.Timeframe,
                    Status    = RunStatus.Queued,
                    HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        triggeredBy      = "MLSignalSuppressionWorker",
                        rollingAccuracy,
                        hardAccuracyFloor,
                        windowDays,
                        resolvedCount    = resolved.Count,
                        modelId          = model.Id,
                    }),
                });
                await writeCtx.SaveChangesAsync(ct);
            }
        }
        else if (!shouldSuppress && model.IsSuppressed)
        {
            // ── Un-suppress (accuracy recovered) ────────────────────────────
            // Transition: suppressed → not suppressed.
            // This can happen after a retrain produces a better model whose new predictions
            // push the rolling window accuracy back above the floor, or if market conditions
            // shift so that the existing model is once again predictive.
            _logger.LogInformation(
                "Suppression: LIFTING suppression for {Symbol}/{Tf} model {Id} — rolling accuracy " +
                "{Acc:P1} has recovered above floor {Floor:P1}.",
                model.Symbol, model.Timeframe, model.Id, rollingAccuracy, hardAccuracyFloor);

            await writeCtx.Set<MLModel>()
                .Where(m => m.Id == model.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsSuppressed, false), ct);
        }
        // else: state is already correct — no write needed.
    }

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key does not exist or the stored
    /// string value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type — typically <c>int</c>, <c>double</c>, or <c>string</c>.</typeparam>
    /// <param name="ctx">Any DbContext with access to the EngineConfig set.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Fallback value returned when the key is absent or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed configuration value, or <paramref name="defaultValue"/>.</returns>
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

        // Convert.ChangeType handles most primitive conversions (string → int, string → double, etc.).
        // Any conversion failure (e.g., malformed value) falls back to the safe default.
        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
