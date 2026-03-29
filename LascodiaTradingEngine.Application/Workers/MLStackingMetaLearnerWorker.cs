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
/// Trains a logistic regression stacking meta-learner on the out-of-fold probability
/// outputs from all active base models for each symbol/timeframe (Rec #25).
/// </summary>
/// <remarks>
/// The stacking procedure:
///   1. Collect resolved <see cref="MLModelPredictionLog"/> records for all active base models.
///   2. For each signal: form a feature vector of each base model's <c>ConfidenceScore</c>.
///   3. Train a logistic regression meta-learner on (base_probs → actual_direction).
///   4. Persist the weights in <see cref="MLStackingMetaModel"/>.
///
/// The meta-learner runs weekly.  A minimum of 100 shared outcomes across base models
/// is required before training proceeds.
/// </remarks>
public sealed class MLStackingMetaLearnerWorker : BackgroundService
{
    private const int TrainingWindowDays = 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLStackingMetaLearnerWorker> _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per weekly training cycle so that scoped EF Core
    /// contexts are correctly disposed after each run.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLStackingMetaLearnerWorker(IServiceScopeFactory scopeFactory, ILogger<MLStackingMetaLearnerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Executes immediately on startup then re-runs every
    /// 7 days. The weekly cadence matches the minimum accumulation period for enough
    /// shared prediction logs across multiple base models.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLStackingMetaLearnerWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLStackingMetaLearnerWorker error"); }
            await Task.Delay(TimeSpan.FromDays(7), stoppingToken);
        }
    }

    /// <summary>
    /// Core stacking meta-learner training cycle. For each symbol/timeframe combination
    /// that has at least two active base models, assembles a stacking dataset from their
    /// resolved prediction logs and trains a logistic regression meta-learner via
    /// <see cref="TrainLogisticMeta"/>.
    /// </summary>
    /// <remarks>
    /// Stacking meta-learner methodology:
    /// <list type="number">
    ///   <item>
    ///     <b>Base model discovery:</b> Load all active, non-meta, non-MAML models
    ///     from the read context. Exclude meta-learners and MAML initialisers so the
    ///     meta-learner is trained only on first-level predictors, not on its own
    ///     prior outputs (which would cause label leakage).
    ///   </item>
    ///   <item>
    ///     <b>Dataset construction (out-of-fold stacking):</b> For each trade signal
    ///     (keyed by <c>TradeSignalId</c>) where at least two base models produced a
    ///     resolved prediction, build a row vector whose k-th element is base model k's
    ///     <c>ConfidenceScore</c> (proxy probability for the Buy direction). Signals
    ///     where a base model has no log entry receive a neutral 0.5 imputation. The
    ///     target label y=1 for Buy, y=0 for Sell/Neutral.
    ///   </item>
    ///   <item>
    ///     <b>Minimum data gate:</b> Require at least 100 shared signal rows before
    ///     training proceeds. Below this threshold the logistic regression is
    ///     under-constrained and the meta-weights are unreliable.
    ///   </item>
    ///   <item>
    ///     <b>Meta-learner training:</b> Delegate to <see cref="TrainLogisticMeta"/>
    ///     which runs 200 epochs of full-batch gradient descent (lr=0.01) on the
    ///     stacking dataset. Returns weights, bias, direction accuracy, and Brier score.
    ///   </item>
    ///   <item>
    ///     <b>Persistence:</b> Deactivate the previous <see cref="MLStackingMetaModel"/>
    ///     for the symbol/timeframe (soft-deactivate, not delete) then insert a new
    ///     active record with the trained weights. The scoring path reads the active
    ///     meta-model to blend base model outputs at inference time.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Find all symbol/timeframe combinations with ≥ 2 active base models.
        // Meta-learners and MAML initialisers are excluded — they are not first-level
        // predictors and including them would introduce circular self-reference.
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive &&
                        !m.IsDeleted &&
                        !m.IsMetaLearner &&
                        !m.IsMamlInitializer &&
                        !m.IsSuppressed &&
                        !m.IsFallbackChampion)
            .ToListAsync(ct);

        // Only proceed for (symbol, timeframe) pairs that have multiple base models.
        // A single model cannot produce a meaningful stacking dataset.
        var groups = activeModels
            .GroupBy(m => (m.Symbol, m.Timeframe))
            .Where(g => g.Count() >= 2)
            .ToList();

        foreach (var group in groups)
        {
            var (symbol, timeframe) = group.Key;
            var modelIds = group.Select(m => m.Id).ToList();
            DateTime cohortStart = group.Max(m => m.ActivatedAt ?? m.TrainedAt);
            DateTime since = DateTime.UtcNow.AddDays(-TrainingWindowDays);
            if (cohortStart > since)
                since = cohortStart;

            // Train on the current active cohort over a bounded recent window so the stacker
            // does not learn from stale pre-cohort or long-obsolete calibration regimes.
            var allLogs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => modelIds.Contains(l.MLModelId)
                         && !l.IsDeleted
                         && l.DirectionCorrect.HasValue
                         && l.ActualDirection.HasValue
                         && l.CalibratedProbability != null
                         && l.OutcomeRecordedAt != null
                         && l.OutcomeRecordedAt >= since)
                .ToListAsync(ct);

            // Build stacking dataset: for each TradeSignalId, assemble a K-dimensional
            // feature vector of base model confidence scores.
            // Filter to signals covered by at least 2 distinct models so the meta-learner
            // has genuine cross-model variation to learn from.
            var bySignal = allLogs
                .GroupBy(l => l.TradeSignalId)
                .Where(g => g.Select(l => l.MLModelId).Distinct().Count() >= 2)
                .ToList();

            // Require a minimum of 100 shared outcomes before training.
            // Below this threshold the logistic regression is statistically unreliable.
            if (bySignal.Count < 100) continue;

            int K = modelIds.Count;
            var X = new List<double[]>(); // rows = signals, columns = base model probabilities
            var y = new List<int>();      // target: 1 = Buy, 0 = Sell/Neutral

            foreach (var sg in bySignal)
            {
                // Build feature row: each element is base model k's ConfidenceScore.
                // Default to 0.5 (maximum uncertainty) if a base model has no log for
                // this signal — this is equivalent to treating the missing model as
                // a random classifier with no opinion.
                var row = new double[K];
                for (int k = 0; k < K; k++)
                {
                    var log = sg.FirstOrDefault(l => l.MLModelId == modelIds[k]);
                    row[k]  = log != null
                        ? MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(log)
                        : 0.5;
                }

                // Use the first log with a resolved ActualDirection as the ground truth.
                // All logs for the same signal share the same underlying trade outcome.
                var trueDir = sg.First(l => l.ActualDirection.HasValue).ActualDirection!.Value;
                X.Add(row);
                y.Add(trueDir == Domain.Enums.TradeDirection.Buy ? 1 : 0);
            }

            // Train the logistic meta-learner on the assembled stacking dataset.
            var (weights, bias, accuracy, brier) = TrainLogisticMeta(X, y);

            // Deactivate the previous active meta-learner for this symbol/timeframe.
            // Soft-deactivate (IsActive = false) preserves audit history.
            var prev = await writeDb.Set<MLStackingMetaModel>()
                .Where(s => s.Symbol == symbol && s.Timeframe == timeframe && s.IsActive && !s.IsDeleted)
                .ToListAsync(ct);
            foreach (var p in prev) p.IsActive = false;

            // Persist the newly trained meta-learner. Weights and base model IDs are
            // JSON-serialised for schema flexibility; the scoring path deserialises them.
            writeDb.Set<MLStackingMetaModel>().Add(new MLStackingMetaModel
            {
                Symbol           = symbol,
                Timeframe        = timeframe,
                BaseModelIdsJson = JsonSerializer.Serialize(modelIds),
                BaseModelCount   = K,
                MetaWeightsJson  = JsonSerializer.Serialize(weights),
                MetaBias         = bias,
                DirectionAccuracy = (decimal)accuracy,
                BrierScore       = (decimal)brier,
                IsActive         = true,
                TrainingSamples  = X.Count,
                TrainedAt        = DateTime.UtcNow
            });

            await writeDb.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Stacking meta-learner trained for {S}/{T}: {K} base models, {N} samples, acc={A:P1}",
                symbol, timeframe, K, X.Count, accuracy);
        }
    }

    /// <summary>
    /// Trains a logistic regression meta-learner using full-batch gradient descent.
    /// </summary>
    /// <remarks>
    /// The meta-learner learns to combine base model probabilities via a linear logistic
    /// function: P(Buy) = σ(Σ_k w_k × p_k + b). This is the canonical Level-2 learner
    /// in the stacking ensemble literature (Wolpert 1992, Breiman 1996).
    ///
    /// Training details:
    /// <list type="bullet">
    ///   <item>200 full-batch epochs with a fixed learning rate of 0.01.</item>
    ///   <item>
    ///     Gradient: for each sample i, compute σ(z_i) − y_i and update:
    ///     w_k -= lr × err × p_k[i], b -= lr × err.
    ///   </item>
    ///   <item>
    ///     Evaluation: after training, compute direction accuracy (argmax threshold 0.5)
    ///     and Brier score (mean squared error between predicted probability and label).
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="X">Feature matrix: N samples × K base model probabilities.</param>
    /// <param name="y">Binary labels: 1 = Buy, 0 = Sell/Neutral.</param>
    /// <returns>
    /// Tuple of (Weights, Bias, DirectionAccuracy, BrierScore).
    /// </returns>
    private static (double[] Weights, double Bias, double Accuracy, double Brier)
        TrainLogisticMeta(List<double[]> X, List<int> y)
    {
        int N = X.Count, K = X[0].Length;
        var w = new double[K]; // initialise to zero (no warm-start needed for meta-learner)
        double b = 0;
        double lr = 0.01;

        // Full-batch gradient descent for 200 epochs.
        // Full-batch is acceptable here because the dataset is relatively small (≥ 100 rows).
        for (int epoch = 0; epoch < 200; epoch++)
        {
            for (int i = 0; i < N; i++)
            {
                // Forward pass: logit = w · x + b, probability = sigmoid(logit).
                double dot = b;
                for (int k = 0; k < K; k++) dot += w[k] * X[i][k];
                double p   = 1.0 / (1 + Math.Exp(-dot));

                // Cross-entropy gradient: err = σ(z) − y.
                double err = p - y[i];

                // Weight and bias update (gradient descent step).
                for (int k = 0; k < K; k++) w[k] -= lr * err * X[i][k];
                b -= lr * err;
            }
        }

        // Evaluate on training set to report accuracy and Brier score.
        // (Out-of-fold evaluation would be more rigorous but requires a held-out split
        //  not available in the current data collection pipeline.)
        int correct = 0;
        double brierSum = 0;
        for (int i = 0; i < N; i++)
        {
            double dot = b;
            for (int k = 0; k < K; k++) dot += w[k] * X[i][k];
            double p   = 1.0 / (1 + Math.Exp(-dot));
            int pred   = p >= 0.5 ? 1 : 0;
            if (pred == y[i]) correct++;
            // Brier component: (p - y)^2; mean across N gives the Brier score.
            brierSum += (p - y[i]) * (p - y[i]);
        }
        return (w, b, (double)correct / N, brierSum / N);
    }
}
