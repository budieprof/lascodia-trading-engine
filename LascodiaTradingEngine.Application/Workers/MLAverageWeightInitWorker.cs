using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Cross-symbol average weight initialization worker that produces a task-agnostic weight
/// initialisation by averaging weights across all symbols (Rec #33). The resulting
/// initialiser enables faster convergence when training on new symbols.
/// </summary>
/// <remarks>
/// This is a cross-symbol weight averaging heuristic, NOT true MAML (which requires
/// inner/outer gradient loops). The resulting initializer enables faster convergence
/// when training on new symbols.
///
/// Runs every 48 hours (configurable via <c>MLAvgWeightInit:PollIntervalSeconds</c>).
///
/// For symbols with >= 5 completed training runs, the worker loads the final weights
/// from each symbol's most recent successful model and computes a weight initialisation
/// by averaging weights across ALL symbols.
///
/// The resulting model (<c>IsMamlInitializer = true</c>) is stored per timeframe.
/// When a new symbol needs cold-start training, the training worker can warm-start
/// from the averaged initialiser instead of random weights.
/// </remarks>
public sealed class MLAverageWeightInitWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLAverageWeightInitWorker> _logger;

    private const int    DefaultPollIntervalSec = 172800; // 48 hours
    private const string CK_PollInterval        = "MLAvgWeightInit:PollIntervalSeconds";
    private const int    MinSymbolsForMaml      = 5;

    public MLAverageWeightInitWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLAverageWeightInitWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLAverageWeightInitWorker started.");

        // Stagger startup to let training workers complete initial runs first.
        await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollIntervalSec;
            try
            {
                using var scope = _scopeFactory.CreateAsyncScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var readDb  = readCtx.GetDbContext();
                pollSecs = await GetConfigAsync<int>(readDb, CK_PollInterval, DefaultPollIntervalSec, stoppingToken);

                await RunAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MLAverageWeightInitWorker error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }
    }

    private async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var readCtx  = sp.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = sp.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Find all symbols that have completed training runs, grouped by symbol.
        // We need symbols with >= MinSymbolsForMaml completed runs total.
        var runsBySymbol = await readDb.Set<MLTrainingRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == RunStatus.Completed
                     && r.MLModelId != null
                     && !r.IsDistillationRun
                     && !r.IsMamlRun)
            .GroupBy(r => r.Symbol)
            .Select(g => new { Symbol = g.Key, RunCount = g.Count() })
            .ToListAsync(ct);

        // Filter to symbols with enough completed runs.
        var qualifiedSymbols = runsBySymbol
            .Where(s => s.RunCount >= MinSymbolsForMaml)
            .Select(s => s.Symbol)
            .ToList();

        if (qualifiedSymbols.Count < MinSymbolsForMaml)
        {
            _logger.LogDebug("MLAverageWeightInitWorker: insufficient qualified symbols ({N}), need {Min}.",
                qualifiedSymbols.Count, MinSymbolsForMaml);
            return;
        }

        // Load the most recent successful model per symbol (the one with weights we want to average).
        var latestModels = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => !m.IsDeleted
                     && m.ModelBytes != null
                     && !m.IsMamlInitializer
                     && !m.IsMetaLearner
                     && !m.IsSoupModel
                     && qualifiedSymbols.Contains(m.Symbol))
            .OrderByDescending(m => m.TrainedAt)
            .ToListAsync(ct);

        // Deduplicate: keep only the most recent model per (symbol, timeframe).
        var uniqueModels = latestModels
            .GroupBy(m => new { m.Symbol, m.Timeframe })
            .Select(g => g.First())
            .ToList();

        // Group by timeframe — we produce one MAML initialiser per timeframe.
        var byTimeframe = uniqueModels.GroupBy(m => m.Timeframe).ToList();

        foreach (var tfGroup in byTimeframe)
        {
            try
            {
                var timeframe = tfGroup.Key;
                var modelsForTf = tfGroup.ToList();

                if (modelsForTf.Count < MinSymbolsForMaml) continue;

                // Deserialise all snapshots.
                var snapshots = new List<ModelSnapshot>();
                foreach (var model in modelsForTf)
                {
                    try
                    {
                        var snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);
                        if (snap?.Weights is { Length: > 0 } && snap.Biases is { Length: > 0 })
                            snapshots.Add(snap);
                    }
                    catch { /* skip malformed snapshots */ }
                }

                if (snapshots.Count < MinSymbolsForMaml) continue;

                // Use the first snapshot for dimensional reference.
                var refSnap = snapshots[0];
                int K = refSnap.Weights.Length;
                int F = refSnap.Weights[0].Length;

                // Compute weight initialisation by averaging weights across ALL symbols.
                // The averaged weights represent a good starting point from which any
                // symbol can fine-tune quickly.
                var metaWeights = new double[K][];
                var metaBiases  = new double[K];
                for (int k = 0; k < K; k++) metaWeights[k] = new double[F];

                int N = snapshots.Count;
                foreach (var snap in snapshots)
                {
                    for (int k = 0; k < Math.Min(K, snap.Weights.Length); k++)
                    {
                        for (int f = 0; f < Math.Min(F, snap.Weights[k].Length); f++)
                            metaWeights[k][f] += snap.Weights[k][f] / N;

                        if (k < snap.Biases.Length)
                            metaBiases[k] += snap.Biases[k] / N;
                    }
                }

                // Average standardisation parameters too, so the initialiser comes with
                // reasonable feature normalisation out of the box.
                float[] avgMeans = AverageFloatArrays(snapshots.Select(s => s.Means).ToList(), refSnap.Means.Length);
                float[] avgStds  = AverageFloatArrays(snapshots.Select(s => s.Stds).ToList(), refSnap.Stds.Length);

                var metaSnap = new ModelSnapshot
                {
                    Type          = "AvgWeightInit",
                    Version       = $"avgwi-{N}sym-{DateTime.UtcNow:yyyyMMdd}",
                    Features      = refSnap.Features,
                    Means         = avgMeans,
                    Stds          = avgStds,
                    BaseLearnersK = K,
                    Weights       = metaWeights,
                    Biases        = metaBiases,
                    TrainedOn     = DateTime.UtcNow,
                    TrainSamples  = N // number of symbol models used
                };

                // Deactivate all previous average-weight initialisers for this timeframe.
                var prevMaml = await writeDb.Set<MLModel>()
                    .Where(m => m.IsMamlInitializer && !m.IsDeleted && m.Timeframe == timeframe)
                    .ToListAsync(ct);

                foreach (var prev in prevMaml)
                {
                    prev.IsActive = false;
                    prev.IsMamlInitializer = false;
                    prev.Status = MLModelStatus.Superseded;
                }

                // Create the new average-weight initialiser model. Symbol="ALL" signals cross-symbol scope.
                var mamlModel = new MLModel
                {
                    Symbol              = "ALL",
                    Timeframe           = timeframe,
                    ModelVersion        = $"AvgWeightInit-{timeframe}-{DateTime.UtcNow:yyyyMMdd}",
                    Status              = MLModelStatus.Active,
                    IsActive            = true,
                    IsMamlInitializer   = true,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    ModelBytes          = JsonSerializer.SerializeToUtf8Bytes(metaSnap),
                    TrainedAt           = DateTime.UtcNow,
                    TrainingSamples     = N
                };

                writeDb.Set<MLModel>().Add(mamlModel);

                // Insert an audit training run record for traceability.
                writeDb.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol       = "ALL",
                    Timeframe    = timeframe,
                    TriggerType  = TriggerType.Scheduled,
                    Status       = RunStatus.Completed,
                    FromDate     = DateTime.UtcNow.AddDays(-365),
                    ToDate       = DateTime.UtcNow,
                    TotalSamples = N,
                    IsMamlRun    = true,
                    MamlInnerSteps = 0, // pure averaging, no inner-loop SGD in this implementation
                    CompletedAt  = DateTime.UtcNow
                });

                await writeDb.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "MLAverageWeightInitWorker created average-weight initialiser for {Timeframe} from {N} symbol models.",
                    timeframe, N);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Average weight initialization failed for timeframe {Timeframe}.", tfGroup.Key);
            }
        }
    }

    /// <summary>
    /// Averages a list of float arrays element-wise. Arrays shorter than
    /// <paramref name="length"/> are zero-padded.
    /// </summary>
    private static float[] AverageFloatArrays(List<float[]> arrays, int length)
    {
        if (length == 0 || arrays.Count == 0) return [];

        var result = new float[length];
        int n = arrays.Count;

        foreach (var arr in arrays)
        {
            for (int i = 0; i < Math.Min(length, arr.Length); i++)
                result[i] += arr[i] / n;
        }

        return result;
    }

    private static async Task<T> GetConfigAsync<T>(
        DbContext ctx, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
