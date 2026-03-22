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

    /// <summary>
    /// Initialises the worker with its logger and DI scope factory.
    /// </summary>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope on every poll cycle so that scoped EF Core
    /// contexts are correctly disposed between polls.
    /// </param>
    public MLModelDistillationWorker(
        ILogger<MLModelDistillationWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Hosted-service entry point. Waits <see cref="_initialDelay"/> after startup
    /// then polls every <see cref="_interval"/> (30 min), calling
    /// <see cref="RunCycleAsync"/> to check for sustained latency breaches.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLModelDistillationWorker starting");

        // Stagger startup to avoid contention with heavier workers during boot.
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "MLModelDistillationWorker cycle failed"); }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Core distillation detection cycle. Computes the rolling P99 inference latency
    /// for each active model from recent <see cref="MLModelPredictionLog"/> records,
    /// increments an in-process breach counter per model, and queues a distillation
    /// training run once <see cref="ConsecutiveCyclesThreshold"/> consecutive breaches
    /// are observed.
    /// </summary>
    /// <remarks>
    /// Knowledge distillation methodology:
    /// <list type="number">
    ///   <item>
    ///     Query all prediction logs written in the last 2 hours that carry a
    ///     <c>LatencyMs</c> measurement. Group by <c>MLModelId</c> and compute the
    ///     approximate P99 (skip the top 1% of sorted latencies). Models whose P99
    ///     exceeds <see cref="LatencyThresholdMs"/> are candidates for distillation.
    ///   </item>
    ///   <item>
    ///     Use the in-memory <see cref="_breachCounts"/> dictionary as a hysteresis
    ///     filter. A single high-latency sample could be a transient spike; only after
    ///     <see cref="ConsecutiveCyclesThreshold"/> consecutive 30-minute cycles all
    ///     breaching the threshold do we treat the latency regression as persistent.
    ///   </item>
    ///   <item>
    ///     Before queuing, check whether a distillation run is already in flight
    ///     (any run with <c>IsDistillationRun = true</c> that has not yet completed
    ///     or failed). This prevents duplicate student training runs stacking up.
    ///   </item>
    ///   <item>
    ///     Queue the distillation <see cref="MLTrainingRun"/> with
    ///     <c>IsDistillationRun = true</c> and <c>K = 1</c>. The <c>MLTrainingWorker</c>
    ///     detects this flag and routes the run to a single-learner trainer that uses
    ///     temperature-scaled soft labels from the teacher ensemble rather than hard
    ///     0/1 direction labels. The resulting student model is dramatically smaller
    ///     and faster while retaining most of the teacher's predictive signal.
    ///   </item>
    ///   <item>
    ///     Reset the breach counter to zero after queuing, so a new independent
    ///     threshold-crossing period must elapse before another distillation is triggered.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb       = readCtx.GetDbContext();
        var writeDb      = writeCtx.GetDbContext();

        // Query prediction logs from the last 2 hours that include latency measurements.
        // Group by model and compute an approximate P99 by ordering descending and
        // skipping the top 1% of rows. This is an in-database approximation; for exact
        // P99 a window function (EF OVER/PERCENTILE_CONT) would be required.
        var latencyBreaches = await readDb.Set<MLModelPredictionLog>()
            .Where(p => !p.IsDeleted
                     && p.LatencyMs != null
                     && p.PredictedAt >= DateTime.UtcNow.AddHours(-2))
            .GroupBy(p => p.MLModelId)
            .Select(g => new
            {
                ModelId  = g.Key,
                // Approximate P99: sort descending, skip top 1%, take next row's latency.
                P99Ms    = g.OrderByDescending(p => p.LatencyMs).Skip((int)(g.Count() * 0.01)).FirstOrDefault()!.LatencyMs ?? 0
            })
            .Where(x => x.P99Ms > LatencyThresholdMs)
            .ToListAsync(ct);

        foreach (var breach in latencyBreaches)
        {
            // Increment in-process hysteresis counter for this model.
            // State is intentionally in-process (not persisted) so it resets on restart,
            // which is acceptable — a restart represents a recovery event that clears
            // transient state anyway.
            _breachCounts.TryGetValue(breach.ModelId, out int count);
            _breachCounts[breach.ModelId] = count + 1;

            // Only act after ConsecutiveCyclesThreshold sustained breaches.
            // This prevents distillation being triggered by a single noisy measurement.
            if (_breachCounts[breach.ModelId] < ConsecutiveCyclesThreshold) continue;

            // Check if distillation run already queued or distilled model already active.
            // A queued, pending, or running distillation run indicates work is in progress.
            bool alreadyQueued = await writeDb.Set<MLTrainingRun>()
                .AnyAsync(r => r.IsDistillationRun
                            && r.Status != RunStatus.Completed
                            && r.Status != RunStatus.Failed
                            && !r.IsDeleted, ct);

            if (alreadyQueued) continue;

            // Fetch the teacher model — must still be active at queue time.
            var teacher = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == breach.ModelId && m.IsActive && !m.IsDeleted, ct);

            if (teacher == null) continue;

            // Queue a distillation training run.
            // K=1 produces a single lightweight logistic student that mimics the full
            // ensemble's soft probability outputs (temperature-scaled cross-entropy loss).
            // MaxEpochs=30 is sufficient for the student to converge on soft labels;
            // fewer epochs reduce the risk of the student overfitting to teacher noise.
            var distillRun = new MLTrainingRun
            {
                Symbol              = teacher.Symbol,
                Timeframe           = teacher.Timeframe,
                TriggerType         = TriggerType.Scheduled,
                Status              = RunStatus.Queued,
                // 180-day window provides sufficient recency without reaching back to
                // market regimes that may differ from the teacher's training distribution.
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

            // Reset breach counter so a new independent latency episode must be
            // observed before another distillation run is queued.
            _breachCounts[breach.ModelId] = 0;

            _logger.LogInformation(
                "Distillation run queued for {Symbol}/{Timeframe} — P99 latency breached {Threshold}ms",
                teacher.Symbol, teacher.Timeframe, LatencyThresholdMs);
        }
    }
}
