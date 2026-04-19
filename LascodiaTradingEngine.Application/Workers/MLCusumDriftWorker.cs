using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects bidirectional accuracy drift in active ML models using the Cumulative Sum (CUSUM)
/// control chart algorithm — a classical sequential hypothesis test optimised for detecting
/// <em>sudden, step-change</em> shifts in a process mean.
///
/// <para>
/// <b>Where it sits in the ML monitoring pipeline:</b><br/>
/// The system operates several complementary drift detectors, each sensitive to a different
/// drift signature:
/// <list type="bullet">
///   <item><see cref="MLCusumDriftWorker"/> (this class) — sudden step changes in binary accuracy.</item>
///   <item><c>MLPageHinkleyDriftWorker</c> — gradual upward mean shifts (slower concept drift).</item>
///   <item><see cref="MLAdwinDriftWorker"/> — adaptive windowing; no fixed window required.</item>
///   <item><see cref="MLMultiScaleDriftWorker"/> — compares short vs long rolling windows.</item>
///   <item><see cref="MLPeltChangePointWorker"/> — globally optimal multiple change-point detection on price returns.</item>
///   <item><see cref="MLStructuralBreakWorker"/> — Bai-Perron structural break test for regime changes.</item>
/// </list>
/// Running CUSUM alongside Page-Hinkley gives the system sensitivity to both the speed
/// and direction of distributional shifts in model performance.
/// </para>
///
/// <para>
/// <b>Algorithm — two one-sided CUSUM accumulators:</b><br/>
/// Two statistics run in parallel over the chronologically ordered prediction outcomes:
/// <list type="bullet">
///   <item>
///     <b>S⁺ (degradation detector):</b> accumulates evidence that accuracy has fallen
///     below the reference level. Fires when S⁺ ≥ h.<br/>
///     Update rule: <c>S⁺ₙ = max(0, S⁺ₙ₋₁ + (μ₀ − xₙ) − k)</c>
///   </item>
///   <item>
///     <b>S⁻ (improvement detector):</b> accumulates evidence that accuracy has risen
///     above the reference level. Fires when S⁻ ≥ h.<br/>
///     Update rule: <c>S⁻ₙ = max(0, S⁻ₙ₋₁ + (xₙ − μ₀) − k)</c>
///   </item>
/// </list>
/// Where:
/// <list type="bullet">
///   <item><c>μ₀</c> — reference (baseline) accuracy estimated from the first half of the observation window.</item>
///   <item><c>xₙ</c> — binary outcome of the nth prediction (1.0 = correct, 0.0 = incorrect).</item>
///   <item><c>k</c> — allowable slack (sensitivity tuning knob); smaller k = more sensitive but more false positives.</item>
///   <item><c>h</c> — decision interval (alarm threshold); larger h = fewer alarms but slower detection.</item>
/// </list>
/// The <c>max(0, ...)</c> reset is the key property that makes CUSUM a sequential test:
/// when evidence accumulates in the wrong direction (i.e., the model is performing well),
/// the statistic resets to zero rather than going negative, giving the detector "amnesia"
/// about past good performance while preserving memory of developing degradation.
/// </para>
///
/// <para>
/// <b>Reference partitioning strategy:</b><br/>
/// The worker splits each model's recent prediction window in half. The first half is used to
/// estimate the reference accuracy μ₀; the CUSUM accumulators are then run over the second half.
/// This avoids needing an explicit "training period" baseline stored externally and makes the
/// detector fully self-contained per polling cycle.
/// </para>
///
/// <para>
/// <b>On drift detection:</b><br/>
/// When S⁺ ≥ h (degradation alarm), the worker:
/// <list type="number">
///   <item>Checks whether a retraining run is already queued or running (suppression guard).</item>
///   <item>Writes an <see cref="Alert"/> of type <see cref="AlertType.MLModelDegraded"/> with full diagnostic JSON.</item>
///   <item>Creates a new <see cref="MLTrainingRun"/> with <c>TriggerType.AutoDegrading</c> to schedule retraining.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Configuration keys (stored in <see cref="EngineConfig"/> table):</b>
/// <list type="table">
///   <listheader><term>Key</term><description>Default / Description</description></listheader>
///   <item><term><c>MLCusum:PollIntervalSeconds</c></term><description>3600 — how often the worker wakes up (1 hour).</description></item>
///   <item><term><c>MLCusum:WindowSize</c></term><description>300 — maximum number of recent resolved predictions to examine.</description></item>
///   <item><term><c>MLCusum:K</c></term><description>0.005 — allowable slack (half the smallest detectable shift, in accuracy units).</description></item>
///   <item><term><c>MLCusum:H</c></term><description>5.0 — decision interval (alarm threshold for cumulative sum).</description></item>
///   <item><term><c>MLCusum:AlertDestination</c></term><description>"ml-ops" — webhook destination for drift alerts.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class MLCusumDriftWorker : BackgroundService
{
    private const string CK_PollSecs  = "MLCusum:PollIntervalSeconds";
    private const string CK_Window    = "MLCusum:WindowSize";
    private const string CK_K         = "MLCusum:K";
    private const string CK_H         = "MLCusum:H";
    private const string CK_AlertDest = "MLCusum:AlertDestination";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<MLCusumDriftWorker>     _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory used to create a new DI scope on every polling iteration, ensuring that
    /// scoped services (EF Core DbContexts) are properly disposed after each cycle.
    /// </param>
    /// <param name="logger">Structured logger for diagnostic and warning output.</param>
    public MLCusumDriftWorker(
        IServiceScopeFactory         scopeFactory,
        ILogger<MLCusumDriftWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point called by the .NET hosted-service infrastructure when the application starts.
    /// Runs the CUSUM drift check in a continuous polling loop until the application is shut down.
    /// </summary>
    /// <remarks>
    /// The poll interval is re-read from <see cref="EngineConfig"/> on every iteration so that
    /// operators can adjust the cadence at runtime without restarting the service. A fresh DI scope
    /// (and therefore fresh EF Core contexts) is created on every iteration to avoid stale
    /// change-tracker state and connection-pool exhaustion.
    /// </remarks>
    /// <param name="stoppingToken">
    /// Cancellation token injected by the host; signals a graceful shutdown request.
    /// <c>OperationCanceledException</c> is caught and exits the loop cleanly.
    /// </param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCusumDriftWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval — overridden per-iteration from EngineConfig.
            int pollSecs = 3600;

            try
            {
                // Create a fresh DI scope so EF DbContexts are properly scoped and disposed.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read poll interval from DB config on every iteration (allows runtime tuning).
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — exit the loop without logging as an error.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCusumDriftWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCusumDriftWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all active (non-deleted) ML models and runs a CUSUM drift check on each one.
    /// CUSUM parameters are read once per cycle from <see cref="EngineConfig"/> so they can
    /// be tuned by operators without restarting the service.
    /// </summary>
    /// <param name="readCtx">EF Core read-side context for querying models and prediction logs.</param>
    /// <param name="writeCtx">EF Core write-side context for persisting alerts and training run records.</param>
    /// <param name="ct">Cancellation token; <see cref="OperationCanceledException"/> propagates immediately.</param>
    private async Task CheckAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read CUSUM tuning parameters from runtime configuration.
        // windowSize: maximum number of recent resolved prediction logs to examine per model.
        int    windowSize = await GetConfigAsync<int>   (readCtx, CK_Window,    300,     ct);
        // k: allowable slack — half the minimum accuracy shift worth detecting.
        //    Smaller k → more sensitive, but also more false positives.
        double k          = await GetConfigAsync<double>(readCtx, CK_K,         0.005,   ct);
        // h: decision interval (alarm threshold for the cumulative sum statistic).
        //    Larger h → fewer false alarms but slower detection of real drift.
        double h          = await GetConfigAsync<double>(readCtx, CK_H,         5.0,     ct);
        string alertDest  = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        _logger.LogDebug("CUSUM drift: {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await CheckModelAsync(model, readCtx, writeCtx, windowSize, k, h, alertDest, ct);
            }
            catch (Exception ex)
            {
                // Per-model errors are non-fatal — log and continue with the remaining models
                // so that a bad prediction log for one model does not block others.
                _logger.LogWarning(ex,
                    "CUSUM drift check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Runs the full CUSUM drift detection pipeline for a single ML model.
    /// </summary>
    /// <remarks>
    /// <b>Step-by-step execution:</b>
    /// <list type="number">
    ///   <item>Loads up to <paramref name="windowSize"/> most-recent resolved prediction logs in chronological order.</item>
    ///   <item>Guards against insufficient data (minimum 30 resolved outcomes required).</item>
    ///   <item>Splits the window in half: first half estimates the reference accuracy μ₀;
    ///       second half is where the CUSUM accumulators are run.</item>
    ///   <item>Iterates through the second half sequentially, updating S⁺ and S⁻ at each step.</item>
    ///   <item>On S⁺ ≥ h, fires a "Degradation" alarm, suppresses if retrain already queued,
    ///       otherwise persists an Alert and a new MLTrainingRun.</item>
    /// </list>
    /// </remarks>
    /// <param name="model">The active ML model being evaluated.</param>
    /// <param name="readCtx">Read-side EF Core context.</param>
    /// <param name="writeCtx">Write-side EF Core context for persisting alerts and training runs.</param>
    /// <param name="windowSize">Maximum number of recent resolved prediction logs to consider.</param>
    /// <param name="k">
    /// CUSUM allowable slack parameter. Conceptually equal to half the minimum accuracy shift
    /// worth detecting (δ/2). A value of 0.005 means the detector is tuned to find shifts
    /// of ~1% in accuracy as quickly as possible while tolerating small noise.
    /// </param>
    /// <param name="h">
    /// CUSUM decision interval. The alarm fires when the cumulative sum exceeds this value.
    /// Higher h means more evidence must accumulate before an alarm fires — fewer false positives
    /// but longer detection delay. The default of 5.0 corresponds roughly to a 5-standard-deviation
    /// shift-evidence threshold when individual outcomes are Bernoulli(μ₀).
    /// </param>
    /// <param name="alertDest">Webhook destination string embedded in the Alert record.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        double                                  k,
        double                                  h,
        string                                  alertDest,
        CancellationToken                       ct)
    {
        // ── Load resolved prediction logs in chronological order ──────────────
        // Only outcomes where DirectionCorrect has been resolved (non-null) are usable.
        // Take the most recent `windowSize` records, then reverse to chronological order.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId       == model.Id &&
                        l.DirectionCorrect != null    &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .ToListAsync(ct);

        if (logs.Count < 30)
        {
            // CUSUM needs enough data to estimate a stable reference accuracy in the first half.
            // Logged at Debug because insufficient resolved-prediction history is the normal
            // bootstrap state for a fresh model and does not warrant per-cycle warning-level
            // noise. Sustained starvation (a mature model still below 30 resolved logs) is
            // surfaced by separate prediction-resolution health workers.
            _logger.LogDebug(
                "CUSUM skipped model {Id} ({Symbol}/{Tf}) — only {N} resolved logs (need 30). " +
                "Drift detection will activate once prediction data accumulates.",
                model.Id, model.Symbol, model.Timeframe, logs.Count);
            return;
        }

        // Reverse to chronological order (oldest → newest) so the CUSUM
        // accumulators see observations in the correct temporal sequence.
        logs.Reverse();

        // ── Estimate reference accuracy from the first half ───────────────────
        // μ₀ = proportion correct in the "stable" reference period (older half of window).
        // This is analogous to the in-control mean in classical SPC (Statistical Process Control).
        int    refHalf    = logs.Count / 2;
        double refAcc     = logs.Take(refHalf).Count(l => l.DirectionCorrect == true) / (double)refHalf;
        // recentLogs is the "monitoring" period over which CUSUM accumulators are run.
        var    recentLogs = logs.Skip(refHalf).ToList();

        // ── Run CUSUM accumulators on the second half ─────────────────────────
        // Both accumulators start at zero (no evidence of drift at the beginning of the monitoring period).
        double sPlus  = 0; // S⁺: accumulates evidence of accuracy *degradation* (downward shift)
        double sMinus = 0; // S⁻: accumulates evidence of accuracy *improvement* (upward shift)
        bool   fired  = false;
        string fireType = string.Empty;
        int    fireIdx  = -1; // Index in recentLogs at which the alarm first fires

        for (int i = 0; i < recentLogs.Count; i++)
        {
            // Convert binary outcome to a numeric signal: correct=1.0, incorrect=0.0.
            double x = recentLogs[i].DirectionCorrect == true ? 1.0 : 0.0;

            // ── S⁺ update: degradation CUSUM ──────────────────────────────────
            // The increment (refAcc − x − k) is positive when the model underperforms
            // relative to its reference accuracy (minus the allowable slack k).
            // max(0, ...) resets evidence to zero when the model is performing above
            // (refAcc − k), preventing the statistic from going negative.
            // Formula: S⁺ₙ = max(0, S⁺ₙ₋₁ + (μ₀ − xₙ) − k)
            sPlus  = Math.Max(0, sPlus  + (refAcc - x) - k);

            // ── S⁻ update: improvement CUSUM ──────────────────────────────────
            // The increment (x − refAcc − k) is positive when the model overperforms.
            // Computed here for completeness and future use (improvement-triggered recalibration).
            // Formula: S⁻ₙ = max(0, S⁻ₙ₋₁ + (xₙ − μ₀) − k)
            sMinus = Math.Max(0, sMinus + (x - refAcc) - k);

            // ── Alarm check ────────────────────────────────────────────────────
            // S⁺ ≥ h means enough evidence has accumulated that the mean has dropped
            // by at least k below μ₀. This is the classical Page (1954) CUSUM alarm criterion.
            if (sPlus >= h && !fired)
            {
                fired    = true;
                fireType = "Degradation";
                fireIdx  = i; // Record the first index where the alarm fires
                break;        // Stop iterating — alarm already confirmed
            }
        }

        if (!fired)
        {
            _logger.LogDebug(
                "CUSUM model {Id} ({Symbol}/{Tf}): S+={SPlus:F2} S-={SMinus:F2} h={H:F1} — no drift.",
                model.Id, model.Symbol, model.Timeframe, sPlus, sMinus, h);
            return;
        }

        // Compute the observed accuracy over the sub-window up to the point the alarm fired.
        // This gives a human-readable "recent accuracy" figure for the alert payload.
        double recentAcc = recentLogs.Take(fireIdx + 1).Count(l => l.DirectionCorrect == true)
                         / (double)(fireIdx + 1);

        _logger.LogWarning(
            "CUSUM {Type} drift detected model {Id} ({Symbol}/{Tf}): " +
            "refAcc={Ref:P1} recentAcc={Recent:P1} S+={SPlus:F2} >= h={H:F1} at step {Step}/{Total}.",
            fireType, model.Id, model.Symbol, model.Timeframe,
            refAcc, recentAcc, sPlus, h, fireIdx + 1, recentLogs.Count);

        // ── Suppress if retrain already queued ────────────────────────────────
        // Avoid creating duplicate training runs if another detector (or a previous CUSUM cycle)
        // has already queued a retrain for this symbol/timeframe. Check the write context
        // (not read context) to ensure we see uncommitted in-flight rows.
        bool retrainQueued = await writeCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (retrainQueued)
        {
            _logger.LogDebug(
                "CUSUM alert suppressed for model {Id} — retrain already queued.", model.Id);
            return;
        }

        // ── Persist alert ─────────────────────────────────────────────────────
        // The ConditionJson carries full diagnostic metadata so that downstream alert
        // handlers and the operations team can reconstruct what triggered the alarm.
        writeCtx.Set<Alert>().Add(new Alert
        {
            Symbol        = model.Symbol,
            AlertType     = AlertType.MLModelDegraded,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                DetectorType   = "CUSUM",
                ModelId        = model.Id,
                Timeframe      = model.Timeframe.ToString(),
                DriftType      = fireType,
                ReferenceAcc   = Math.Round(refAcc,    4),  // μ₀: baseline accuracy
                RecentAcc      = Math.Round(recentAcc, 4),  // Observed accuracy up to alarm point
                CusumSPlus     = Math.Round(sPlus,     4),  // S⁺ value at alarm
                DecisionInterval = h,                        // Alarm threshold h
                SlackK         = k,                          // Allowable slack k
                FireStep       = fireIdx + 1,                // Step index when alarm fired (1-based)
                WindowHalf     = recentLogs.Count,           // Size of the monitoring half-window
            }),
            IsActive = true,
        });

        // ── Queue retraining run ───────────────────────────────────────────────
        // The MLTrainingWorker picks up Queued runs and orchestrates the full retrain pipeline.
        // TriggerType.AutoDegrading distinguishes CUSUM-triggered retrains from manual or
        // scheduled ones, which is useful for auditing and performance attribution.
        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol      = model.Symbol,
            Timeframe   = model.Timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status      = RunStatus.Queued,
            FromDate    = DateTime.UtcNow.AddDays(-365), // Train on the last full year of data
            ToDate      = DateTime.UtcNow,
            StartedAt   = DateTime.UtcNow,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a strongly-typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">
    /// Target value type (e.g., <see langword="int"/>, <see langword="double"/>, <see langword="string"/>).
    /// Must be supported by <see cref="Convert.ChangeType(object, Type)"/>.
    /// </typeparam>
    /// <param name="ctx">EF Core DbContext used for the config lookup (read-side context is preferred).</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> value to look up (e.g., <c>"MLCusum:K"</c>).</param>
    /// <param name="defaultValue">
    /// Value returned when the key is missing from the table or the stored value cannot be parsed.
    /// This ensures the worker always has a safe operational default without requiring config entries
    /// to be seeded before the service can start.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed configuration value, or <paramref name="defaultValue"/> on any failure.</returns>
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
        catch { return defaultValue; } // Silently fall back; bad config should not crash the worker
    }
}
