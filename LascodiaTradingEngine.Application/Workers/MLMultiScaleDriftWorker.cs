using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects two qualitatively different forms of ML model degradation by computing
/// direction accuracy over two rolling windows simultaneously and comparing them.
///
/// <para>
/// <b>Where it sits in the ML monitoring pipeline:</b><br/>
/// Most drift detectors operate on a single time scale. The key insight of this worker
/// is that different types of drift have different temporal signatures:
/// <list type="bullet">
///   <item>
///     <b>Sudden drift</b> (regime change, data pipeline fault, feature distribution flip):
///     Short-window accuracy collapses much faster than the long-window average. The long window
///     still contains many "good" predictions from before the break, masking the degradation
///     if only a single window is used.
///   </item>
///   <item>
///     <b>Gradual drift</b> (slow concept drift, regime shift over weeks):
///     Both windows fall together, slowly, below an absolute accuracy floor. Neither the
///     short-window spike nor the short-long gap alone reveals this — it is only visible when
///     the long-window average eventually crosses a minimum acceptable threshold.
///   </item>
/// </list>
/// This worker is complementary to the other drift detectors:
/// <list type="bullet">
///   <item><c>MLDriftMonitorWorker</c> — single-window accuracy, Brier score, ensemble disagreement.</item>
///   <item><see cref="MLCusumDriftWorker"/> — CUSUM sequential test; optimal for step changes.</item>
///   <item><see cref="MLAdwinDriftWorker"/> — ADWIN adaptive windowing; no fixed window required.</item>
///   <item><see cref="MLPeltChangePointWorker"/> — globally optimal multiple change-point detection.</item>
///   <item><see cref="MLStructuralBreakWorker"/> — Bai-Perron structural break test.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Sudden drift detection logic:</b><br/>
/// <c>gap = shortAccuracy − longAccuracy</c><br/>
/// Fires when <c>gap &lt; −ShortLongAccuracyGap</c> (default −0.07, i.e., a 7-percentage-point drop).<br/>
/// Severity: <c>critical</c>. Queues an immediate retrain and creates an urgent alert.
/// </para>
///
/// <para>
/// <b>Gradual drift detection logic:</b><br/>
/// Fires when <c>longAccuracy &lt; LongWindowFloor</c> (default 0.50, i.e., model is no better than random).<br/>
/// Sudden drift takes precedence; gradual drift is only evaluated if sudden drift was not detected.<br/>
/// Severity: <c>standard</c>. Queues a retrain with <c>TriggerType.AutoDegrading</c>.
/// </para>
///
/// <para>
/// <b>Configuration keys (stored in <see cref="EngineConfig"/> table):</b>
/// <list type="table">
///   <listheader><term>Key</term><description>Default / Description</description></listheader>
///   <item><term><c>MLMultiScaleDrift:PollIntervalSeconds</c></term><description>1800 — poll every 30 minutes.</description></item>
///   <item><term><c>MLMultiScaleDrift:ShortWindowDays</c></term><description>3 — recent window in days (fast signal).</description></item>
///   <item><term><c>MLMultiScaleDrift:LongWindowDays</c></term><description>21 — baseline window in days (slow signal, ~1 month).</description></item>
///   <item><term><c>MLMultiScaleDrift:MinPredictions</c></term><description>20 — minimum resolved predictions in the long window to run the check.</description></item>
///   <item><term><c>MLMultiScaleDrift:ShortLongAccuracyGap</c></term><description>0.07 — sudden-drift trigger threshold (7% accuracy gap).</description></item>
///   <item><term><c>MLMultiScaleDrift:LongWindowFloor</c></term><description>0.50 — gradual-drift trigger; model at or below random-chance accuracy.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class MLMultiScaleDriftWorker : BackgroundService
{
    private const string CK_PollSecs           = "MLMultiScaleDrift:PollIntervalSeconds";
    private const string CK_ShortWindowDays    = "MLMultiScaleDrift:ShortWindowDays";
    private const string CK_LongWindowDays     = "MLMultiScaleDrift:LongWindowDays";
    private const string CK_MinPredictions     = "MLMultiScaleDrift:MinPredictions";
    private const string CK_ShortLongGap       = "MLMultiScaleDrift:ShortLongAccuracyGap";
    private const string CK_LongWindowFloor    = "MLMultiScaleDrift:LongWindowFloor";

    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly ILogger<MLMultiScaleDriftWorker> _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// DI scope factory. A new scope (and therefore new EF Core DbContexts) is created on
    /// every polling iteration to prevent stale change-tracker state and connection exhaustion.
    /// </param>
    /// <param name="logger">Structured logger for diagnostic, warning, and error output.</param>
    public MLMultiScaleDriftWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLMultiScaleDriftWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point invoked by the .NET hosted-service infrastructure on application start.
    /// Runs a multi-scale drift check every <c>MLMultiScaleDrift:PollIntervalSeconds</c> seconds
    /// (default 30 minutes) until the application shuts down.
    /// </summary>
    /// <remarks>
    /// The poll interval is re-read from <see cref="EngineConfig"/> on every iteration so it
    /// can be adjusted at runtime without a service restart. The 30-minute default is a deliberate
    /// balance: frequent enough to catch sudden drift within a trading session, but not so frequent
    /// that it floods the database with redundant retrain requests during volatile markets.
    /// </remarks>
    /// <param name="stoppingToken">
    /// Graceful-shutdown cancellation token provided by the .NET host.
    /// <c>OperationCanceledException</c> breaks the loop cleanly without logging an error.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLMultiScaleDriftWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Local copy of poll interval so Task.Delay uses the correct value
            // even if an exception occurs before the config is re-read.
            int pollSecs = 1800;

            try
            {
                // Fresh DI scope per iteration — avoids holding scoped DbContext instances
                // open across the sleep period (which could cause connection-pool starvation).
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read poll interval every cycle so operators can tune without restarting.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 1800, stoppingToken);

                await CheckMultiScaleDriftAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Clean shutdown — not an error
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLMultiScaleDriftWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLMultiScaleDriftWorker stopping.");
    }

    /// <summary>
    /// Loads configuration, retrieves all active models, and dispatches a per-model
    /// multi-scale drift check. Configuration is read once per cycle so runtime tuning
    /// takes effect on the next poll without a service restart.
    /// </summary>
    /// <param name="readCtx">Read-side EF Core context for querying models, prediction logs, and config.</param>
    /// <param name="writeCtx">Write-side EF Core context for persisting training runs and alerts.</param>
    /// <param name="ct">Cancellation token; propagates <see cref="OperationCanceledException"/> immediately.</param>
    private async Task CheckMultiScaleDriftAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read all window/threshold parameters from the runtime config table.
        // shortWindowDays: the "fast" signal window — captures only very recent model performance.
        int    shortWindowDays  = await GetConfigAsync<int>   (readCtx, CK_ShortWindowDays, 3,    ct);
        // longWindowDays: the "slow" baseline window — smooths out short-term noise.
        int    longWindowDays   = await GetConfigAsync<int>   (readCtx, CK_LongWindowDays,  21,   ct);
        // minPredictions: minimum resolved predictions required in the long window to run the check.
        // Below this threshold, both accuracy estimates are too noisy to compare reliably.
        int    minPredictions   = await GetConfigAsync<int>   (readCtx, CK_MinPredictions,  20,   ct);
        // shortLongGap: the sudden-drift threshold. If the short-window accuracy falls more than
        // this amount below the long-window accuracy, the model has likely encountered a regime change.
        double shortLongGap     = await GetConfigAsync<double>(readCtx, CK_ShortLongGap,    0.07, ct);
        // longWindowFloor: the gradual-drift threshold. If the long-window accuracy falls below
        // this level, the model is performing at or below random-chance and must be retrained.
        double longWindowFloor  = await GetConfigAsync<double>(readCtx, CK_LongWindowFloor, 0.50, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelDriftAsync(
                    model, readCtx, writeCtx,
                    shortWindowDays, longWindowDays, minPredictions,
                    shortLongGap, longWindowFloor, ct);
            }
            catch (Exception ex)
            {
                // Per-model failures are non-fatal — skip the failing model and continue.
                // This prevents a corrupted prediction log for one symbol from blocking all others.
                _logger.LogWarning(ex,
                    "Multi-scale drift check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Core per-model multi-scale drift analysis. Computes accuracy over both windows and
    /// applies the sudden-drift and gradual-drift decision rules to determine whether
    /// retraining and alerting are required.
    /// </summary>
    /// <remarks>
    /// <b>Decision rules:</b>
    /// <list type="number">
    ///   <item>
    ///     <b>Sudden drift:</b> <c>gap = shortAccuracy − longAccuracy &lt; −shortLongGap</c><br/>
    ///     The short window has degraded sharply compared to the long-term baseline.
    ///     This pattern indicates an abrupt regime change or data-pipeline fault.
    ///     Severity: <c>critical</c>.
    ///   </item>
    ///   <item>
    ///     <b>Gradual drift (mutually exclusive with sudden):</b>
    ///     <c>longAccuracy &lt; longWindowFloor</c><br/>
    ///     Both windows have drifted below an absolute accuracy floor, indicating slow
    ///     concept drift or a sustained regime shift over weeks. Severity: <c>standard</c>.
    ///   </item>
    /// </list>
    /// <b>Deduplication:</b>
    /// Training runs are guarded by checking for existing Queued/Running runs on the same
    /// symbol+timeframe. Alerts are deduped by checking for an existing active alert of type
    /// <see cref="AlertType.MLModelDegraded"/> for the same symbol.
    /// </remarks>
    /// <param name="model">Active ML model to evaluate.</param>
    /// <param name="readCtx">Read-side EF context.</param>
    /// <param name="writeCtx">Write-side EF context.</param>
    /// <param name="shortWindowDays">Size of the recent/fast window in calendar days.</param>
    /// <param name="longWindowDays">Size of the baseline/slow window in calendar days.</param>
    /// <param name="minPredictions">
    /// Minimum resolved predictions required in the long window. Below this the accuracy
    /// estimates have insufficient statistical power for reliable comparison.
    /// </param>
    /// <param name="shortLongGap">
    /// Minimum negative gap (shortAccuracy − longAccuracy) that triggers sudden-drift detection.
    /// A default of 0.07 means a 7-percentage-point sharper drop in the short window vs the long.
    /// </param>
    /// <param name="longWindowFloor">
    /// Minimum acceptable long-window accuracy. Below 0.50 the model is no better than coin-flip
    /// on a directional trading decision and must be retrained immediately.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckModelDriftAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     shortWindowDays,
        int                                     longWindowDays,
        int                                     minPredictions,
        double                                  shortLongGap,
        double                                  longWindowFloor,
        CancellationToken                       ct)
    {
        // Define the time boundaries for each window.
        var longSince  = DateTime.UtcNow.AddDays(-longWindowDays);
        var shortSince = DateTime.UtcNow.AddDays(-shortWindowDays);

        // ── Load all resolved predictions within the long window ───────────────
        // Loading the full long window first then filtering in-memory for the short window
        // avoids a second database round-trip. The projection keeps memory usage small —
        // we only need the timestamp and the binary outcome.
        var allResolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id  &&
                        l.PredictedAt      >= longSince &&
                        l.DirectionCorrect != null       &&
                        !l.IsDeleted)
            .Select(l => new { l.PredictedAt, DirectionCorrect = l.DirectionCorrect!.Value })
            .AsNoTracking()
            .ToListAsync(ct);

        if (allResolved.Count < minPredictions)
        {
            // Too few data points to compute a stable long-window accuracy estimate.
            _logger.LogDebug(
                "MultiScaleDrift: {Symbol}/{Tf} model {Id} only {N} resolved in long window — skip.",
                model.Symbol, model.Timeframe, model.Id, allResolved.Count);
            return;
        }

        // ── Long-window accuracy ───────────────────────────────────────────────
        // The proportion of correct directional predictions over the full long window.
        // This is the baseline/reference; the short-window accuracy is compared against it.
        double longAccuracy = allResolved.Count(r => r.DirectionCorrect) / (double)allResolved.Count;

        // ── Short-window accuracy ──────────────────────────────────────────────
        // Filter to the recent sub-window in-memory (already loaded above).
        var shortResolved = allResolved.Where(r => r.PredictedAt >= shortSince).ToList();

        // Require at least max(5, minPredictions/4) predictions in the short window.
        // With fewer points the short-window accuracy is too volatile to be actionable.
        if (shortResolved.Count < Math.Max(5, minPredictions / 4))
        {
            _logger.LogDebug(
                "MultiScaleDrift: {Symbol}/{Tf} model {Id} insufficient short-window predictions ({N}) — skip.",
                model.Symbol, model.Timeframe, model.Id, shortResolved.Count);
            return;
        }

        double shortAccuracy = shortResolved.Count(r => r.DirectionCorrect) / (double)shortResolved.Count;

        // ── Compute the inter-window accuracy gap ─────────────────────────────
        // gap > 0: model is performing better recently than on average (improving).
        // gap < 0: model is performing worse recently than on average (degrading).
        // gap < -shortLongGap: the degradation is large enough to signal sudden drift.
        double gap = shortAccuracy - longAccuracy;

        _logger.LogDebug(
            "MultiScaleDrift: {Symbol}/{Tf} model {Id}: short={Short:P1}(n={Ns}) " +
            "long={Long:P1}(n={Nl}) gap={Gap:+0.0%;-0.0%}",
            model.Symbol, model.Timeframe, model.Id,
            shortAccuracy, shortResolved.Count, longAccuracy, allResolved.Count, gap);

        // ── Apply drift decision rules ────────────────────────────────────────
        // Sudden drift takes precedence: if the gap is large enough, gradual drift is irrelevant.
        bool suddenDrift  = gap < -shortLongGap;
        // Gradual drift: only evaluated when sudden drift was not triggered. Long-window accuracy
        // falling below the floor means even the "smoothed" baseline has collapsed.
        bool gradualDrift = !suddenDrift && longAccuracy < longWindowFloor;

        if (!suddenDrift && !gradualDrift) return; // Model is performing within acceptable bounds

        string driftType = suddenDrift ? "sudden" : "gradual";

        _logger.LogWarning(
            "MultiScaleDrift: {Symbol}/{Tf} model {Id}: {DriftType} drift detected. " +
            "short={Short:P1} long={Long:P1} gap={Gap:+0.0%;-0.0%} floor={Floor:P1}",
            model.Symbol, model.Timeframe, model.Id, driftType,
            shortAccuracy, longAccuracy, gap, longWindowFloor);

        // ── Queue retrain if not already pending ──────────────────────────────
        // Guard against duplicate training runs. We check the read context (which sees committed
        // rows from previous cycles) rather than the write context to include rows from prior runs.
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        bool saved = false;

        if (!alreadyQueued)
        {
            // Embed diagnostic context in HyperparamConfigJson so the MLTrainingWorker and
            // operators can trace why this retrain was triggered.
            writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol    = model.Symbol,
                Timeframe = model.Timeframe,
                Status    = RunStatus.Queued,
                FromDate  = DateTime.UtcNow.AddDays(-365),
                ToDate    = DateTime.UtcNow,
                HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    triggeredBy    = "MLMultiScaleDriftWorker",
                    driftType,                  // "sudden" or "gradual"
                    shortAccuracy,              // Recent accuracy over shortWindowDays
                    longAccuracy,               // Baseline accuracy over longWindowDays
                    gap,                        // shortAccuracy − longAccuracy
                    shortWindowDays,
                    longWindowDays,
                    modelId        = model.Id,
                }),
            });
            saved = true;
        }

        // ── Create alert (deduplicated by active alert check) ─────────────────
        // Only create a new alert if there is no existing active alert for this symbol.
        // This prevents flooding the alerting channel during sustained periods of degradation
        // when the model fails to recover between polling cycles.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == model.Symbol              &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (!alertExists)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = model.Symbol,
                Channel       = AlertChannel.Webhook,
                Destination   = "ml-ops",
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason          = "multi_scale_drift",
                    driftType,                              // "sudden" or "gradual"
                    severity        = suddenDrift ? "critical" : "standard", // Severity tier for ops routing
                    shortAccuracy,                          // Fast-window accuracy at time of detection
                    longAccuracy,                           // Slow-window baseline accuracy
                    gap,                                    // shortAccuracy − longAccuracy
                    shortWindowDays,
                    longWindowDays,
                    symbol          = model.Symbol,
                    timeframe       = model.Timeframe.ToString(),
                    modelId         = model.Id,
                }),
                IsActive = true,
            });
            saved = true;
        }

        // Persist all changes in a single transaction for atomicity.
        if (saved) await writeCtx.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads a strongly-typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or the stored value
    /// cannot be converted to <typeparamref name="T"/>, so the worker always has a safe
    /// operational default without requiring config entries to exist at startup.
    /// </summary>
    /// <typeparam name="T">Target type; must be supported by <see cref="Convert.ChangeType(object, Type)"/>.</typeparam>
    /// <param name="ctx">EF Core DbContext (use the read-side context to avoid change-tracker overhead).</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> to look up.</param>
    /// <param name="defaultValue">Fallback value used when the key is missing or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed config value, or <paramref name="defaultValue"/> on any failure.</returns>
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
        catch { return defaultValue; } // Malformed config value — fall back silently
    }
}
