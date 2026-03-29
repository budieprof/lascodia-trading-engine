using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Performs online EMA-based adaptation of the ML model's decision threshold without
/// requiring a full retrain.
///
/// <para>
/// The worker loads the most recent N resolved <see cref="MLModelPredictionLog"/> records
/// for each active model, then sweeps threshold values from 0.3 to 0.7 in steps of 0.01
/// and picks the threshold that maximises the expected value (EV) on the recent window.
/// </para>
///
/// <para>
/// The new threshold is blended with the existing <see cref="ModelSnapshot.AdaptiveThreshold"/>
/// using an EMA: θ_new = α × θ_optimal + (1 − α) × θ_current, where α is configurable
/// (default 0.2). The blended threshold is then written back to <see cref="MLModel.ModelBytes"/>.
/// </para>
///
/// <para>
/// Adaptation is skipped when:
/// <list type="bullet">
///   <item>Fewer than <c>MLAdaptiveThreshold:MinResolvedPredictions</c> resolved logs are available.</item>
///   <item>The absolute change in threshold is below <c>MLAdaptiveThreshold:MinThresholdDrift</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MLAdaptiveThresholdWorker : BackgroundService
{
    // ── EngineConfig key constants ─────────────────────────────────────────────

    /// <summary>Seconds between threshold adaptation cycles (default 3600 = 1 h).
    /// Hourly adaptation is appropriate because the EMA smoothing (alpha = 0.2) already
    /// dampens rapid changes. Running more frequently would waste DB resources without
    /// meaningfully changing the threshold since the underlying prediction window barely shifts.</summary>
    private const string CK_PollSecs         = "MLAdaptiveThreshold:PollIntervalSeconds";

    /// <summary>Maximum number of recent resolved prediction logs to load per model for the
    /// threshold sweep (default 500). Larger windows provide more stable EV estimates but
    /// increase DB load and the risk of including stale market regimes in the computation.</summary>
    private const string CK_WindowSize       = "MLAdaptiveThreshold:WindowSize";

    /// <summary>Minimum number of resolved predictions required before threshold adaptation
    /// is attempted (default 100). This guards against overfitting the threshold sweep to
    /// a small, potentially unrepresentative sample of predictions.</summary>
    private const string CK_MinPredictions   = "MLAdaptiveThreshold:MinResolvedPredictions";

    /// <summary>EMA smoothing factor alpha used to blend the new optimal threshold with the
    /// existing threshold (default 0.2). Formula: θ_new = α × θ_optimal + (1 - α) × θ_current.
    /// A lower alpha produces a slower, more stable adaptation (high inertia).
    /// A higher alpha reacts faster but is noisier. 0.2 = roughly 4-cycle lag to full convergence.</summary>
    private const string CK_EmaAlpha         = "MLAdaptiveThreshold:EmaAlpha";

    /// <summary>Minimum absolute change in threshold value required before the snapshot is
    /// re-serialised and written to the DB (default 0.01 = 1 percentage point).
    /// Prevents spurious writes when the EMA blending produces a sub-pip threshold change
    /// that has no practical effect on signal generation.</summary>
    private const string CK_MinDrift         = "MLAdaptiveThreshold:MinThresholdDrift";

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly IMemoryCache                          _cache;
    private readonly ILogger<MLAdaptiveThresholdWorker>    _logger;
    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    /// <summary>
    /// Initialises the worker with scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">Used to create per-iteration DI scopes for safe scoped service access.</param>
    /// <param name="logger">Structured logger for threshold adaptation events and diagnostics.</param>
    public MLAdaptiveThresholdWorker(
        IServiceScopeFactory                 scopeFactory,
        IMemoryCache                         cache,
        ILogger<MLAdaptiveThresholdWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    /// <summary>
    /// Main background loop. Runs indefinitely until the host signals cancellation.
    /// On each iteration:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope for scoped DbContext access.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/> (hot-reload).</item>
    ///   <item>Delegates to <see cref="AdaptAllModelsAsync"/> to update thresholds for all active models.</item>
    ///   <item>Sleeps for the configured poll interval (default 1 h) before the next cycle.</item>
    /// </list>
    /// This worker is purely reactive — it does not send alerts or suppress signals. Its only
    /// side effect is updating <c>MLModel.ModelBytes</c> with a revised <see cref="ModelSnapshot"/>
    /// containing updated global and per-regime thresholds. The <c>MLSignalScorer</c> reads these
    /// thresholds at scoring time to determine whether a prediction's confidence is sufficient
    /// to emit a <see cref="TradeSignal"/>.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLAdaptiveThresholdWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                // New DI scope per iteration — EF DbContexts are scoped and must not outlive a tick.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Hot-reload: re-read poll interval each cycle.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await AdaptAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLAdaptiveThresholdWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLAdaptiveThresholdWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all active ML models that have serialised <c>ModelBytes</c> and delegates to
    /// <see cref="AdaptModelAsync"/> for each one. Models without <c>ModelBytes</c> are skipped
    /// because the threshold is stored inside the <see cref="ModelSnapshot"/> which lives in
    /// that field — there is nowhere to persist the updated threshold without it.
    ///
    /// All adaptation parameters are read from <see cref="EngineConfig"/> once upfront to avoid
    /// N+1 configuration queries per model.
    /// Per-model failures are isolated so one model's deserialisation error cannot block
    /// adaptation for all other models.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for model, prediction log, and regime snapshot queries.</param>
    /// <param name="writeCtx">Write DbContext for persisting updated ModelBytes.</param>
    /// <param name="ct">Cancellation token from the host.</param>
    private async Task AdaptAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read all adaptation parameters once — avoids repeated EngineConfig queries per model.
        int    windowSize      = await GetConfigAsync<int>   (readCtx, CK_WindowSize,     500,  ct);
        int    minPredictions  = await GetConfigAsync<int>   (readCtx, CK_MinPredictions, 100,  ct);
        double emaAlpha        = await GetConfigAsync<double>(readCtx, CK_EmaAlpha,       0.2,  ct);
        double minDrift        = await GetConfigAsync<double>(readCtx, CK_MinDrift,       0.01, ct);

        // Only load models with ModelBytes — adaptation requires a deserialisable ModelSnapshot.
        // Models without bytes have not yet been trained with threshold support and must be skipped.
        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .ToListAsync(ct);

        _logger.LogDebug("Adaptive threshold: {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await AdaptModelAsync(model, readCtx, writeCtx,
                    windowSize, minPredictions, emaAlpha, minDrift, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-model failures — one bad snapshot should not block all others.
                _logger.LogWarning(ex,
                    "Adaptive threshold failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Performs the full threshold adaptation pipeline for a single ML model:
    ///
    /// <b>Phase 1 — Global threshold sweep:</b>
    /// <list type="number">
    ///   <item>Load the most recent <paramref name="windowSize"/> resolved prediction logs.</item>
    ///   <item>Skip if fewer than <paramref name="minPredictions"/> are available.</item>
    ///   <item>Sweep threshold values from 0.30 to 0.70 in steps of 0.01.</item>
    ///   <item>At each threshold, simulate trading on the window using <see cref="ComputeEvAtThreshold"/>.</item>
    ///   <item>Select the threshold that maximised EV (expected value = win − loss fraction).</item>
    ///   <item>Blend with the current threshold using EMA:
    ///         <c>θ_new = α × θ_optimal + (1 - α) × θ_current</c>.</item>
    ///   <item>Only persist if absolute drift ≥ <paramref name="minDrift"/>.</item>
    /// </list>
    ///
    /// <b>Phase 2 — Regime-conditioned threshold sweep:</b>
    /// <list type="number">
    ///   <item>Load recent <see cref="MarketRegimeSnapshot"/> records for this model's symbol/timeframe.</item>
    ///   <item>Assign each prediction log to the most recent regime snapshot that preceded it
    ///         (using a temporal join on <c>DetectedAt &lt;= PredictedAt</c>).</item>
    ///   <item>For each regime with at least 20 observations, repeat the EV threshold sweep.</item>
    ///   <item>Blend the per-regime optimal threshold with the existing regime threshold using the same EMA.</item>
    ///   <item>Only update if regime-level drift ≥ <paramref name="minDrift"/>.</item>
    /// </list>
    ///
    /// <b>Write-back:</b> If any threshold changed (global or per-regime), the updated
    /// <see cref="ModelSnapshot"/> is serialised to UTF-8 JSON and written to <c>MLModel.ModelBytes</c>
    /// via a bulk <c>ExecuteUpdateAsync</c> — no entity tracking required.
    /// </summary>
    /// <param name="model">The ML model to adapt (loaded with AsNoTracking; ModelBytes is non-null).</param>
    /// <param name="readCtx">Read-only DbContext for prediction log and regime snapshot queries.</param>
    /// <param name="writeCtx">Write DbContext for persisting the updated ModelBytes.</param>
    /// <param name="windowSize">Maximum number of recent resolved prediction logs to include in the sweep.</param>
    /// <param name="minPredictions">Minimum resolved logs required to proceed with adaptation.</param>
    /// <param name="emaAlpha">EMA blending factor (0 = no change, 1 = full replacement with optimal).</param>
    /// <param name="minDrift">Minimum absolute threshold change required to trigger a DB write.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task AdaptModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        int                                     minPredictions,
        double                                  emaAlpha,
        double                                  minDrift,
        CancellationToken                       ct)
    {
        // ── Load resolved prediction logs ─────────────────────────────────────
        // Only resolved logs (DirectionCorrect != null) are useful — pending outcomes
        // cannot contribute to the EV sweep since we don't know whether they were correct.
        // OrderByDescending on OutcomeRecordedAt ensures we get the most recently
        // resolved outcomes rather than merely the most recently predicted ones.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId        == model.Id &&
                        l.DirectionCorrect != null   &&
                        l.ActualDirection.HasValue   &&
                        l.OutcomeRecordedAt != null  &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.OutcomeRecordedAt)
            .ThenByDescending(l => l.Id)
            .Take(windowSize)
            .ToListAsync(ct);

        // Guard: insufficient data to produce a statistically reliable threshold estimate.
        if (logs.Count < minPredictions)
        {
            _logger.LogDebug(
                "Adaptive threshold skipped model {Id} — only {N} resolved logs (need {Min}).",
                model.Id, logs.Count, minPredictions);
            return;
        }

        // ── Sweep threshold to maximise EV ────────────────────────────────────
        // Step through thresholds from 0.30 to 0.70 in increments of 0.01 (41 steps total).
        // The range 0.30–0.70 is intentional: below 0.30 a model would be nearly always trading
        // (too aggressive), and above 0.70 it would almost never trade (too conservative).
        double bestThreshold = 0.5;  // sensible default if all steps tie
        double bestEv        = double.MinValue;

        for (int step = 30; step <= 70; step++)
        {
            double thr = step / 100.0;
            double ev  = ComputeEvAtThreshold(logs, thr);
            if (ev > bestEv)
            {
                bestEv        = ev;
                bestThreshold = thr;
            }
        }

        var (writeModel, snap) = await MLModelSnapshotWriteHelper
            .LoadTrackedLatestSnapshotAsync(writeCtx, model.Id, ct);
        if (writeModel == null || snap == null)
            return;

        // Start from the same deployed threshold precedence as live scoring so the first
        // adaptive update blends from the true in-production baseline, not an arbitrary 0.5.
        double currentThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

        // EMA blend: smoothly move the threshold toward the optimal value.
        // Alpha = 0.2 means 20 % weight on the new optimal and 80 % on the existing value.
        // This prevents threshold thrashing when the optimal value fluctuates between cycles.
        double newThreshold     = emaAlpha * bestThreshold + (1.0 - emaAlpha) * currentThreshold;
        double drift            = Math.Abs(newThreshold - currentThreshold);

        // Track whether any part of the snapshot was updated — used to decide whether to write-back.
        bool anyUpdate = false;

        if (drift >= minDrift)
        {
            // Drift is meaningful — update the global threshold in the snapshot.
            snap.AdaptiveThreshold = newThreshold;
            anyUpdate = true;

            _logger.LogInformation(
                "Adaptive threshold updated model {Id} ({Symbol}/{Tf}): " +
                "{Old:F4} → {New:F4} (optimal={Opt:F4}, EV={Ev:F4}, drift={Drift:F4}).",
                model.Id, model.Symbol, model.Timeframe,
                currentThreshold, newThreshold, bestThreshold, bestEv, drift);
        }
        else
        {
            // Change is below the noise floor — skip the global update to avoid redundant DB writes.
            _logger.LogDebug(
                "Adaptive threshold model {Id}: global drift {Drift:F4} < minDrift {Min:F4} — skipping global update.",
                model.Id, drift, minDrift);
        }

        // ── Regime-conditioned threshold sweep ────────────────────────────────
        // Load recent regime snapshots to assign each prediction log to a market regime.
        // This enables the model to use a higher threshold during trending regimes (more selective)
        // and a lower threshold during ranging regimes (more permissive), for example.
        var regimeSnapshots = await readCtx.Set<MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(r => r.Symbol    == model.Symbol    &&
                        r.Timeframe == model.Timeframe &&
                        !r.IsDeleted)
            .OrderByDescending(r => r.DetectedAt)
            .Take(2000)  // 2000 snapshots ≈ ~83 days of hourly regime detection
            .ToListAsync(ct);

        if (regimeSnapshots.Count > 0)
        {
            // Group prediction logs by the closest prior regime snapshot.
            // This is a temporal left join: for each prediction log, find the latest regime
            // snapshot whose DetectedAt is at or before the log's PredictedAt timestamp.
            var regimeGroups = new Dictionary<string, List<MLModelPredictionLog>>();

            foreach (var log in logs)
            {
                // The OrderByDescending query above ensures regimeSnapshots[0] is the latest.
                // FirstOrDefault (ordered desc) efficiently finds the most recent snapshot
                // that pre-dates this prediction — equivalent to a "last known regime" lookup.
                var regime = regimeSnapshots
                    .FirstOrDefault(r => r.DetectedAt <= log.PredictedAt);

                // Assign to "Unknown" bucket when no prior regime snapshot exists for this period.
                // This typically happens for prediction logs older than the oldest regime snapshot.
                string regimeName = regime?.Regime.ToString() ?? "Unknown";
                if (!regimeGroups.TryGetValue(regimeName, out var group))
                {
                    group = [];
                    regimeGroups[regimeName] = group;
                }
                group.Add(log);
            }

            // Ensure the RegimeThresholds dictionary exists in the snapshot before writing to it.
            snap.RegimeThresholds ??= [];

            foreach (var (regimeName, regimeLogs) in regimeGroups)
            {
                // Skip regimes with fewer than 20 observations — the EV sweep would overfit
                // to a tiny sample and produce a threshold that does not generalise.
                if (regimeLogs.Count < 20) continue;

                // Repeat the full EV threshold sweep using only logs from this specific regime.
                double regimeBestThr = 0.5;
                double regimeBestEv  = double.MinValue;

                for (int step = 30; step <= 70; step++)
                {
                    double thr = step / 100.0;
                    double ev  = ComputeEvAtThreshold(regimeLogs, thr);
                    if (ev > regimeBestEv) { regimeBestEv = ev; regimeBestThr = thr; }
                }

                // Start a new regime-specific threshold from the currently deployed
                // global baseline, not a hardcoded 0.5, so the first regime override
                // blends smoothly from what production is already using.
                double currentRegimeThr = snap.RegimeThresholds.TryGetValue(regimeName, out var existing)
                    ? existing : currentThreshold;

                // Apply the same EMA blend to the regime-conditioned threshold.
                double newRegimeThr = emaAlpha * regimeBestThr + (1.0 - emaAlpha) * currentRegimeThr;
                double regimeDrift  = Math.Abs(newRegimeThr - currentRegimeThr);

                if (regimeDrift >= minDrift)
                {
                    // Regime threshold changed enough to be worth persisting.
                    snap.RegimeThresholds[regimeName] = newRegimeThr;
                    anyUpdate = true;

                    _logger.LogInformation(
                        "Regime threshold updated model {Id} ({Symbol}/{Tf}) regime={Regime}: " +
                        "{Old:F4} → {New:F4} (optimal={Opt:F4}, EV={Ev:F4}, N={N}).",
                        model.Id, model.Symbol, model.Timeframe, regimeName,
                        currentRegimeThr, newRegimeThr, regimeBestThr, regimeBestEv, regimeLogs.Count);
                }
            }
        }

        // Skip the write-back if neither the global nor any regime threshold actually changed.
        // This avoids redundant serialise + DB update round trips on stable models.
        if (!anyUpdate) return;

        // ── Write updated snapshot back to ModelBytes ─────────────────────────
        // Serialise the entire ModelSnapshot to UTF-8 JSON bytes and update in place.
        // ExecuteUpdateAsync avoids loading the full entity into the change tracker.
        writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
        await writeCtx.SaveChangesAsync(ct);
        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
    }

    // ── EV computation ────────────────────────────────────────────────────────

    /// <summary>
    /// Simulates trading on the resolved prediction window using a fixed threshold and
    /// returns the mean per-trade expected value (win fraction − loss fraction).
    ///
    /// <b>How prediction is reconstructed from logged data:</b>
    /// The stored <see cref="MLModelPredictionLog.ConfidenceScore"/> is a model-output confidence
    /// value, not a raw probability. The calibrated probability is approximated as:
    /// <list type="bullet">
    ///   <item>For Buy predictions:  <c>calibP ≈ threshold + conf / 2</c></item>
    ///   <item>For Sell predictions: <c>calibP ≈ threshold - conf / 2</c></item>
    /// </list>
    /// A prediction is considered "active" (i.e. the model would have emitted a signal) when
    /// <c>calibP &gt;= threshold</c>. The method then compares the simulated prediction against
    /// the known outcome (<see cref="MLModelPredictionLog.DirectionCorrect"/>).
    ///
    /// <b>EV formula:</b> <c>EV = (wins - losses) / (wins + losses)</c>
    /// <list type="bullet">
    ///   <item>+1.0 = all predictions correct (impossible in practice)</item>
    ///   <item> 0.0 = equal wins and losses (break-even, same as random)</item>
    ///   <item>-1.0 = all predictions wrong</item>
    /// </list>
    /// The threshold that maximises this value provides the best risk-adjusted selectivity on
    /// the observed prediction window.
    ///
    /// <b>Limitation:</b> This approximation assumes confidence is linearly related to calibrated
    /// probability. For models with non-linear probability outputs (e.g. neural networks), a
    /// full Platt or isotonic calibration pass during training would produce more accurate results.
    /// </summary>
    /// <param name="logs">Resolved prediction logs to simulate against (must have non-null <c>DirectionCorrect</c>).</param>
    /// <param name="threshold">The decision threshold to evaluate (0.30 – 0.70).</param>
    /// <returns>Expected value in [-1, +1] for this threshold on the provided prediction window.</returns>
    private static double ComputeEvAtThreshold(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              threshold)
    {
        int wins = 0, losses = 0;

        foreach (var log in logs)
        {
            // Skip any log whose outcome was not resolved — should not occur in practice
            // because the query filters DirectionCorrect != null, but defensive check.
            if (log.DirectionCorrect is null) continue;

            // Reconstruct the calibrated probability from direction + confidence score.
            // Approximate inverse: calibP ≈ threshold + conf/2 (for Buy direction).
            // For Sell, a high confidence score means the model strongly predicts downward movement,
            // so we subtract conf/2 to move the probability below the threshold midpoint.
            double calibP = MLFeatureHelper.ResolveLoggedServedBuyProbability(log, threshold);

            bool predictedBuy = calibP >= threshold;
            bool actualBuy    = log.ActualDirection == TradeDirection.Buy;

            // Compare simulated prediction to actual outcome.
            if (predictedBuy == actualBuy) wins++;
            else                          losses++;
        }

        int total = wins + losses;
        if (total == 0) return 0;  // no scorable predictions in this window — neutral EV

        // Return the normalised EV: positive means more wins than losses at this threshold.
        return (double)(wins - losses) / total;
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key does not exist or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type — typically <c>int</c> or <c>double</c>.</typeparam>
    /// <param name="ctx">Any DbContext with access to the EngineConfig set.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Fallback returned when the key is absent or unparseable.</param>
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

        // Convert.ChangeType handles common primitive conversions (string → int, string → double).
        // Any conversion failure silently falls back to the safe default value.
        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
