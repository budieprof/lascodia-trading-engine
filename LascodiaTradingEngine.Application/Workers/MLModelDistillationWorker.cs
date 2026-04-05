using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Distills large ensemble models into smaller, faster BaggedLogistic models with K=3
/// when inference latency exceeds a configurable threshold. The resulting student model
/// is trained by <c>MLTrainingWorker</c> using soft labels from the teacher ensemble.
/// </summary>
/// <remarks>
/// Flow:
/// 1. Polls every 6 hours (configurable via <c>MLDistillation:PollIntervalSeconds</c>).
/// 2. Finds active models where <c>LearnerArchitecture != BaggedLogistic</c> AND the
///    rolling P99 inference latency from recent <see cref="MLModelPredictionLog"/> records
///    exceeds the threshold.
/// 3. Queues an <see cref="MLTrainingRun"/> with <c>IsDistillationRun = true</c>,
///    targeting BaggedLogistic with K=3 (small ensemble).
/// 4. The training worker detects the distillation flag and produces a student model
///    with <c>IsDistilled = true</c> and <c>DistilledFromModelId</c> set to the teacher.
/// </remarks>
public sealed class MLModelDistillationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLModelDistillationWorker> _logger;

    /// <summary>Default poll interval (6 hours); overridable via EngineConfig key.</summary>
    private const int DefaultPollIntervalSec = 21600;
    private const string CK_PollInterval      = "MLDistillation:PollIntervalSeconds";
    private const string CK_LatencyThresholdMs = "MLDistillation:LatencyThresholdMs";

    /// <summary>Default P99 latency threshold in milliseconds above which distillation is triggered.</summary>
    private const int DefaultLatencyThresholdMs = 200;

    /// <summary>Student ensemble size — small enough for low latency while preserving diversity.</summary>
    private const int StudentK = 3;

    public MLModelDistillationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLModelDistillationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLModelDistillationWorker started.");

        // Stagger startup to avoid contention with heavier workers.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

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
                _logger.LogError(ex, "MLModelDistillationWorker cycle failed.");
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

        int latencyThreshold = await GetConfigAsync<int>(readDb, CK_LatencyThresholdMs, DefaultLatencyThresholdMs, ct);

        // Find active models that are NOT already BaggedLogistic (distillation targets
        // complex architectures that have higher inference cost).
        var candidates = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted
                     && m.LearnerArchitecture != LearnerArchitecture.BaggedLogistic)
            .Select(m => new { m.Id, m.Symbol, m.Timeframe, m.LearnerArchitecture })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        // Compute rolling P99 latency from recent prediction logs (last 6 hours).
        var recentPredictions = await readDb.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(p => !p.IsDeleted
                     && p.LatencyMs != null
                     && p.PredictedAt >= DateTime.UtcNow.AddHours(-6))
            .Select(p => new { p.MLModelId, p.LatencyMs })
            .ToListAsync(ct);

        var latencyByModel = recentPredictions
            .GroupBy(p => p.MLModelId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var sorted = g.OrderBy(p => p.LatencyMs).ToList();
                    int p99Idx = Math.Max(0, (int)(sorted.Count * 0.99) - 1);
                    return sorted[p99Idx].LatencyMs ?? 0;
                });

        foreach (var candidate in candidates)
        {
            try
            {
                // Check if this model's P99 latency exceeds the threshold.
                if (!latencyByModel.TryGetValue(candidate.Id, out var p99Ms)) continue;
                if (p99Ms <= latencyThreshold) continue;

                // Check if a distillation run is already in flight for this symbol/timeframe.
                bool alreadyQueued = await writeDb.Set<MLTrainingRun>()
                    .AnyAsync(r => r.IsDistillationRun
                                && r.Symbol == candidate.Symbol
                                && r.Timeframe == candidate.Timeframe
                                && r.Status != RunStatus.Completed
                                && r.Status != RunStatus.Failed
                                && !r.IsDeleted, ct);

                if (alreadyQueued) continue;

                // Queue a distillation training run targeting BaggedLogistic with K=3.
                var distillRun = new MLTrainingRun
                {
                    Symbol              = candidate.Symbol,
                    Timeframe           = candidate.Timeframe,
                    TriggerType         = TriggerType.Scheduled,
                    Status              = RunStatus.Queued,
                    FromDate            = DateTime.UtcNow.AddDays(-180),
                    ToDate              = DateTime.UtcNow,
                    IsDistillationRun   = true,
                    LearnerArchitecture = LearnerArchitecture.BaggedLogistic,
                    HyperparamConfigJson = JsonSerializer.Serialize(new
                    {
                        TeacherModelId = candidate.Id,
                        K              = StudentK,
                        MaxEpochs      = 30,
                    })
                };

                writeDb.Set<MLTrainingRun>().Add(distillRun);
                await writeDb.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Distillation run queued for {Symbol}/{Timeframe} (P99={P99}ms > {Threshold}ms, arch={Arch}).",
                    candidate.Symbol, candidate.Timeframe, p99Ms, latencyThreshold, candidate.LearnerArchitecture);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Distillation check failed for model {Id}.", candidate.Id);
            }
        }
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
