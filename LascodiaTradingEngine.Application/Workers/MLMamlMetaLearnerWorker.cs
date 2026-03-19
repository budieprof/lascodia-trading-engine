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

    public MLMamlMetaLearnerWorker(IServiceScopeFactory scopeFactory, ILogger<MLMamlMetaLearnerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

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

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Collect active models as "tasks" — each is a (symbol, timeframe) pair
        var tasks = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner
                     && !m.IsMamlInitializer && m.ModelBytes != null)
            .ToListAsync(ct);

        if (tasks.Count < MinTasks)
        {
            _logger.LogDebug("MLMamlMetaLearnerWorker: insufficient tasks ({N}), skipping.", tasks.Count);
            return;
        }

        // Load a reference model to get the weight dimensions
        var refSnap = JsonSerializer.Deserialize<ModelSnapshot>(tasks[0].ModelBytes!);
        if (refSnap?.Weights == null || refSnap.Biases == null) return;

        int K = refSnap.Weights.Length;
        int F = refSnap.Weights[0].Length;

        // Initialise meta-weights as average across task models
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
                for (int k = 0; k < Math.Min(K, snap.Weights.Length); k++)
                {
                    for (int f = 0; f < Math.Min(F, snap.Weights[k].Length); f++)
                        metaW[k][f] += snap.Weights[k][f];
                    if (k < snap.Biases.Length) metaB[k] += snap.Biases[k];
                }
                validTasks++;
            }
            catch { /* skip malformed snapshots */ }
        }
        if (validTasks == 0) return;

        for (int k = 0; k < K; k++) { for (int f = 0; f < F; f++) metaW[k][f] /= validTasks; metaB[k] /= validTasks; }

        var rng = new Random(42);

        // MAML outer loop — simplified first-order MAML (FOMAML)
        for (int step = 0; step < OuterSteps && !ct.IsCancellationRequested; step++)
        {
            var outerGradW = new double[K][];
            var outerGradB = new double[K];
            for (int k = 0; k < K; k++) outerGradW[k] = new double[F];

            // Sample a mini-batch of tasks
            var batch = tasks.OrderBy(_ => rng.Next()).Take(Math.Min(5, tasks.Count)).ToList();
            foreach (var taskModel in batch)
            {
                try
                {
                    // Load candles for this task and build training samples
                    var candles = await readDb.Set<Candle>()
                        .Where(c => c.Symbol == taskModel.Symbol
                                 && c.Timeframe == taskModel.Timeframe
                                 && !c.IsDeleted)
                        .OrderBy(c => c.Timestamp)
                        .Take(300)
                        .ToListAsync(ct);

                    if (candles.Count < MLFeatureHelper.LookbackWindow + 30) continue;
                    var taskSamples = MLFeatureHelper.BuildTrainingSamples(candles).ToArray();
                    if (taskSamples.Length < 20) continue;

                    // Clone meta-weights for inner loop
                    var localW = metaW.Select(r => (double[])r.Clone()).ToArray();
                    var localB = (double[])metaB.Clone();

                    // Inner loop: InnerSteps SGD steps on task support set
                    var support = taskSamples.Take(taskSamples.Length / 2).ToArray();
                    for (int inner = 0; inner < InnerSteps; inner++)
                        SgdStep(localW, localB, support, InnerLr, K, F);

                    // Outer gradient: evaluate on query set with adapted weights
                    var query = taskSamples.Skip(taskSamples.Length / 2).ToArray();
                    AccumulateGradient(outerGradW, outerGradB, localW, localB, query, K, F);
                }
                catch { /* skip */ }
            }

            // Outer update
            for (int k = 0; k < K; k++)
            {
                for (int f = 0; f < F; f++) metaW[k][f] -= OuterLr * outerGradW[k][f] / batch.Count;
                metaB[k] -= OuterLr * outerGradB[k] / batch.Count;
            }
        }

        // Persist meta-initialiser
        var metaSnap = new ModelSnapshot
        {
            Type     = "MAML-Init",
            Version  = "1.0",
            Features = MLFeatureHelper.FeatureNames,
            Weights  = metaW,
            Biases   = metaB
        };

        // Deactivate previous meta-initialisers
        var prevMaml = await writeDb.Set<MLModel>()
            .Where(m => m.IsMamlInitializer && !m.IsDeleted)
            .ToListAsync(ct);
        foreach (var p in prevMaml) { p.IsActive = false; p.IsMamlInitializer = false; }

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
            TrainingSamples     = validTasks
        };
        writeDb.Set<MLModel>().Add(mamlModel);

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

    private static void SgdStep(double[][] w, double[] b, TrainingSample[] samples, double lr, int K, int F)
    {
        foreach (var s in samples)
        {
            for (int k = 0; k < K; k++)
            {
                double dot = b[k];
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) dot += w[k][f] * s.Features[f];
                double p   = 1.0 / (1 + Math.Exp(-dot));
                double err = p - s.Direction;
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) w[k][f] -= lr * err * s.Features[f];
                b[k] -= lr * err;
            }
        }
    }

    private static void AccumulateGradient(double[][] gradW, double[] gradB, double[][] w, double[] b,
        TrainingSample[] samples, int K, int F)
    {
        foreach (var s in samples)
        {
            for (int k = 0; k < K; k++)
            {
                double dot = b[k];
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) dot += w[k][f] * s.Features[f];
                double p   = 1.0 / (1 + Math.Exp(-dot));
                double err = p - s.Direction;
                for (int f = 0; f < Math.Min(F, s.Features.Length); f++) gradW[k][f] += err * s.Features[f];
                gradB[k] += err;
            }
        }
    }
}
