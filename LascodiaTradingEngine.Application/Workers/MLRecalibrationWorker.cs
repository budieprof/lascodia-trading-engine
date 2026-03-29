using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Performs lightweight Platt + isotonic recalibration of active ML models when
/// Expected Calibration Error (ECE) rises above an acceptable threshold.
///
/// Unlike a full retrain, only the Platt (A, B) parameters and the isotonic
/// regression breakpoints are re-fitted on a rolling window of recently resolved
/// <see cref="MLModelPredictionLog"/> records whose outcomes are known.
/// The new parameters are patched directly into <see cref="MLModel.ModelBytes"/>
/// and the in-memory scorer cache is invalidated.
///
/// The recalibration is skipped when:
/// <list type="bullet">
///   <item>Fewer than <c>MLRecalibration:MinSamples</c> resolved predictions are available.</item>
///   <item>The current ECE is already below <c>MLRecalibration:MaxEce</c>.</item>
///   <item>A full retrain (<see cref="MLTrainingRun"/>) is already queued for the same symbol/timeframe.</item>
/// </list>
/// </summary>
public sealed class MLRecalibrationWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    private const string CK_PollSecs      = "MLRecalibration:PollIntervalSeconds";
    private const string CK_WindowDays    = "MLRecalibration:WindowDays";
    private const string CK_MinSamples    = "MLRecalibration:MinSamples";
    private const string CK_MaxEce        = "MLRecalibration:MaxEce";
    private const string CK_PlattLr       = "MLRecalibration:PlattLearningRate";
    private const string CK_PlattEpochs   = "MLRecalibration:PlattEpochs";

    // ── Cache key prefix (matches MLSignalScorer) ────────────────────────────
    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly IMemoryCache                      _cache;
    private readonly ILogger<MLRecalibrationWorker>    _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per polling cycle.</param>
    /// <param name="cache">
    /// Shared in-memory cache. Used to evict the <c>MLSnapshot:{modelId}</c> entry so the
    /// <c>MLSignalScorer</c> reloads the patched snapshot on its next scoring call.
    /// </param>
    /// <param name="logger">Structured logger.</param>
    public MLRecalibrationWorker(
        IServiceScopeFactory             scopeFactory,
        IMemoryCache                     cache,
        ILogger<MLRecalibrationWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Polls at the interval configured under
    /// <c>MLRecalibration:PollIntervalSeconds</c> (default 3600 s / 1 h).
    /// Each cycle creates a fresh DI scope and delegates to <see cref="RecalibrateActiveModelsAsync"/>.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRecalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval — refreshed from EngineConfig each cycle.
            int pollSecs = 3600;

            try
            {
                // Fresh scope per cycle prevents stale EF change-tracker state.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await RecalibrateActiveModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLRecalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRecalibrationWorker stopping.");
    }

    // ── Per-poll recalibration loop ───────────────────────────────────────────

    /// <summary>
    /// Loads global recalibration configuration then iterates over every active model,
    /// delegating to <see cref="RecalibrateModelAsync"/> for the actual calibration update.
    /// Errors for individual models are caught and logged so a single bad model cannot
    /// stall calibration of the remaining models.
    /// </summary>
    private async Task RecalibrateActiveModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Pull all recalibration hyperparameters from live EngineConfig.
        int windowDays  = await GetConfigAsync<int>(readCtx,    CK_WindowDays,  30,    ct);
        int minSamples  = await GetConfigAsync<int>(readCtx,    CK_MinSamples,  50,    ct);
        double maxEce   = await GetConfigAsync<double>(readCtx, CK_MaxEce,      0.07,  ct);
        double plattLr  = await GetConfigAsync<double>(readCtx, CK_PlattLr,     0.01,  ct);
        int    plattEpochs = await GetConfigAsync<int>(readCtx, CK_PlattEpochs, 200,   ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug("MLRecalibrationWorker: {N} active model(s) to check.", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await RecalibrateModelAsync(
                    model, readCtx, writeCtx,
                    windowDays, minSamples, maxEce, plattLr, plattEpochs, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Recalibration failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Recalibrates a single model's Platt (A, B) parameters and isotonic breakpoints
    /// by fitting them on recent resolved prediction logs and patching <c>ModelBytes</c>
    /// only when the new calibration demonstrably reduces ECE.
    ///
    /// <b>Algorithm:</b>
    /// <list type="number">
    ///   <item>Skip if a full retrain is already queued — the retrain will produce fresh calibration.</item>
    ///   <item>Load resolved logs from the last <paramref name="windowDays"/> days.</item>
    ///   <item>Compute the current ECE from stored confidence scores; skip if already ≤ <paramref name="maxEce"/>.</item>
    ///   <item>Build temporal decay weights: w_i = exp(−λ × days_ago), so more-recent outcomes
    ///         influence the gradient more strongly (λ = 0 gives uniform weights).</item>
    ///   <item>Run weighted SGD to find new Platt (A, B) parameters.</item>
    ///   <item>Run PAVA to fit new isotonic breakpoints using the post-Platt probabilities.</item>
    ///   <item>Accept the update only if the new ECE (computed under the new Platt parameters)
    ///         is strictly lower than the current ECE — prevents degradation from a bad fit.</item>
    ///   <item>Fit class-conditional Platt scalers (Buy subset / Sell subset) to correct
    ///         direction-specific probability bias.</item>
    ///   <item>Persist patched <c>ModelBytes</c> and evict the scorer cache entry.</item>
    /// </list>
    /// </summary>
    /// <param name="model">Active model entity to recalibrate.</param>
    /// <param name="readCtx">Read-only EF context.</param>
    /// <param name="writeCtx">Write EF context for persisting the patched snapshot.</param>
    /// <param name="windowDays">How many days of resolved logs to include in the fit.</param>
    /// <param name="minSamples">Minimum resolved-log count required to proceed.</param>
    /// <param name="maxEce">ECE ceiling; skip if current ECE is already below this value.</param>
    /// <param name="plattLr">SGD learning rate for Platt parameter fitting.</param>
    /// <param name="plattEpochs">Number of SGD epochs for Platt fitting.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task RecalibrateModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minSamples,
        double                                  maxEce,
        double                                  plattLr,
        int                                     plattEpochs,
        CancellationToken                       ct)
    {
        // Skip if a full retrain is already queued for this symbol/timeframe.
        // The retrain will produce a fresh calibration from scratch, making this lightweight
        // recalibration redundant and potentially wasteful.
        bool retrainQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol == model.Symbol && r.Timeframe == model.Timeframe
                           && r.Status == RunStatus.Queued, ct);
        if (retrainQueued)
        {
            _logger.LogDebug(
                "Recalibration skipped for {Symbol}/{Tf} — full retrain queued.",
                model.Symbol, model.Timeframe);
            return;
        }

        // Load resolved prediction logs within the recalibration window.
        // Logs must have DirectionCorrect set (the trade outcome is known).
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var logs  = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId           == model.Id &&
                        l.ActualDirection.HasValue        &&
                        l.DirectionCorrect.HasValue       &&
                        l.OutcomeRecordedAt != null       &&
                        l.OutcomeRecordedAt >= since      &&
                        !l.IsDeleted)
            .OrderBy(l => l.OutcomeRecordedAt)
            .ThenBy(l => l.Id)
            .AsNoTracking()
            .ToListAsync(ct);

        if (logs.Count < minSamples)
        {
            _logger.LogDebug(
                "Recalibration skipped for {Symbol}/{Tf}: only {N} resolved predictions (need {Min}).",
                model.Symbol, model.Timeframe, logs.Count, minSamples);
            return;
        }

        var (writeModel, snap) = await MLModelSnapshotWriteHelper
            .LoadTrackedLatestSnapshotAsync(writeCtx, model.Id, ct);
        if (writeModel == null || snap == null)
        {
            _logger.LogWarning(
                "Recalibration: failed to load the latest snapshot for model {Id} — skipping.",
                model.Id);
            return;
        }

        double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

        // Compute current ECE from prediction logs (using stored predicted probability).
        // This is the baseline to beat; if already acceptable, skip the recalibration work.
        double currentEce = ComputeEceFromLogs(logs, decisionThreshold);
        _logger.LogDebug(
            "Model {Symbol}/{Tf} id={Id}: ECE={Ece:F4} (max={Max:F4}, samples={N})",
            model.Symbol, model.Timeframe, model.Id, currentEce, maxEce, logs.Count);

        if (currentEce <= maxEce)
        {
            _logger.LogDebug(
                "Recalibration not needed for {Symbol}/{Tf} — ECE {Ece:F4} ≤ {Max:F4}.",
                model.Symbol, model.Timeframe, currentEce, maxEce);
            return;
        }

        // Build per-log temporal decay weights: w_i = exp(−λ × days_since_prediction).
        // λ=0 → uniform weights; λ>0 → exponential decay giving more weight to recent logs.
        // This means the Platt parameters will be pulled more strongly by recent market conditions.
        double recalDecayLambda = await GetConfigAsync<double>(readCtx, "MLRecalibration:DecayLambda", 0.0, ct);
        double[] logWeights = BuildTemporalWeights(logs, recalDecayLambda);

        // Refit Platt scaling (A, B) using temporally-weighted SGD on the resolved prediction logs.
        // Platt scaling maps a raw logit f to a calibrated probability: σ(A·f + B)
        // where σ is the sigmoid function. A=1, B=0 is the identity (no change).
        bool useTemperature = snap.TemperatureScale > 0.0 && snap.TemperatureScale < 10.0;
        var (newPlattA, newPlattB) = useTemperature
            ? (snap.PlattA, snap.PlattB)
            : RefitPlattFromLogs(logs, logWeights, decisionThreshold, plattLr, plattEpochs);

        // Refit class-conditional Platt (separate scalers for Buy and Sell).
        // Buy-predicted logs (confidence ≥ 0.5) and Sell-predicted logs (< 0.5) may have
        // systematically different calibration errors; fitting separate (A, B) pairs corrects this.
        var (newABuy, newBBuy, newASell, newBSell) = RefitClassConditionalPlatt(
            logs, decisionThreshold, snap.TemperatureScale, newPlattA, newPlattB, plattLr, plattEpochs);

        // Refit isotonic calibration breakpoints on the same pre-isotonic calibration path
        // that production will actually use after this update.
        var newIsotonicBp = RefitIsotonicFromLogs(
            logs,
            decisionThreshold,
            snap.TemperatureScale,
            newPlattA,
            newPlattB,
            newABuy,
            newBBuy,
            newASell,
            newBSell,
            snap.AgeDecayLambda,
            snap.TrainedAtUtc);

        // Verify that the full patched calibration stack actually improves ECE before committing.
        double newEce = ComputeEceFromLogsWithCalibration(
            logs,
            decisionThreshold,
            snap.TemperatureScale,
            newPlattA,
            newPlattB,
            newABuy,
            newBBuy,
            newASell,
            newBSell,
            newIsotonicBp,
            snap.AgeDecayLambda,
            snap.TrainedAtUtc);
        if (newEce >= currentEce)
        {
            _logger.LogDebug(
                "Recalibration for {Symbol}/{Tf}: new ECE {New:F4} not better than {Old:F4} — skipping update.",
                model.Symbol, model.Timeframe, newEce, currentEce);
            return;
        }

        // Patch snapshot with new calibration parameters.
        snap.PlattA              = newPlattA;
        snap.PlattB              = newPlattB;
        snap.IsotonicBreakpoints = newIsotonicBp;
        snap.Ece                 = newEce;
        // Only update class-conditional Platt if the fitting produced non-zero parameters
        // (FitSgd returns (0, 0) when a class has fewer than 5 samples).
        if (newABuy  != 0.0) { snap.PlattABuy  = newABuy;  snap.PlattBBuy  = newBBuy;  }
        if (newASell != 0.0) { snap.PlattASell = newASell; snap.PlattBSell = newBSell; }

        writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);
        await writeCtx.SaveChangesAsync(ct);

        // Evict the scorer's 30-min cache entry so MLSignalScorer picks up the new parameters
        // on its very next scoring call, rather than continuing to use the stale snapshot.
        _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");

        _logger.LogInformation(
            "Recalibration applied for {Symbol}/{Tf} model {Id}: " +
            "ECE {Old:F4} → {New:F4}, PlattA={A:F4} B={B:F4}, {N} resolved predictions.",
            model.Symbol, model.Timeframe, model.Id,
            currentEce, newEce, newPlattA, newPlattB, logs.Count);
    }

    // ── Calibration helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Computes Expected Calibration Error (ECE) directly from logged Buy-class probabilities
    /// using 10 equal-width bins.
    ///
    /// For calibration, each bin compares mean predicted <c>P(Buy)</c> against the empirical
    /// Buy frequency in that bin rather than thresholded direction correctness.
    ///
    /// ECE = Σ_m (|B_m| / n) × |mean(y in B_m) − mean(p in B_m)|
    /// </summary>
    /// <param name="logs">Resolved prediction logs with non-null <c>DirectionCorrect</c>.</param>
    /// <returns>ECE in [0, 1]. Lower is better calibrated.</returns>
    private static double ComputeEceFromLogs(
        IReadOnlyList<MLModelPredictionLog> logs,
        double decisionThreshold)
    {
        if (logs.Count == 0) return 0.0;
        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binLabelSum = new double[NumBins];
        var binCount   = new int[NumBins];

        foreach (var log in logs)
        {
            double p = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(log, decisionThreshold);
            int    bin = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);
            binConfSum[bin] += p;
            double y = log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            binLabelSum[bin] += y;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = logs.Count;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double avgLabel = binLabelSum[b] / binCount[b];
            ece += Math.Abs(avgLabel - avgConf) * binCount[b] / n;
        }
        return ece;
    }

    private static double ComputeEceFromLogsWithCalibration(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              decisionThreshold,
        double                              temperatureScale,
        double                              plattA,
        double                              plattB,
        double                              plattABuy,
        double                              plattBBuy,
        double                              plattASell,
        double                              plattBSell,
        double[]                            isotonicBreakpoints,
        double                              ageDecayLambda,
        DateTime                            trainedAtUtc)
    {
        if (logs.Count == 0) return 0.0;
        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binLabelSum = new double[NumBins];
        var binCount   = new int[NumBins];

        foreach (var log in logs)
        {
            double rawP = MLFeatureHelper.ResolveLoggedRawBuyProbability(log, decisionThreshold);
            double calibP = ApplyCalibrationWithoutAgeDecay(
                rawP,
                temperatureScale,
                plattA,
                plattB,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                isotonicBreakpoints,
                ageDecayLambda,
                trainedAtUtc);
            int    bin     = Math.Clamp((int)(calibP * NumBins), 0, NumBins - 1);
            binConfSum[bin] += calibP;
            double y = log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            binLabelSum[bin] += y;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = logs.Count;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double avgLabel = binLabelSum[b] / binCount[b];
            ece += Math.Abs(avgLabel - avgConf) * binCount[b] / n;
        }
        return ece;
    }

    /// <summary>
    /// Builds per-log temporal decay weights: w_i = exp(−λ × days_since_prediction).
    /// Normalised to sum to logs.Count. When λ = 0, returns uniform weights (all = 1.0).
    ///
    /// The normalisation ensures the effective gradient magnitude in SGD is the same
    /// regardless of λ, so the learning rate does not need to be adjusted when enabling decay.
    /// </summary>
    /// <param name="logs">Prediction logs ordered by time (oldest to newest).</param>
    /// <param name="lambda">
    /// Exponential decay rate in units of 1/days.
    /// λ = 0   → uniform weights (no temporal preference).
    /// λ = 0.1 → weight halves every ≈ 7 days.
    /// </param>
    /// <returns>Array of non-negative weights, one per log, normalised to sum to <c>logs.Count</c>.</returns>
    private static double[] BuildTemporalWeights(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              lambda)
    {
        int    n       = logs.Count;
        var    weights = new double[n];
        var    now     = DateTime.UtcNow;

        if (lambda <= 0.0)
        {
            // Uniform weights — all samples equally influential.
            for (int i = 0; i < n; i++) weights[i] = 1.0;
            return weights;
        }

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            DateTime resolvedAt = logs[i].OutcomeRecordedAt ?? logs[i].PredictedAt;
            double days = (now - resolvedAt).TotalDays;
            // Exponential decay: older samples receive geometrically smaller weights.
            weights[i]  = Math.Exp(-lambda * days);
            sum        += weights[i];
        }
        // Normalise so that the effective gradient scale equals the unweighted scale.
        // Without this, a high-λ config would produce tiny gradients and slow convergence.
        if (sum > 1e-15)
            for (int i = 0; i < n; i++) weights[i] = weights[i] / sum * n;
        return weights;
    }

    /// <summary>
    /// Fits Platt scaling parameters (A, B) via weighted mini-batch SGD (full-batch gradient
    /// descent over the provided logs, repeated for <paramref name="epochs"/> iterations).
    ///
    /// Platt scaling model:  calibP = σ(A × logit(rawP) + B)
    /// Loss:                 binary cross-entropy L = −[y log(calibP) + (1−y) log(1−calibP)]
    /// Gradients:            ∂L/∂A = (calibP − y) × logit(rawP)
    ///                       ∂L/∂B = (calibP − y)
    /// Update rule:          A ← A − lr × Σ(w_i × ∂L_i/∂A) / Σ(w_i)
    ///                       B ← B − lr × Σ(w_i × ∂L_i/∂B) / Σ(w_i)
    ///
    /// Initialised at A=1, B=0 (identity — no calibration change from the start).
    /// </summary>
    /// <param name="logs">Resolved prediction logs to fit on.</param>
    /// <param name="sampleWeights">Per-sample weights (from <see cref="BuildTemporalWeights"/>).</param>
    /// <param name="lr">SGD learning rate.</param>
    /// <param name="epochs">Number of full-pass gradient descent iterations.</param>
    /// <returns>Fitted (A, B) Platt parameters.</returns>
    private static (double A, double B) RefitPlattFromLogs(
        IReadOnlyList<MLModelPredictionLog> logs,
        double[]                            sampleWeights,
        double                              decisionThreshold,
        double                              lr,
        int                                 epochs)
    {
        // Initialise at identity: A=1, B=0 means calibP = σ(logit(rawP)) = rawP (no change).
        double plattA = 1.0, plattB = 0.0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0, dB = 0, wSum = 0;
            for (int i = 0; i < logs.Count; i++)
            {
                var    log    = logs[i];
                double w      = sampleWeights[i];
                double rawP = MLFeatureHelper.ResolveLoggedRawBuyProbability(log, decisionThreshold);
                double logit  = MLFeatureHelper.Logit(rawP);
                double calibP = MLFeatureHelper.Sigmoid(plattA * logit + plattB);
                double y = log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
                // Gradient of cross-entropy loss with respect to A and B.
                double err    = (calibP - y) * w;
                dA   += err * logit;
                dB   += err;
                wSum += w;
            }
            if (wSum < 1e-15) break;
            // Weighted average gradient update step.
            plattA -= lr * dA / wSum;
            plattB -= lr * dB / wSum;
        }

        return (plattA, plattB);
    }

    /// <summary>
    /// Refits isotonic regression calibration breakpoints using the Pool Adjacent Violators
    /// Algorithm (PAVA) applied to Platt-transformed confidence scores.
    ///
    /// Isotonic regression finds the closest non-decreasing step function to the empirical
    /// accuracy curve. The result is a monotone mapping from predicted probability → true
    /// empirical accuracy that can be stored as interleaved [x₀,y₀, x₁,y₁, …] breakpoints.
    ///
    /// PAVA algorithm:
    /// For each new point (P_i, y_i), push it onto the stack.
    /// While the new block's mean y is less than the previous block's mean y, merge them
    /// (pool the violating pair into a single block whose y = pooled mean).
    /// This enforces non-decreasingness across all blocks.
    /// </summary>
    /// <param name="logs">Resolved prediction logs.</param>
    /// <param name="plattA">Platt A parameter for pre-transforming confidence scores.</param>
    /// <param name="plattB">Platt B parameter for pre-transforming confidence scores.</param>
    /// <returns>
    /// Interleaved [x, y] breakpoints: x = mean calibP in the PAVA block, y = mean label.
    /// Returns an empty array when fewer than 10 samples are available.
    /// </returns>
    private static double[] RefitIsotonicFromLogs(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              decisionThreshold,
        double                              temperatureScale,
        double                              plattA,
        double                              plattB,
        double                              plattABuy,
        double                              plattBBuy,
        double                              plattASell,
        double                              plattBSell,
        double                              ageDecayLambda,
        DateTime                            trainedAtUtc)
    {
        if (logs.Count < 10) return [];

        // Apply Platt transform and pair with binary labels, sorted by ascending calibP.
        var pairs = logs
            .Select(l =>
            {
                double rawP = MLFeatureHelper.ResolveLoggedRawBuyProbability(l, decisionThreshold);
                double calibP = ApplyCalibrationWithoutAgeDecay(
                    rawP,
                    temperatureScale,
                    plattA,
                    plattB,
                    plattABuy,
                    plattBBuy,
                    plattASell,
                    plattBSell,
                    [],
                    ageDecayLambda,
                    trainedAtUtc);
                return (P: calibP, Y: l.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0);
            })
            .OrderBy(x => x.P)
            .ToList();

        // Pool Adjacent Violators Algorithm (PAVA).
        // Each stack entry is a "block" of merged samples: (sumY, sumP, count).
        // Block mean y = sumY / count; block mean P = sumP / count.
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Count);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            // Merge while the previous block's average accuracy > current block's average accuracy.
            // This enforces the non-decreasing constraint of isotonic regression.
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var prev = stack[^2];
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    // Violation: pool the two blocks into one with combined statistics.
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY,
                                 prev.SumP + last.SumP,
                                 prev.Count + last.Count);
                }
                else break;
            }
        }

        // Emit interleaved [x, y] breakpoint pairs: x = mean calibP, y = mean empirical accuracy.
        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count; // x: mean probability in block
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count; // y: mean accuracy in block
        }
        return bp;
    }

    // ── Class-conditional Platt ───────────────────────────────────────────────

    /// <summary>
    /// Refits separate Platt scalers for the Buy-predicted subset (ConfidenceScore ≥ 0.5)
    /// and the Sell-predicted subset (ConfidenceScore &lt; 0.5) of the resolved prediction logs.
    /// Returns (0,0,0,0) when either class subset has fewer than 5 samples.
    ///
    /// Motivation: a model that is well-calibrated overall may still be systematically
    /// over-confident on Buy signals or under-confident on Sell signals. Class-conditional
    /// Platt scalers correct this asymmetric bias by fitting independent (A, B) pairs for
    /// each predicted class, ensuring that P(correct | Buy prediction) and
    /// P(correct | Sell prediction) are both accurately reflected in the output probability.
    /// </summary>
    /// <param name="logs">Resolved prediction logs to split and fit on.</param>
    /// <param name="lr">SGD learning rate for the per-class SGD fits.</param>
    /// <param name="epochs">Number of SGD epochs per class.</param>
    /// <returns>
    /// (ABuy, BBuy, ASell, BSell) — Platt (A, B) pairs for each class.
    /// Returns (0, 0, 0, 0) if either class has fewer than 5 samples.
    /// </returns>
    private static (double ABuy, double BBuy, double ASell, double BSell)
        RefitClassConditionalPlatt(
            IReadOnlyList<MLModelPredictionLog> logs,
            double                              decisionThreshold,
            double                              temperatureScale,
            double                              globalPlattA,
            double                              globalPlattB,
            double                              lr,
            int                                 epochs)
    {
        var buyPairs  = new List<(double Logit, double Y)>();
        var sellPairs = new List<(double Logit, double Y)>();

        // Partition by the same global calibration branch that live inference uses before
        // applying the class-conditional scaler: temperature/global Platt, then >= 0.5.
        foreach (var log in logs)
        {
            double rawP = MLFeatureHelper.ResolveLoggedRawBuyProbability(log, decisionThreshold);
            double logit = MLFeatureHelper.Logit(rawP);
            double y     = log.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            double globalCalibP = temperatureScale > 0.0 && temperatureScale < 10.0
                ? MLFeatureHelper.Sigmoid(logit / temperatureScale)
                : MLFeatureHelper.Sigmoid(globalPlattA * logit + globalPlattB);

            if (globalCalibP >= 0.5) buyPairs.Add((logit, y));
            else                     sellPairs.Add((logit, y));
        }

        // Local SGD fitter: standard Platt mini-batch SGD for a single (logit, label) dataset.
        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs, double lr, int epochs)
        {
            // Return (0, 0) sentinel when not enough samples for a reliable fit.
            if (pairs.Count < 5) return (0.0, 0.0);
            double a = 1.0, b = 0.0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0, dB = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err    = calibP - y;
                    dA += err * logit;
                    dB += err;
                }
                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;
            }
            return (a, b);
        }

        var (aBuy,  bBuy)  = FitSgd(buyPairs,  lr, epochs);
        var (aSell, bSell) = FitSgd(sellPairs, lr, epochs);
        return (aBuy, bBuy, aSell, bSell);
    }

    private static double ApplyCalibrationWithoutAgeDecay(
        double   rawP,
        double   temperatureScale,
        double   plattA,
        double   plattB,
        double   plattABuy,
        double   plattBBuy,
        double   plattASell,
        double   plattBSell,
        double[] isotonicBreakpoints,
        double   ageDecayLambda,
        DateTime trainedAtUtc)
    {
        double rawLogit = MLFeatureHelper.Logit(rawP);
        double globalCalibP = temperatureScale > 0.0 && temperatureScale < 10.0
            ? MLFeatureHelper.Sigmoid(rawLogit / temperatureScale)
            : MLFeatureHelper.Sigmoid(plattA * rawLogit + plattB);

        double calibP;
        if (globalCalibP >= 0.5 && plattABuy != 0.0)
            calibP = MLFeatureHelper.Sigmoid(plattABuy * rawLogit + plattBBuy);
        else if (globalCalibP < 0.5 && plattASell != 0.0)
            calibP = MLFeatureHelper.Sigmoid(plattASell * rawLogit + plattBSell);
        else
            calibP = globalCalibP;

        if (isotonicBreakpoints.Length >= 4)
            calibP = BaggedLogisticTrainer.ApplyIsotonicCalibration(calibP, isotonicBreakpoints);

        if (ageDecayLambda > 0.0 && trainedAtUtc != default)
        {
            double daysSinceTrain = (DateTime.UtcNow - trainedAtUtc).TotalDays;
            double decayFactor = Math.Exp(-ageDecayLambda * Math.Max(0.0, daysSinceTrain));
            calibP = 0.5 + (calibP - 0.5) * decayFactor;
        }

        return calibP;
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from the <see cref="EngineConfig"/> table by key.
    /// Falls back to <paramref name="defaultValue"/> when the key is missing or unparseable.
    /// </summary>
    /// <typeparam name="T">Target CLR type.</typeparam>
    /// <param name="ctx">EF Core context to query.</param>
    /// <param name="key">Configuration key stored in <c>EngineConfig.Key</c>.</param>
    /// <param name="defaultValue">Fallback value.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
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
