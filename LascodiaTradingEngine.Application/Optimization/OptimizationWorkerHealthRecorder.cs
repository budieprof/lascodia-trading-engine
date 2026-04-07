using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class OptimizationWorkerHealthRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IOptimizationWorkerHealthStore _optimizationHealthStore;

    public OptimizationWorkerHealthRecorder(
        IServiceScopeFactory scopeFactory,
        IOptimizationWorkerHealthStore optimizationHealthStore,
        IWorkerHealthMonitor? healthMonitor)
    {
        _scopeFactory = scopeFactory;
        _optimizationHealthStore = optimizationHealthStore;
        _healthMonitor = healthMonitor;
    }

    internal async Task RecordAsync(
        OptimizationConfig config,
        DateTime lastConfigRefreshUtc,
        DateTime nextConfigRefreshUtc,
        CancellationToken ct)
    {
        if (_healthMonitor is null)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeCtx.GetDbContext();

        int queuedRuns = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted && r.Status == OptimizationRunStatus.Queued, ct);
        int runningRuns = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted && r.Status == OptimizationRunStatus.Running, ct);
        int retryableFailedRuns = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted
                          && r.Status == OptimizationRunStatus.Failed
                          && r.RetryCount < Math.Max(0, config.MaxRetryAttempts), ct);
        int abandonedRuns = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted && r.Status == OptimizationRunStatus.Abandoned, ct);
        int pendingFollowUps = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted
                          && r.Status == OptimizationRunStatus.Approved
                          && (r.ValidationFollowUpStatus == ValidationFollowUpStatus.Pending
                              || r.ValidationFollowUpStatus == null), ct);
        int pendingCompletionPublications = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted
                          && (r.Status == OptimizationRunStatus.Completed
                           || r.Status == OptimizationRunStatus.Approved
                           || r.Status == OptimizationRunStatus.Rejected)
                          && r.CompletionPublicationPayloadJson != null
                          && (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Pending
                           || r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Failed), ct);
        var oldestRunningRun = await writeDb.Set<OptimizationRun>()
            .Where(r => !r.IsDeleted && r.Status == OptimizationRunStatus.Running)
            .OrderBy(r => r.ExecutionStageUpdatedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt)
            .Select(r => new
            {
                r.Id,
                r.ExecutionStage,
                r.ExecutionStageMessage,
                r.ExecutionStageUpdatedAt
            })
            .FirstOrDefaultAsync(ct);

        _healthMonitor.RecordBacklogDepth("OptimizationWorker", queuedRuns);
        _optimizationHealthStore.UpdateMainWorkerState(new OptimizationWorkerHealthStateSnapshot
        {
            QueuedRuns = queuedRuns,
            RunningRuns = runningRuns,
            RetryableFailedRuns = retryableFailedRuns,
            AbandonedRuns = abandonedRuns,
            PendingFollowUps = pendingFollowUps,
            PendingCompletionPublications = pendingCompletionPublications,
            ConfigCacheAgeSeconds = lastConfigRefreshUtc == DateTime.MinValue
                ? 0
                : Math.Max(0, (int)(DateTime.UtcNow - lastConfigRefreshUtc).TotalSeconds),
            ConfigRefreshDueAtUtc = nextConfigRefreshUtc,
            ConfigRefreshIntervalSeconds = Math.Clamp(
                config.SchedulePollSeconds,
                30,
                30),
            OldestRunningRunId = oldestRunningRun?.Id,
            OldestRunningStage = oldestRunningRun?.ExecutionStage,
            OldestRunningStageMessage = oldestRunningRun?.ExecutionStageMessage,
            OldestRunningStageUpdatedAt = oldestRunningRun?.ExecutionStageUpdatedAt,
        });
        _healthMonitor.RecordWorkerMetadata("OptimizationWorker", null, TimeSpan.FromSeconds(30));
    }
}
