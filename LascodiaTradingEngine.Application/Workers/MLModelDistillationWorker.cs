using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Triggers knowledge-distillation training runs when the inference latency of the
/// active ensemble model consistently breaches the P99 threshold configured in EngineConfig.
/// </summary>
/// <remarks>
/// Knowledge distillation trains a compact student model to mimic the ensemble teacher's
/// soft probability outputs (temperature-scaled). The student has K=1 (or a small K) and
/// converges faster because it learns from soft labels rather than hard 0/1 outcomes.
///
/// Flow:
/// 1. Read rolling P99 latency from the most recent <c>MLInferenceLatencyWorker</c> output.
/// 2. When P99 > threshold for 3 consecutive cycles, queue a distillation run.
/// 3. The training run has <c>IsDistillationRun = true</c>; <c>MLTrainingWorker</c> detects
///    this flag and routes the run to a single-learner trainer using the teacher's soft labels.
/// 4. On promotion, the produced model has <c>IsDistilled = true</c> and
///    <c>DistilledFromModelId</c> pointing at the teacher.
/// </remarks>
public class MLModelDistillationWorker : BackgroundService
{
    private readonly ILogger<MLModelDistillationWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private static readonly TimeSpan _interval      = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _initialDelay  = TimeSpan.FromMinutes(8);

    /// <summary>P99 latency in milliseconds above which distillation is triggered.</summary>
    private const int LatencyThresholdMs = 200;

    /// <summary>Number of consecutive high-latency cycles before triggering distillation.</summary>
    private const int ConsecutiveCyclesThreshold = 3;

    // Track consecutive breaches per model (in-process state; resets on restart)
    private readonly Dictionary<long, int> _breachCounts = new();

    public MLModelDistillationWorker(
        ILogger<MLModelDistillationWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLModelDistillationWorker starting");
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "MLModelDistillationWorker cycle failed"); }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb       = readCtx.GetDbContext();
        var writeDb      = writeCtx.GetDbContext();

        // Find active models with recent high-latency prediction logs
        var latencyBreaches = await readDb.Set<MLModelPredictionLog>()
            .Where(p => !p.IsDeleted
                     && p.LatencyMs != null
                     && p.PredictedAt >= DateTime.UtcNow.AddHours(-2))
            .GroupBy(p => p.MLModelId)
            .Select(g => new
            {
                ModelId  = g.Key,
                P99Ms    = g.OrderByDescending(p => p.LatencyMs).Skip((int)(g.Count() * 0.01)).FirstOrDefault()!.LatencyMs ?? 0
            })
            .Where(x => x.P99Ms > LatencyThresholdMs)
            .ToListAsync(ct);

        foreach (var breach in latencyBreaches)
        {
            _breachCounts.TryGetValue(breach.ModelId, out int count);
            _breachCounts[breach.ModelId] = count + 1;

            if (_breachCounts[breach.ModelId] < ConsecutiveCyclesThreshold) continue;

            // Check if distillation run already queued or distilled model already active
            bool alreadyQueued = await writeDb.Set<MLTrainingRun>()
                .AnyAsync(r => r.IsDistillationRun
                            && r.Status != RunStatus.Completed
                            && r.Status != RunStatus.Failed
                            && !r.IsDeleted, ct);

            if (alreadyQueued) continue;

            var teacher = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == breach.ModelId && m.IsActive && !m.IsDeleted, ct);

            if (teacher == null) continue;

            // Queue a distillation training run
            var distillRun = new MLTrainingRun
            {
                Symbol              = teacher.Symbol,
                Timeframe           = teacher.Timeframe,
                TriggerType         = TriggerType.Scheduled,
                Status              = RunStatus.Queued,
                FromDate            = DateTime.UtcNow.AddDays(-180),
                ToDate              = DateTime.UtcNow,
                IsDistillationRun   = true,
                LearnerArchitecture = teacher.LearnerArchitecture,
                HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    TeacherModelId = teacher.Id,
                    K              = 1,  // single lightweight student
                    MaxEpochs      = 30,
                })
            };

            writeDb.Set<MLTrainingRun>().Add(distillRun);
            await writeDb.SaveChangesAsync(ct);

            _breachCounts[breach.ModelId] = 0; // reset counter

            _logger.LogInformation(
                "Distillation run queued for {Symbol}/{Timeframe} — P99 latency breached {Threshold}ms",
                teacher.Symbol, teacher.Timeframe, LatencyThresholdMs);
        }
    }
}
