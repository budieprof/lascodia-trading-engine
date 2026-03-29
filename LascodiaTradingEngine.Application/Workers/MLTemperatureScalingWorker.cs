using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Applies temperature scaling to improve the calibration of active ML model outputs (Rec #362).
///
/// <b>Temperature scaling</b> is a post-hoc single-parameter calibration method introduced by
/// Guo et al. (2017) "On Calibration of Modern Neural Networks". It divides every model logit
/// by a scalar temperature T before applying the sigmoid:
///
///   calibP = σ(logit / T)
///
/// When T &gt; 1 the output probabilities are pushed toward 0.5 (the model becomes less confident),
/// correcting over-confidence. When T &lt; 1 the probabilities are pushed toward the extremes,
/// correcting under-confidence. T = 1 leaves the outputs unchanged (identity).
///
/// The optimal temperature is found by minimising the negative log-likelihood (NLL) on recent
/// resolved prediction logs using a 20-iteration binary search over T ∈ [0.1, 10.0].
///
/// The worker persists the optimal temperature back into the serialised
/// <see cref="ModelSnapshot"/> stored in <see cref="MLModel.ModelBytes"/> so live inference
/// uses the recalibrated temperature immediately. It also upserts a
/// <see cref="MLTemperatureScalingLog"/> record for diagnostics.
///
/// Runs every 3 days. Requires at least 20 resolved prediction logs per model.
/// Meta-learner and MAML-initializer models are excluded.
/// </summary>
public sealed class MLTemperatureScalingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MLTemperatureScalingWorker> _logger;
    private const string SnapshotCacheKeyPrefix = "MLSnapshot:";

    // Maximum number of recent resolved prediction logs to use per model.
    private const int    MaxSamples   = 200;

    // Number of binary-search iterations for finding the optimal temperature.
    // 20 iterations gives precision of (10.0 - 0.1) / 2^20 ≈ 9.4e-6.
    private const int    BsIterations = 20;

    // Number of equal-width bins for ECE computation.
    private const int    EceBins      = 10;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per run cycle.</param>
    /// <param name="cache">Shared snapshot cache used by the live scorer.</param>
    /// <param name="logger">Structured logger for temperature scaling results.</param>
    public MLTemperatureScalingWorker(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<MLTemperatureScalingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Runs every 3 days, delegating to <see cref="RunAsync"/>.
    /// The 3-day interval is intentionally long — temperature scaling is computationally cheap
    /// but the underlying data changes slowly enough that daily updates add little value.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTemperatureScalingWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLTemperatureScalingWorker error"); }
            await Task.Delay(TimeSpan.FromDays(3), stoppingToken);
        }
    }

    /// <summary>
    /// Core temperature-scaling routine. For each eligible active model:
    /// <list type="number">
    ///   <item>Loads the last <see cref="MaxSamples"/> resolved prediction logs.</item>
    ///   <item>Converts stored ConfidenceScores to logits: logit = log(p / (1 − p)).</item>
    ///   <item>Measures pre-calibration NLL and ECE at T = 1 (baseline).</item>
    ///   <item>Runs a 20-iteration binary search to find the optimal temperature T*
    ///         that minimises NLL:
    ///         <list type="bullet">
    ///           <item>At each iteration, evaluates NLL at the midpoint T_mid and T_mid + 0.01.</item>
    ///           <item>If NLL(T_mid) &gt; NLL(T_mid + 0.01), the gradient points upward → shift tLow up.</item>
    ///           <item>Otherwise shift tHigh down. This is a numerical first-order gradient check.</item>
    ///         </list>
    ///   </item>
    ///   <item>Computes post-calibration NLL and ECE using T*: calibP = σ(logit / T*).</item>
    ///   <item>Writes the optimal temperature back into <c>ModelBytes</c> and upserts a
    ///         <see cref="MLTemperatureScalingLog"/> record with diagnostics.</item>
    /// </list>
    /// </summary>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Only evaluate base models that produce direct trade signals.
        var models = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            ModelSnapshot? snap;
            try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
            catch { continue; }

            if (snap is null) continue;

            double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

            // Load the most recent resolved prediction logs for this model.
            var logs = await readDb.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(p => p.MLModelId == model.Id &&
                            !p.IsDeleted &&
                            p.DirectionCorrect != null &&
                            p.ActualDirection != null)
                .OrderByDescending(p => p.PredictedAt)
                .Take(MaxSamples)
                .ToListAsync(ct);

            // Require at least 20 logs for a meaningful temperature search.
            if (logs.Count < 20) continue;

            // Reconstruct calibrated buy-probabilities from the logged scoring outputs,
            // then convert those probabilities to logits for temperature scaling.
            double[] confs = logs.Select(p =>
                    Math.Clamp(
                        MLFeatureHelper.ResolveLoggedRawBuyProbability(p, decisionThreshold),
                        1e-7, 1 - 1e-7))
                .ToArray();
            double[] logits = confs.Select(c => Math.Log(c / (1.0 - c))).ToArray();
            double[] labels = logs.Select(p => p.ActualDirection == Domain.Enums.TradeDirection.Buy ? 1.0 : 0.0).ToArray();

            // Pre-calibration NLL and ECE at T = 1 (no temperature scaling — raw model outputs).
            double preNll = ComputeNll(logits, labels, 1.0);
            double preEce = ComputeEce(confs, labels);

            // Binary search for optimal temperature T* ∈ [0.1, 10.0] that minimises NLL.
            // The search uses a numerical gradient check: compare NLL at tMid vs tMid + 0.01.
            double tLow = 0.1, tHigh = 10.0;
            double optT = 1.0;
            for (int iter = 0; iter < BsIterations; iter++)
            {
                double tMid     = (tLow + tHigh) / 2.0;
                double tMidPlus = tMid + 0.01;      // small step for finite-difference gradient

                double nllMid     = ComputeNll(logits, labels, tMid);
                double nllMidPlus = ComputeNll(logits, labels, tMidPlus);

                // Gradient direction: if NLL decreases going higher, move tLow up (need larger T).
                // If NLL increases going higher, move tHigh down (need smaller T).
                if (nllMid > nllMidPlus)
                    tLow = tMid;
                else
                    tHigh = tMid;

                optT = (tLow + tHigh) / 2.0;
            }

            // Post-calibration NLL and ECE at optimal temperature T*.
            // calibP = σ(logit / T*) — softer probabilities when T* > 1.
            double postNll   = ComputeNll(logits, labels, optT);
            double[] calConf = logits.Select(lg => Sigmoid(lg / optT)).ToArray();
            double postEce   = ComputeEce(calConf, labels);

            var (writeModel, latestSnap) = await MLModelSnapshotWriteHelper
                .LoadTrackedLatestSnapshotAsync(writeDb, model.Id, ct);
            if (writeModel != null && latestSnap != null)
            {
                latestSnap.TemperatureScale = optT;
                writeModel.ModelBytes = JsonSerializer.SerializeToUtf8Bytes(latestSnap);
                _cache.Remove($"{SnapshotCacheKeyPrefix}{model.Id}");
            }

            // Upsert the temperature scaling log: one record per model, updated in place.
            var existing = await writeDb.Set<MLTemperatureScalingLog>()
                .FirstOrDefaultAsync(x => x.MLModelId == model.Id && !x.IsDeleted, ct);

            if (existing == null)
            {
                // First-time calibration for this model — create a new record.
                writeDb.Set<MLTemperatureScalingLog>().Add(new MLTemperatureScalingLog
                {
                    MLModelId            = model.Id,
                    Symbol               = model.Symbol,
                    Timeframe            = model.Timeframe.ToString(),
                    OptimalTemperature   = optT,
                    PreCalibrationEce    = preEce,
                    PostCalibrationEce   = postEce,
                    PreCalibrationNll    = preNll,
                    PostCalibrationNll   = postNll,
                    CalibrationSamples   = logs.Count,
                    ComputedAt           = DateTime.UtcNow
                });
            }
            else
            {
                // Update the existing record with the latest calibration results.
                existing.OptimalTemperature = optT;
                existing.PreCalibrationEce  = preEce;
                existing.PostCalibrationEce = postEce;
                existing.PreCalibrationNll  = preNll;
                existing.PostCalibrationNll = postNll;
                existing.CalibrationSamples = logs.Count;
                existing.ComputedAt         = DateTime.UtcNow;
            }

            _logger.LogInformation(
                "MLTemperatureScalingWorker: {S}/{T} optT={T2:F3}, ECE: {Pre:F4}→{Post:F4}",
                model.Symbol, model.Timeframe, optT, preEce, postEce);
        }

        await writeDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Computes the mean binary cross-entropy (negative log-likelihood) for a set of logits
    /// scaled by the given temperature.
    ///
    /// Formula per sample:  NLL_i = −[y_i log σ(logit_i / T) + (1 − y_i) log(1 − σ(logit_i / T))]
    /// Mean NLL             = (1/n) Σ NLL_i
    ///
    /// Lower NLL = better calibrated outputs (the model assigns high probability to correct outcomes).
    /// This is the objective minimised during the binary search for optimal T.
    /// </summary>
    /// <param name="logits">Raw log-odds (logit) values for each prediction.</param>
    /// <param name="labels">Binary labels: 1.0 = correct, 0.0 = incorrect.</param>
    /// <param name="temperature">Temperature scalar T to divide logits by before sigmoid.</param>
    /// <returns>Mean NLL over all samples.</returns>
    private static double ComputeNll(double[] logits, double[] labels, double temperature)
    {
        double nll = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            // Apply temperature scaling: calibP = σ(logit / T)
            double p = Sigmoid(logits[i] / temperature);
            // Clamp to avoid log(0) in the NLL computation.
            p = Math.Clamp(p, 1e-7, 1 - 1e-7);
            // Binary cross-entropy contribution for sample i.
            nll += -(labels[i] * Math.Log(p) + (1 - labels[i]) * Math.Log(1 - p));
        }
        return nll / logits.Length; // mean NLL
    }

    /// <summary>
    /// Computes the Expected Calibration Error (ECE) using <see cref="EceBins"/> equal-width bins.
    ///
    /// ECE = Σ_b (|B_b| / n) × |meanConf(B_b) − meanAcc(B_b)|
    ///
    /// where B_b is the set of predictions whose confidence falls in the b-th bin.
    /// A well-calibrated model has ECE ≈ 0: the mean predicted confidence in each bin
    /// matches the empirical accuracy of predictions in that bin.
    /// </summary>
    /// <param name="confs">Calibrated confidence values in [0, 1].</param>
    /// <param name="labels">Binary outcome labels: 1.0 = correct, 0.0 = incorrect.</param>
    /// <returns>ECE in [0, 1]. Lower is better calibrated.</returns>
    private static double ComputeEce(double[] confs, double[] labels)
    {
        double ece = 0;
        int n = confs.Length;
        for (int b = 0; b < EceBins; b++)
        {
            // Each bin covers the equal-width interval [lo, hi) over [0, 1].
            double lo = (double)b / EceBins;
            double hi = (double)(b + 1) / EceBins;
            var inBin = Enumerable.Range(0, n)
                .Where(i => confs[i] >= lo && confs[i] < hi)
                .ToList();
            if (inBin.Count == 0) continue;
            double meanConf = inBin.Average(i => confs[i]); // mean predicted confidence in bin
            double meanAcc  = inBin.Average(i => labels[i]); // empirical accuracy in bin
            // Weight this bin's calibration gap by its proportion of the total predictions.
            ece += (double)inBin.Count / n * Math.Abs(meanConf - meanAcc);
        }
        return ece;
    }

    /// <summary>
    /// Standard sigmoid (logistic) function: σ(x) = 1 / (1 + e^{−x}).
    /// Maps any real-valued logit to a probability in (0, 1).
    /// Used for applying temperature scaling: calibP = σ(logit / T).
    /// </summary>
    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));
}
