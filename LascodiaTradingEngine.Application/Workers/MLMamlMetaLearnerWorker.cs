using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Model-Agnostic Meta-Learning (MAML) worker that finds a task-agnostic weight
/// initialisation enabling rapid fine-tuning to new symbols in K gradient steps (Rec #33).
/// </summary>
/// <remarks>
/// MAML outer loop (runs weekly):
///   For each meta-update step:
///     1. Sample a mini-batch of symbol tasks from existing models.
///     2. For each task, run K inner-loop SGD steps from the current meta-init θ → θ'_task.
///     3. Compute the outer-loop gradient as the average loss on task support sets using θ'_task.
///     4. Update θ ← θ − α_outer × Σ ∇_θ L_task(θ'_task).
///
/// After convergence, the meta-initialiser is stored as a special <see cref="MLModel"/>
/// with <c>IsMamlInitializer = true</c>.
///
/// When a new symbol with insufficient data is encountered, the training worker loads
/// this model's <c>ModelBytes</c> and fine-tunes in K inner steps instead of cold-starting.
/// </remarks>
public sealed class MLMamlMetaLearnerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLMamlMetaLearnerWorker> _logger;

    private const int  OuterSteps   = 50;
    private const int  InnerSteps   = 5;
    private const double InnerLr    = 0.01;
    private const double OuterLr    = 0.001;
    private const int  MinTasks     = 3;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per weekly training cycle so scoped EF Core
    /// contexts are correctly disposed after each run.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLMamlMetaLearnerWorker(IServiceScopeFactory scopeFactory, ILogger<MLMamlMetaLearnerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Executes immediately on startup then re-runs every
    /// 7 days. The weekly cadence gives newly activated task models time to accumulate
    /// candle history before their data is used to update the meta-initialiser.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLMamlMetaLearnerWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLMamlMetaLearnerWorker error"); }
            await Task.Delay(TimeSpan.FromDays(7), stoppingToken);
        }
    }

    /// <summary>
    /// Core MAML meta-learning training loop. Computes a task-agnostic weight
    /// initialisation θ that allows any new symbol's model to reach good performance
    /// in only <see cref="InnerSteps"/> gradient steps (few-shot adaptation).
    /// </summary>
    /// <remarks>
    /// MAML meta-learning methodology (Finn et al., 2017):
    ///
    /// MAML seeks θ such that for any new task T_i, the fine-tuned weights
    ///   θ'_i = θ − α_inner × ∇_θ L_Ti(θ)
    /// achieve low loss on T_i after only K inner steps.
    ///
    /// <b>Outer-loop initialisation:</b>
    /// Because true MAML requires differentiating through the inner loop (second-order
    /// gradients), we implement First-Order MAML (FOMAML) which uses the inner-adapted
    /// weights θ'_i as if they were the original θ when computing the outer gradient.
    /// This loses accuracy but reduces computation from O(K²) to O(K).
    ///
    /// <b>Step-by-step algorithm:</b>
    /// <list type="number">
    ///   <item>
    ///     Collect all active base models (excluding meta-learners and MAML initialisers)
    ///     as the set of training tasks. Each task is identified by its (symbol, timeframe)
    ///     pair and carries a serialised <see cref="ModelSnapshot"/> of its learned weights.
    ///     At least <see cref="MinTasks"/> tasks are required.
    ///   </item>
    ///   <item>
    ///     <b>Meta-initialisation:</b> Start θ as the element-wise average of all task
    ///     model weights. This "warm average" is closer to a good initialisation than
    ///     random and reduces the number of outer steps needed to converge.
    ///   </item>
    ///   <item>
    ///     <b>FOMAML outer loop</b> (<see cref="OuterSteps"/> iterations):
    ///     Sample a random mini-batch of 5 tasks. For each task:
    ///     <list type="bullet">
    ///       <item>Load up to 300 candles and build training samples via
    ///             <see cref="MLFeatureHelper.BuildTrainingSamples"/>.</item>
    ///       <item>Split into a support set (first half) and query set (second half).</item>
    ///       <item>Clone θ → localW/localB and run <see cref="InnerSteps"/> SGD steps
    ///             on the support set at <see cref="InnerLr"/> to get adapted weights θ'.</item>
    ///       <item>Accumulate the outer gradient by evaluating the cross-entropy loss on
    ///             the query set using θ' via <see cref="AccumulateGradient"/>.</item>
    ///     </list>
    ///     Apply the averaged outer gradient to θ at <see cref="OuterLr"/>.
    ///   </item>
    ///   <item>
    ///     <b>Persistence:</b> Deactivate all prior MAML initialisers and insert a new
    ///     <see cref="MLModel"/> with <c>IsMamlInitializer = true</c>, <c>Symbol = "ALL"</c>,
    ///     and <c>ModelBytes</c> containing the converged θ. Also insert a completed
    ///     <see cref="MLTrainingRun"/> with <c>IsMamlRun = true</c> for audit.
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

        // Collect active models as MAML "tasks" — each is a (symbol, timeframe) pair
        // with a known weight distribution. Exclude meta-learners and prior MAML
        // initialisers to avoid circular meta-learning.
        var tasks = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner
                     && !m.IsMamlInitializer && m.ModelBytes != null)
            .ToListAsync(ct);

        if (tasks.Count < MinTasks)
        {
            _logger.LogDebug("MLMamlMetaLearnerWorker: insufficient tasks ({N}), skipping.", tasks.Count);
            return;
        }

        // Load the first valid snapshot to determine the weight tensor dimensions (K, F).
        // All task models should share the same architecture; dimension mismatches are
        // guarded against in the averaging loop below using Math.Min bounds.
        var refSnap = JsonSerializer.Deserialize<ModelSnapshot>(tasks[0].ModelBytes!);
        if (refSnap?.Weights == null || refSnap.Biases == null) return;

        int K = refSnap.Weights.Length;  // number of ensemble learners
        int F = refSnap.Weights[0].Length; // number of input features per learner

        // Initialise meta-weights θ as the element-wise average across all task models.
        // This "warm average" initialisation is a standard MAML baseline that reduces
        // the number of outer steps required to reach a good initialisation.
        var metaW = new double[K][];
        var metaB = new double[K];
        for (int k = 0; k < K; k++) metaW[k] = new double[F];

        int validTasks = 0;
        foreach (var task in tasks)
        {
            try
            {
                var snap = JsonSerializer.Deserialize<ModelSnapshot>(task.ModelBytes!);
                if (snap?.Weights == null || snap.Biases == null) continue;
                // Accumulate weights element-wise; Math.Min guards against dimension mismatches
                // between models trained with different feature sets.
                for (int k = 0; k < Math.Min(K, snap.Weights.Length); k++)
                {
                    for (int f = 0; f < Math.Min(F, snap.Weights[k].Length); f++)
                        metaW[k][f] += snap.Weights[k][f];
                    if (k < snap.Biases.Length) metaB[k] += snap.Biases[k];
                }
                validTasks++;
            }
            catch { /* skip malformed snapshots — do not abort the full meta-update */ }
        }
        if (validTasks == 0) return;

        // Divide accumulated sum by valid task count to get the element-wise average.
        for (int k = 0; k < K; k++) { for (int f = 0; f < F; f++) metaW[k][f] /= validTasks; metaB[k] /= validTasks; }

        // Fixed random seed for reproducible task sampling within each weekly run.
        var rng = new Random(42);

        // MAML outer loop — simplified first-order MAML (FOMAML).
        // FOMAML ignores the curvature correction from the Hessian of the inner loop,
        // treating the inner-adapted θ' as if it were directly differentiated w.r.t. θ.
        // This is a well-studied approximation that is 80–90% as effective as full MAML.
        for (int step = 0; step < OuterSteps && !ct.IsCancellationRequested; step++)
        {
            // Accumulate outer gradient across the task mini-batch.
            var outerGradW = new double[K][];
            var outerGradB = new double[K];
            for (int k = 0; k < K; k++) outerGradW[k] = new double[F];

            // Sample a random mini-batch of 5 tasks (or fewer if < 5 tasks exist).
            var batch = tasks.OrderBy(_ => rng.Next()).Take(Math.Min(5, tasks.Count)).ToList();
            foreach (var taskModel in batch)
            {
                try
                {
                    // Load recent candles for this task and build feature/label samples.
                    // Take(300) caps memory and computation without losing representative
                    // recent market behaviour for the inner loop.
                    var candles = await readDb.Set<Candle>()
                        .Where(c => c.Symbol == taskModel.Symbol
                                 && c.Timeframe == taskModel.Timeframe
                                 && !c.IsDeleted)
                        .OrderBy(c => c.Timestamp)
                        .Take(300)
                        .ToListAsync(ct);

                    // MLFeatureHelper.LookbackWindow candles are consumed before the first
                    // sample is available; require at least 30 usable samples.
                    if (candles.Count < MLFeatureHelper.LookbackWindow + 30) continue;
                    var taskSamples = MLFeatureHelper.BuildTrainingSamples(candles).ToArray();
                    if (taskSamples.Length < 20) continue;

                    // Clone meta-weights for the inner loop — each task adapts its own
                    // private copy of θ without modifying the shared meta-initialiser.
                    var localW = metaW.Select(r => (double[])r.Clone()).ToArray();
                    var localB = (double[])metaB.Clone();

                    // Inner loop: InnerSteps SGD gradient steps on the support set (first half).
                    // The support set simulates the "K-shot" examples available at adaptation time.
                    var support = taskSamples.Take(taskSamples.Length / 2).ToArray();
                    for (int inner = 0; inner < InnerSteps; inner++)
                        SgdStep(localW, localB, support, InnerLr, K, F);

                    // Outer gradient: evaluate adapted weights θ' on the query set (second half).
                    // The query set simulates the test examples the adapted model must generalise to.
                    // AccumulateGradient adds to outerGradW/outerGradB in place.
                    var query = taskSamples.Skip(taskSamples.Length / 2).ToArray();
                    AccumulateGradient(outerGradW, outerGradB, localW, localB, query, K, F);
                }
                catch { /* skip tasks that fail — do not abort the outer loop */ }
            }

            // Outer update: move θ in the direction that reduces average query-set loss
            // across all tasks, scaled by the batch size to keep lr independent of batch count.
            for (int k = 0; k < K; k++)
            {
                for (int f = 0; f < F; f++) metaW[k][f] -= OuterLr * outerGradW[k][f] / batch.Count;
                metaB[k] -= OuterLr * outerGradB[k] / batch.Count;
            }
        }

        // Serialise the converged meta-initialiser θ into a ModelSnapshot.
        var metaSnap = new ModelSnapshot
        {
            Type     = "MAML-Init",
            Version  = "1.0",
            Features = MLFeatureHelper.FeatureNames,
            Weights  = metaW,
            Biases   = metaB
        };

        // Deactivate all previous MAML initialisers before inserting the new one.
        // This ensures the training worker always uses the most recently converged θ.
        var prevMaml = await writeDb.Set<MLModel>()
            .Where(m => m.IsMamlInitializer && !m.IsDeleted)
            .ToListAsync(ct);
        foreach (var p in prevMaml) { p.IsActive = false; p.IsMamlInitializer = false; }

        // Insert the new meta-initialiser with Symbol="ALL" to signal that it applies
        // across all symbols (the training worker queries by IsMamlInitializer, not Symbol).
        var mamlModel = new MLModel
        {
            Symbol              = "ALL",
            Timeframe           = Timeframe.H1,
            ModelVersion        = $"MAML-{DateTime.UtcNow:yyyyMMdd}",
            Status              = MLModelStatus.Active,
            IsActive            = true,
            IsMamlInitializer   = true,
            ModelBytes          = JsonSerializer.SerializeToUtf8Bytes(metaSnap),
            TrainedAt           = DateTime.UtcNow,
            TrainingSamples     = validTasks // number of task models used in outer loop
        };
        writeDb.Set<MLModel>().Add(mamlModel);

        // Insert an audit training run record so the ML health worker can track MAML runs.
        writeDb.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol          = "ALL",
            Timeframe       = Timeframe.H1,
            TriggerType     = TriggerType.Scheduled,
            Status          = RunStatus.Completed,
            FromDate        = DateTime.UtcNow.AddDays(-365),
            ToDate          = DateTime.UtcNow,
            TotalSamples    = validTasks,
            IsMamlRun       = true,
            MamlInnerSteps  = InnerSteps,
            CompletedAt     = DateTime.UtcNow
        });

        await writeDb.SaveChangesAsync(ct);
        _logger.LogInformation("MLMamlMetaLearnerWorker trained meta-initialiser from {N} task models.", validTasks);
    }

    /// <summary>
    /// Performs a single full-pass SGD update over the supplied training samples,
    /// modifying the weight matrix <paramref name="w"/> and bias vector <paramref name="b"/>
    /// in place. Used for the MAML inner loop (task adaptation).
    /// </summary>
    /// <param name="w">Ensemble weight matrix [K × F] to update in place.</param>
    /// <param name="b">Ensemble bias vector [K] to update in place.</param>
    /// <param name="samples">Training samples (feature vectors + direction labels).</param>
    /// <param name="lr">Inner-loop learning rate (typically <see cref="InnerLr"/> = 0.01).</param>
    /// <param name="K">Number of ensemble learners.</param>
    /// <param name="F">Number of features per learner.</param>
    private static void SgdStep(double[][] w, double[] b, TrainingSample[] samples, double lr, int K, int F)
    {
        foreach (var s in samples)
        {
            for (int k = 0; k < K; k++)
            {
                // Forward pass: logit = w_k · x + b_k, probability = sigmoid(logit).
                double dot = b[k];
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) dot += w[k][f] * s.Features[f];
                double p   = 1.0 / (1 + Math.Exp(-dot));

                // Cross-entropy gradient: err = σ(z) − y.
                double err = p - s.Direction;

                // Gradient descent update for weights and bias.
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) w[k][f] -= lr * err * s.Features[f];
                b[k] -= lr * err;
            }
        }
    }

    /// <summary>
    /// Accumulates the outer-loop MAML gradient by evaluating the cross-entropy loss
    /// over the query set using the inner-adapted weights <paramref name="w"/> and
    /// biases <paramref name="b"/>. Gradients are accumulated into
    /// <paramref name="gradW"/> and <paramref name="gradB"/> (not applied).
    /// </summary>
    /// <remarks>
    /// In FOMAML the outer gradient is computed as ∂L_query/∂θ' (treating θ' as if
    /// it were θ). Accumulation without division keeps the outer update code clean —
    /// the caller divides by batch size when applying the outer gradient step.
    /// </remarks>
    /// <param name="gradW">Outer gradient accumulator for weights [K × F].</param>
    /// <param name="gradB">Outer gradient accumulator for biases [K].</param>
    /// <param name="w">Inner-adapted weight matrix θ' [K × F].</param>
    /// <param name="b">Inner-adapted bias vector θ' [K].</param>
    /// <param name="samples">Query set samples for outer loss evaluation.</param>
    /// <param name="K">Number of ensemble learners.</param>
    /// <param name="F">Number of features per learner.</param>
    private static void AccumulateGradient(double[][] gradW, double[] gradB, double[][] w, double[] b,
        TrainingSample[] samples, int K, int F)
    {
        foreach (var s in samples)
        {
            for (int k = 0; k < K; k++)
            {
                // Forward pass using adapted weights θ'.
                double dot = b[k];
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) dot += w[k][f] * s.Features[f];
                double p   = 1.0 / (1 + Math.Exp(-dot));

                // Cross-entropy gradient err = σ(z) − y; add to accumulator without applying.
                double err = p - s.Direction;
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) gradW[k][f] += err * s.Features[f];
                gradB[k] += err;
            }
        }
    }
}
