using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes split conformal prediction calibration for each newly activated ML model,
/// producing statistically guaranteed coverage sets at inference time (Rec #16).
/// </summary>
/// <remarks>
/// Uses the hold-out calibration split (10 % of training data) that was already
/// separated during training.  For each calibration sample, the nonconformity score is:
///   α_i = 1 − ŷ_{y_i}  (1 minus the predicted probability of the true label)
/// The coverage threshold τ at level 1-α is the ⌈(n+1)(1-α)/n⌉-th quantile.
/// At inference: if (1 − ŷ_Buy) ≤ τ the prediction set includes Buy, similarly for Sell.
/// When both are included → "Ambiguous".
/// </remarks>
public sealed class MLConformalCalibrationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLConformalCalibrationWorker> _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per polling cycle.</param>
    /// <param name="logger">Structured logger for conformal calibration events.</param>
    public MLConformalCalibrationWorker(IServiceScopeFactory scopeFactory, ILogger<MLConformalCalibrationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Polls every 30 minutes, checking for newly active models
    /// that do not yet have a <see cref="MLConformalCalibration"/> record and computing
    /// one for each. Models that already have a calibration record are skipped — recalibration
    /// of existing records is handled by <see cref="MLConformalRecalibrationWorker"/>.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLConformalCalibrationWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLConformalCalibrationWorker error"); }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    /// <summary>
    /// Core calibration routine. For each active base model without a conformal record:
    /// <list type="number">
    ///   <item>Loads up to 500 recent resolved prediction logs as the calibration set.</item>
    ///   <item>Computes a nonconformity score for each log:
    ///         <c>α_i = 1 − P(true class)</c>, i.e. 1 minus the predicted probability that
    ///         the model assigned to the label that actually occurred.</item>
    ///   <item>Sorts scores ascending and takes the empirical quantile at level
    ///         <c>⌈(n+1)(1−α)/n⌉</c> as the coverage threshold τ (at 90 % nominal coverage).</item>
    ///   <item>Computes empirical coverage on the same calibration set (an upper bound on
    ///         true coverage — the test-set coverage guarantee requires a held-out set).</item>
    ///   <item>Computes the ambiguous-prediction rate: fraction of logs where both Buy and Sell
    ///         pass the threshold simultaneously, meaning the model cannot confidently distinguish.</item>
    ///   <item>Persists an <see cref="MLConformalCalibration"/> record with all diagnostics.</item>
    /// </list>
    ///
    /// Meta-learner and MAML-initializer models are excluded as they do not produce
    /// direct trade signals and their confidence scores are not interpretable in this framework.
    /// </summary>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Find active base models that do not yet have a conformal calibration record.
        // Meta-learners and MAML initializers are excluded (their scores are not direct probabilities).
        var modelsToCalibrate = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer)
            .ToListAsync(ct);

        foreach (var model in modelsToCalibrate)
        {
            // Skip models that already have a calibration record — handled by MLConformalRecalibrationWorker.
            bool alreadyCalibrated = await readDb.Set<MLConformalCalibration>()
                .AnyAsync(c => c.MLModelId == model.Id && !c.IsDeleted, ct);
            if (alreadyCalibrated) continue;

            // Skip models without serialised model weights (not yet fully trained).
            if (model.ModelBytes == null) continue;

            try
            {
                var snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
                // Require both Weights and Biases to be present — models without these cannot score.
                if (snap?.Weights == null || snap.Biases == null) continue;
                double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

                // Use recent resolved prediction logs as the conformal calibration set.
                // Up to 500 logs are loaded; at least 50 are required for a meaningful quantile.
                var calLogs = await readDb.Set<MLModelPredictionLog>()
                    .Where(l => l.MLModelId == model.Id
                             && !l.IsDeleted
                             && l.ActualDirection.HasValue
                             && l.DirectionCorrect.HasValue
                             && (l.ConfidenceScore > 0
                                 || l.CalibratedProbability != null
                                 || l.RawProbability != null))
                    .OrderByDescending(l => l.PredictedAt)
                    .Take(500)
                    .ToListAsync(ct);

                if (calLogs.Count < 50) continue;

                // Compute nonconformity scores: α_i = 1 − P(true class).
                // For each prediction log:
                //   pBuy  = ConfidenceScore (probability assigned to Buy)
                //   pSell = 1 − pBuy        (probability assigned to Sell, assumes binary sum = 1)
                //   pTrue = probability assigned to the class that actually occurred
                //   score = 1 − pTrue  (how nonconformant the prediction is: 0 = perfect, 1 = worst)
                var scores = new List<double>(calLogs.Count);
                foreach (var log in calLogs)
                {
                    double pBuy = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(log, decisionThreshold);
                    // pTrue is pBuy when the predicted direction matches actual, else 1 - pBuy.
                    double pTrue = log.PredictedDirection == log.ActualDirection
                        ? pBuy : 1.0 - pBuy;
                    scores.Add(1.0 - pTrue);
                }
                scores.Sort(); // Sorted ascending for quantile lookup.

                // Split-conformal coverage threshold τ at 90 % coverage (α = 0.10).
                // Formula: τ = scores[⌈(n+1)(1−α)⌉ − 1]
                // This is the finite-sample correction for the empirical quantile that provides
                // the marginal coverage guarantee P(Y ∈ C(X)) ≥ 1 − α.
                double alpha      = 0.10;   // mis-coverage rate (10 % of predictions may fall outside the set)
                int    n          = scores.Count;
                int    qIdx       = (int)Math.Ceiling((n + 1) * (1 - alpha)) - 1;
                qIdx              = Math.Clamp(qIdx, 0, n - 1); // guard against out-of-range index
                double threshold  = scores[qIdx]; // τ: the coverage threshold

                // Empirical coverage on the same calibration set.
                // Note: this is an upper bound because the calibration set was used to select τ.
                // True held-out coverage guarantees require a separate test set.
                // Coverage check: the model's prediction set at threshold τ covers the true label
                // when the nonconformity score of the true label ≤ τ.
                int covered = calLogs.Count(l =>
                {
                    double pBuy = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(l, decisionThreshold);
                    double pSell = 1.0 - pBuy;
                    // Prediction set membership: include Buy if (1 − pBuy) ≤ τ, Sell if (1 − pSell) ≤ τ.
                    bool   inBuy  = (1 - pBuy)  <= threshold;
                    bool   inSell = (1 - pSell)  <= threshold;
                    // The true label is covered when its corresponding prediction set member is included.
                    return (l.ActualDirection!.Value == Domain.Enums.TradeDirection.Buy && inBuy)
                        || (l.ActualDirection!.Value == Domain.Enums.TradeDirection.Sell && inSell);
                });
                double empCoverage  = (double)covered / calLogs.Count;

                // Ambiguous prediction rate: fraction where both Buy and Sell are in the prediction set.
                // A high ambiguous rate means τ is too loose — the model is too uncertain to distinguish.
                // (1 − p) ≤ τ AND p ≤ τ  ⟺  (1 − τ) ≤ p ≤ τ  ⟺  the probability is near 0.5.
                int    ambiguousN   = calLogs.Count(l =>
                {
                    double p = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(l, decisionThreshold);
                    return (1 - p) <= threshold && p <= threshold;
                });

                // Persist the calibration record with all computed diagnostics.
                writeDb.Set<MLConformalCalibration>().Add(new MLConformalCalibration
                {
                    MLModelId                = model.Id,
                    Symbol                   = model.Symbol,
                    Timeframe                = model.Timeframe,
                    // Store the full sorted nonconformity score array for future recalibration.
                    NonConformityScoresJson  = JsonSerializer.Serialize(scores),
                    CalibrationSamples       = n,
                    CoverageAlpha            = 1 - alpha,    // nominal coverage level = 0.90
                    CoverageThreshold        = threshold,    // τ: the quantile threshold
                    EmpiricalCoverage        = empCoverage,
                    AmbiguousRate            = (double)ambiguousN / calLogs.Count,
                    CalibratedAt             = DateTime.UtcNow
                });

                await writeDb.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Conformal calibration: model {Id} τ={T:F4} coverage={C:P1} ambiguous={A:P1}",
                    model.Id, threshold, empCoverage, (double)ambiguousN / calLogs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Conformal calibration failed for model {Id}", model.Id);
            }
        }
    }
}
