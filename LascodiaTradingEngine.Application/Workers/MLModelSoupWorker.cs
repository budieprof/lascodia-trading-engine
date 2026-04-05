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
/// Creates model soups by averaging the weights of multiple successful training runs
/// for the same symbol/timeframe pair (Rec #44). Model soups occupy a flatter loss
/// basin and tend to generalise better than any individual checkpoint.
/// </summary>
/// <remarks>
/// Runs every 24 hours (configurable via <c>MLSoup:PollIntervalSeconds</c>).
///
/// For each symbol/timeframe pair with >= 3 completed training runs in the last 30 days
/// that ALL passed quality gates (i.e. produced an <see cref="MLModel"/>), the worker:
/// 1. Loads <c>ModelBytes</c> from each successful run's model.
/// 2. Deserialises each <see cref="ModelSnapshot"/> and averages the weight arrays
///    element-wise (uniform 1/N averaging).
/// 3. Creates a new <see cref="MLModel"/> with <c>IsSoupModel = true</c>.
/// 4. Creates a shadow evaluation to compare the soup against the current champion.
/// </remarks>
public sealed class MLModelSoupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLModelSoupWorker> _logger;

    private const int    DefaultPollIntervalSec = 86400; // 24 hours
    private const string CK_PollInterval        = "MLSoup:PollIntervalSeconds";
    private const int    MinRunsForSoup         = 3;
    private const int    LookbackDays           = 30;

    public MLModelSoupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLModelSoupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLModelSoupWorker started.");

        // Stagger startup to avoid contention with training workers.
        await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = DefaultPollIntervalSec;
            try
            {
                using var scope = _scopeFactory.CreateAsyncScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var readDb  = readCtx.GetDbContext();
                pollSecs = await GetConfigAsync<int>(readDb, CK_PollInterval, DefaultPollIntervalSec, stoppingToken);

                await RunCycleAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MLModelSoupWorker cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }
    }

    private async Task RunCycleAsync(IServiceProvider sp, CancellationToken ct)
    {
        var readCtx  = sp.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = sp.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        DateTime lookbackCutoff = DateTime.UtcNow.AddDays(-LookbackDays);

        // Find completed training runs from the last 30 days that produced a model
        // (MLModelId != null means the run passed quality gates and created a model).
        var recentRuns = await readDb.Set<MLTrainingRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == RunStatus.Completed
                     && r.MLModelId != null
                     && r.CompletedAt != null
                     && r.CompletedAt >= lookbackCutoff
                     && !r.IsDistillationRun
                     && !r.IsMamlRun)
            .Select(r => new
            {
                r.Symbol,
                r.Timeframe,
                r.MLModelId
            })
            .ToListAsync(ct);

        // Group by symbol/timeframe and filter for pairs with >= MinRunsForSoup.
        var candidates = recentRuns
            .GroupBy(r => new { r.Symbol, r.Timeframe })
            .Where(g => g.Count() >= MinRunsForSoup)
            .ToList();

        foreach (var group in candidates)
        {
            try
            {
                var symbol    = group.Key.Symbol;
                var timeframe = group.Key.Timeframe;
                var modelIds  = group.Select(r => r.MLModelId!.Value).Distinct().ToList();

                // Check if a soup model already exists for this symbol/timeframe in the
                // current lookback window (avoid creating duplicate soups).
                bool soupExists = await readDb.Set<MLModel>()
                    .AnyAsync(m => m.IsSoupModel
                               && m.Symbol == symbol
                               && m.Timeframe == timeframe
                               && !m.IsDeleted
                               && m.TrainedAt >= lookbackCutoff, ct);

                if (soupExists) continue;

                // Load ModelBytes from each successful run's model.
                var models = await readDb.Set<MLModel>()
                    .AsNoTracking()
                    .Where(m => modelIds.Contains(m.Id)
                             && !m.IsDeleted
                             && m.ModelBytes != null)
                    .Select(m => new { m.Id, m.ModelBytes })
                    .ToListAsync(ct);

                if (models.Count < MinRunsForSoup) continue;

                // Deserialise each snapshot.
                var allSnapshots = new List<(ModelSnapshot Snap, string Arch)>();
                foreach (var m in models)
                {
                    try
                    {
                        var snap = JsonSerializer.Deserialize<ModelSnapshot>(m.ModelBytes!);
                        if (snap?.Weights is { Length: > 0 } && snap.Biases is { Length: > 0 })
                            allSnapshots.Add((snap, snap.Type ?? "Unknown"));
                    }
                    catch
                    {
                        // Skip malformed snapshots.
                    }
                }

                // Architecture guard: only soup models that share the same architecture.
                // Averaging weights from e.g. a GBM and a BaggedLogistic would produce garbage.
                var archGroups = allSnapshots
                    .GroupBy(s => s.Arch)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                var bestGroup = archGroups.FirstOrDefault();
                if (bestGroup == null || bestGroup.Count() < MinRunsForSoup)
                {
                    _logger.LogDebug(
                        "Soup: {Symbol}/{Tf} — largest architecture group has {N} models (need {Min}), skipping.",
                        symbol, timeframe, bestGroup?.Count() ?? 0, MinRunsForSoup);
                    continue;
                }

                var snapshots = bestGroup.Select(s => s.Snap).ToList();
                var selectedArch = bestGroup.Key;
                int totalDeserialized = allSnapshots.Count;

                _logger.LogInformation(
                    "Soup: {Symbol}/{Tf} — using {N} {Arch} models (filtered from {Total} total)",
                    symbol, timeframe, snapshots.Count, selectedArch, totalDeserialized);

                // Use the first snapshot as the dimensional reference.
                var refSnap = snapshots[0];
                int K = refSnap.Weights.Length;
                int F = refSnap.Weights[0].Length;

                // Average the weight arrays element-wise (uniform 1/N averaging).
                var avgWeights = new double[K][];
                var avgBiases  = new double[K];
                for (int k = 0; k < K; k++) avgWeights[k] = new double[F];

                int N = snapshots.Count;
                foreach (var snap in snapshots)
                {
                    for (int k = 0; k < Math.Min(K, snap.Weights.Length); k++)
                    {
                        for (int f = 0; f < Math.Min(F, snap.Weights[k].Length); f++)
                            avgWeights[k][f] += snap.Weights[k][f] / N;

                        if (k < snap.Biases.Length)
                            avgBiases[k] += snap.Biases[k] / N;
                    }
                }

                // Also average Means and Stds if present for consistent standardisation.
                float[] avgMeans = AverageFloatArrays(snapshots.Select(s => s.Means).ToList(), refSnap.Means.Length);
                float[] avgStds  = AverageFloatArrays(snapshots.Select(s => s.Stds).ToList(), refSnap.Stds.Length);

                // Build the soup snapshot.
                var soupSnap = new ModelSnapshot
                {
                    Type          = "Soup",
                    Version       = $"soup-{N}x-{DateTime.UtcNow:yyyyMMdd}",
                    Features      = refSnap.Features,
                    Means         = avgMeans,
                    Stds          = avgStds,
                    BaseLearnersK = K,
                    Weights       = avgWeights,
                    Biases        = avgBiases,
                    MagWeights    = refSnap.MagWeights,
                    MagBias       = refSnap.MagBias,
                    PlattA        = snapshots.Average(s => s.PlattA),
                    PlattB        = snapshots.Average(s => s.PlattB),
                    TrainSamples  = snapshots.Sum(s => s.TrainSamples),
                    TestSamples   = refSnap.TestSamples,
                    TrainedOn     = DateTime.UtcNow,
                    OptimalThreshold = snapshots.Average(s => s.OptimalThreshold)
                };

                var soupBytes = JsonSerializer.SerializeToUtf8Bytes(soupSnap);

                // Create the soup MLModel. It is NOT activated — it goes through shadow evaluation first.
                var soupModel = new MLModel
                {
                    Symbol              = symbol,
                    Timeframe           = timeframe,
                    ModelVersion        = $"soup-{N}x-{DateTime.UtcNow:yyyyMMdd}",
                    Status              = MLModelStatus.Training, // shadow eval will decide promotion
                    IsActive            = false,
                    IsSoupModel         = true,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    ModelBytes          = soupBytes,
                    TrainedAt           = DateTime.UtcNow,
                    TrainingSamples     = soupSnap.TrainSamples
                };

                writeDb.Set<MLModel>().Add(soupModel);
                await writeDb.SaveChangesAsync(ct);

                // Find the current champion for this symbol/timeframe to create a shadow evaluation.
                var champion = await readDb.Set<MLModel>()
                    .AsNoTracking()
                    .Where(m => m.IsActive && !m.IsDeleted
                             && m.Symbol == symbol
                             && m.Timeframe == timeframe)
                    .Select(m => new { m.Id })
                    .FirstOrDefaultAsync(ct);

                if (champion != null)
                {
                    var shadowEval = new MLShadowEvaluation
                    {
                        ChampionModelId  = champion.Id,
                        ChallengerModelId = soupModel.Id,
                        Symbol           = symbol,
                        Timeframe        = timeframe,
                        Status           = ShadowEvaluationStatus.Running,
                        StartedAt        = DateTime.UtcNow
                    };

                    writeDb.Set<MLShadowEvaluation>().Add(shadowEval);
                    await writeDb.SaveChangesAsync(ct);
                }

                _logger.LogInformation(
                    "MLModelSoupWorker created soup model {Id} for {Symbol}/{Timeframe} from {N} checkpoints.",
                    soupModel.Id, symbol, timeframe, N);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Model soup creation failed for {Symbol}/{Timeframe}.",
                    group.Key.Symbol, group.Key.Timeframe);
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
