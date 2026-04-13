using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors the production calibration quality of active <see cref="MLModel"/> instances
/// by computing a rolling Expected Calibration Error (ECE) over recently resolved
/// <see cref="MLModelPredictionLog"/> records and comparing it to the training-time ECE
/// stored in the model's <see cref="ModelSnapshot"/>.
///
/// Actions taken when calibration degrades beyond the threshold:
/// <list type="bullet">
///   <item>Creates a high-priority <see cref="Alert"/> for the ML ops team.</item>
///   <item>Queues a new <see cref="MLTrainingRun"/> to retrain and recalibrate the model,
///         unless one is already queued or running.</item>
/// </list>
///
/// ECE is computed with 10 equal-width confidence bins using the formula:
/// ECE = Σ_m (|Bm| / n) × |acc(Bm) − conf(Bm)|
/// where Bm is the set of predictions falling in bin m.
/// </summary>
public sealed class MLProductionCalibrationWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLCalibration:PollIntervalSeconds";
    private const string CK_WindowSize     = "MLCalibration:WindowSize";
    private const string CK_EceDeltaAlert  = "MLCalibration:EceDeltaToAlert";
    private const string CK_AlertDest      = "MLCalibration:AlertDestination";

    private const int CalibrationBins = 10;

    private readonly IServiceScopeFactory                       _scopeFactory;
    private readonly ILogger<MLProductionCalibrationWorker>     _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per polling cycle so that scoped EF Core contexts
    /// are correctly disposed after each pass.
    /// </param>
    /// <param name="logger">Structured logger for calibration events and drift warnings.</param>
    public MLProductionCalibrationWorker(
        IServiceScopeFactory                    scopeFactory,
        ILogger<MLProductionCalibrationWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Polls at the interval configured under
    /// <c>MLCalibration:PollIntervalSeconds</c> (default 3600 s / 1 h).
    /// Each cycle creates a fresh DI scope, resolves both the read and write DB contexts,
    /// and delegates to <see cref="CheckActiveModelsAsync"/>.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLProductionCalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval — overridden from EngineConfig each cycle so changes
            // in the config table take effect without restarting the service.
            int pollSecs = 3600;

            try
            {
                // A new async scope per cycle ensures scoped DbContexts are returned to
                // the pool promptly and avoids cross-cycle change-tracking contamination.
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Reload poll interval from database config each cycle.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckActiveModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit cleanly without logging as an error.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLProductionCalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLProductionCalibrationWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads global calibration configuration then iterates over every active,
    /// non-regime-scoped model, delegating to <see cref="CheckModelCalibrationAsync"/>
    /// for the ECE comparison.
    /// </summary>
    /// <param name="readCtx">Read-only EF Core context for loading models and prediction logs.</param>
    /// <param name="writeCtx">Write EF Core context for persisting alerts and training-run queues.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task CheckActiveModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Pull calibration parameters from the live EngineConfig table.
        // windowSize  — how many of the most-recent resolved logs to evaluate.
        // eceDelta    — how much the production ECE must exceed the training ECE before an alert is raised.
        // alertDest   — webhook / Slack destination tag for the degradation alert.
        int    windowSize  = await GetConfigAsync<int>   (readCtx, CK_WindowSize,    200,        ct);
        double eceDelta    = await GetConfigAsync<double>(readCtx, CK_EceDeltaAlert, 0.05,       ct);
        string alertDest   = await GetConfigAsync<string>(readCtx, CK_AlertDest,    "ml-ops",   ct);

        // Only evaluate base (non-regime-scoped) active models.
        // Regime-scoped models (RegimeScope != null) are managed by the regime-specific evaluator.
        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null && m.RegimeScope == null)
            .ToListAsync(ct);

        _logger.LogDebug("Production calibration: checking {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            await CheckModelCalibrationAsync(model, readCtx, writeCtx, windowSize, eceDelta, alertDest, ct);
        }
    }

    /// <summary>
    /// Computes the production ECE for a single model and, if drift exceeds the threshold,
    /// raises an alert and queues a new training run.
    ///
    /// <b>ECE reconstruction:</b> because the raw logit is not stored in prediction logs,
    /// the calibrated probability is approximated from <c>ConfidenceScore</c> and
    /// <c>PredictedDirection</c>:
    /// <list type="bullet">
    ///   <item>Buy:  calibP ≈ 0.5 + confidence / 2  → maps [0,1] confidence to [0.5, 1.0]</item>
    ///   <item>Sell: calibP ≈ 0.5 − confidence / 2  → maps [0,1] confidence to [0.0, 0.5]</item>
    /// </list>
    ///
    /// This approximation assumes the score was produced by a sigmoid whose midpoint is 0.5
    /// (the pre-Platt raw model output).
    /// </summary>
    /// <param name="model">The active model entity to evaluate.</param>
    /// <param name="readCtx">Read-only EF context.</param>
    /// <param name="writeCtx">Write EF context for alerts and training-run records.</param>
    /// <param name="windowSize">Maximum number of most-recent resolved logs to use.</param>
    /// <param name="eceDelta">
    /// Minimum production−training ECE gap that triggers an alert.
    /// A value of 0.05 means: alert when production ECE is more than 5 percentage points
    /// above the ECE recorded at training time.
    /// </param>
    /// <param name="alertDest">Alert destination tag (e.g. webhook group name or Slack channel).</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task CheckModelCalibrationAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        double                                  eceDelta,
        string                                  alertDest,
        CancellationToken                       ct)
    {
        // ── Load model snapshot to get training-time ECE ──────────────────────
        // The ModelSnapshot (serialised into ModelBytes at training time) stores the ECE
        // computed on the held-out test set, which serves as the calibration baseline.
        ModelSnapshot? snap = null;
        try
        {
            snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model {Id}: failed to deserialise snapshot — skipping calibration check", model.Id);
            return;
        }

        if (snap is null) return;

        // Baseline ECE measured on the training hold-out set — stored in the snapshot.
        double trainingEce = snap.Ece;
        double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

        // ── Load last N resolved prediction logs for this model ───────────────
        // Only logs where DirectionCorrect is known (i.e. the trade has settled)
        // and at least one probability representation is available are useful for
        // calibration measurement.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId        == model.Id &&
                        l.DirectionCorrect != null     &&
                        l.ActualDirection != null      &&
                        l.OutcomeRecordedAt != null    &&
                        (l.CalibratedProbability != null ||
                         l.RawProbability != null) &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.OutcomeRecordedAt)
            .ThenByDescending(l => l.Id)
            .Take(windowSize)
            .ToListAsync(ct);

        // Require at least 30 samples; fewer gives unreliable ECE estimates.
        if (logs.Count < 30)
        {
            _logger.LogDebug(
                "Model {Id}: only {N} resolved logs — need at least 30 for calibration check, skipping",
                model.Id, logs.Count);
            return;
        }

        // ── Compute production ECE ────────────────────────────────────────────
        // This worker monitors the base model's deployed calibration surface, so it uses
        // the logged base calibrated probability rather than the served stacked probability.
        // Binning strategy: 10 equal-width bins over [0, 1].
        // For each bin m:
        //   label(Bm) = empirical Buy frequency in bin m
        //   conf(Bm)  = mean predicted P(Buy) for predictions in bin m
        //   ECE       = Σ_m (|Bm| / n) × |label(Bm) − conf(Bm)|
        var binAcc  = new double[CalibrationBins]; // accumulates correct-prediction count per bin
        var binConf = new double[CalibrationBins]; // accumulates sum of calibP per bin
        var binN    = new int[CalibrationBins];    // count of predictions per bin

        foreach (var log in logs)
        {
            double calibP = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(log, decisionThreshold);
            double y = log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;

            // Assign to one of 10 equal-width bins based on the calibrated probability.
            int bin = Math.Clamp((int)(calibP * CalibrationBins), 0, CalibrationBins - 1);
            binN[bin]++;
            binConf[bin] += calibP;
            binAcc[bin] += y;
        }

        double prodEce = 0.0;
        int total = logs.Count;

        // Compute weighted ECE sum across all non-empty bins.
        for (int m = 0; m < CalibrationBins; m++)
        {
            if (binN[m] == 0) continue;
            double acc  = binAcc[m]  / binN[m];  // empirical accuracy for bin m
            double conf = binConf[m] / binN[m];  // mean predicted confidence for bin m
            // Weight each bin's calibration error by its fraction of total predictions.
            prodEce += (double)binN[m] / total * Math.Abs(acc - conf);
        }

        // Degradation = how much worse the production ECE is compared to training.
        // Positive value means the model is less calibrated in production than at training time.
        double eceDegradation = prodEce - trainingEce;

        _logger.LogInformation(
            "Calibration check model {Id} ({Symbol}/{Tf}): " +
            "trainingEce={TrainEce:F4} productionEce={ProdEce:F4} degradation={Delta:F4} " +
            "resolvedLogs={N} threshold={Thr:F4}",
            model.Id, model.Symbol, model.Timeframe,
            trainingEce, prodEce, eceDegradation, logs.Count, eceDelta);

        // No action needed when the degradation is within the acceptable tolerance band.
        if (eceDegradation <= eceDelta) return;

        // ── ECE degradation detected — alert + queue retrain ──────────────────
        // The model's calibration has drifted significantly beyond its training baseline.
        // Alert the ML ops team and schedule a retraining run to restore calibration.
        _logger.LogWarning(
            "CALIBRATION DRIFT: model {Id} ({Symbol}/{Tf}) prodEce={ProdEce:F4} vs " +
            "trainingEce={TrainEce:F4} (delta={Delta:F4} > threshold {Thr:F4}). " +
            "Queuing retraining run.",
            model.Id, model.Symbol, model.Timeframe,
            prodEce, trainingEce, eceDegradation, eceDelta);

        var now = DateTime.UtcNow;

        // Create a high-priority alert for the ML ops team with full calibration diagnostics.
        writeCtx.Set<Alert>().Add(new Alert
        {
            Symbol        = model.Symbol,
            AlertType     = AlertType.MLModelDegraded,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ModelId        = model.Id,
                Timeframe      = model.Timeframe.ToString(),
                ProductionEce  = prodEce,
                TrainingEce    = trainingEce,
                EceDegradation = eceDegradation,
                Threshold      = eceDelta,
                ResolvedLogs   = logs.Count,
            }),
            IsActive = true,
        });

        // Only queue a new training run if one is not already pending or running for this
        // symbol/timeframe combination — avoids duplicate retraining jobs.
        bool alreadyQueued = await writeCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (!alreadyQueued)
        {
            // Queue a full retrain covering the last 365 days of data.
            // TriggerType.AutoDegrading records why the retrain was initiated.
            writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol      = model.Symbol,
                Timeframe   = model.Timeframe,
                TriggerType = TriggerType.AutoDegrading,
                Status      = RunStatus.Queued,
                FromDate    = now.AddDays(-365),
                ToDate      = now,
                StartedAt   = now,
            });
        }

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from the <see cref="EngineConfig"/> table by key.
    /// Falls back to <paramref name="defaultValue"/> when the key is missing or
    /// the stored string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target CLR type (e.g. <c>int</c>, <c>double</c>, <c>string</c>).</typeparam>
    /// <param name="ctx">EF Core context to query.</param>
    /// <param name="key">Configuration key name stored in <c>EngineConfig.Key</c>.</param>
    /// <param name="defaultValue">Fallback value when the key is absent or unparseable.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
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

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
