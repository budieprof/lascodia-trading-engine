using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationWorkerHealthRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IOptimizationWorkerHealthStore _optimizationHealthStore;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationWorkerHealthRecorder(
        IServiceScopeFactory scopeFactory,
        IOptimizationWorkerHealthStore optimizationHealthStore,
        IWorkerHealthMonitor? healthMonitor,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _optimizationHealthStore = optimizationHealthStore;
        _healthMonitor = healthMonitor;
        _timeProvider = timeProvider;
    }

    internal async Task RecordAsync(
        OptimizationConfig config,
        DateTime lastConfigRefreshUtc,
        DateTime nextConfigRefreshUtc,
        OptimizationRunRecoveryCoordinator.LifecycleReconciliationSummary? reconciliationSummary,
        CancellationToken ct)
    {
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
        int approvedRunsMissingFollowUps = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted
                          && r.Status == OptimizationRunStatus.Approved
                          && (r.ValidationFollowUpsCreatedAt == null
                              || r.ValidationFollowUpStatus == null), ct);
        int pendingCompletionPreparation = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted
                          && (r.Status == OptimizationRunStatus.Completed
                           || r.Status == OptimizationRunStatus.Approved
                           || r.Status == OptimizationRunStatus.Rejected)
                          && r.ResultsPersistedAt != null
                          && r.CompletionPublicationPayloadJson == null, ct);
        int pendingCompletionPublications = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted
                          && (r.Status == OptimizationRunStatus.Completed
                           || r.Status == OptimizationRunStatus.Approved
                           || r.Status == OptimizationRunStatus.Rejected)
                          && r.CompletionPublicationPayloadJson != null
                          && (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Pending
                           || r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Failed), ct);
        int strandedLifecycleRuns = await writeDb.Set<OptimizationRun>()
            .CountAsync(r => !r.IsDeleted
                          && (((r.Status == OptimizationRunStatus.Completed
                             || r.Status == OptimizationRunStatus.Approved
                             || r.Status == OptimizationRunStatus.Rejected)
                            && (r.LifecycleReconciledAt == null
                             || (r.ResultsPersistedAt != null && r.CompletionPublicationPayloadJson == null)
                             || (r.CompletionPublicationPayloadJson != null && r.CompletionPublicationStatus == null)
                             || r.CompletionPublicationErrorMessage != null
                             || (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Published && r.CompletionPublicationCompletedAt == null)))
                           || (r.Status == OptimizationRunStatus.Approved
                               && (r.ValidationFollowUpsCreatedAt == null || r.ValidationFollowUpStatus == null || r.BestParametersJson == null))
                           || (r.Status == OptimizationRunStatus.Rejected
                               && (r.ValidationFollowUpsCreatedAt != null || r.ValidationFollowUpStatus != null))), ct);
        var oldestStrandedLifecycleRun = await writeDb.Set<OptimizationRun>()
            .Where(r => !r.IsDeleted
                     && (((r.Status == OptimizationRunStatus.Completed
                        || r.Status == OptimizationRunStatus.Approved
                        || r.Status == OptimizationRunStatus.Rejected)
                       && (r.LifecycleReconciledAt == null
                        || (r.ResultsPersistedAt != null && r.CompletionPublicationPayloadJson == null)
                        || (r.CompletionPublicationPayloadJson != null && r.CompletionPublicationStatus == null)
                        || r.CompletionPublicationErrorMessage != null
                        || (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Published && r.CompletionPublicationCompletedAt == null)))
                      || (r.Status == OptimizationRunStatus.Approved
                          && (r.ValidationFollowUpsCreatedAt == null || r.ValidationFollowUpStatus == null || r.BestParametersJson == null))
                      || (r.Status == OptimizationRunStatus.Rejected
                          && (r.ValidationFollowUpsCreatedAt != null || r.ValidationFollowUpStatus != null))))
            .OrderBy(r => r.CompletedAt ?? r.ApprovedAt ?? r.ResultsPersistedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt)
            .Select(r => new
            {
                r.Id,
                r.Status,
                AnchorAtUtc = r.CompletedAt ?? r.ApprovedAt ?? r.ResultsPersistedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt
            })
            .FirstOrDefaultAsync(ct);
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
        var oldestQueuedRun = await writeDb.Set<OptimizationRun>()
            .Where(r => !r.IsDeleted && r.Status == OptimizationRunStatus.Queued)
            .OrderBy(r => r.QueuedAt == default ? r.StartedAt : r.QueuedAt)
            .Select(r => new
            {
                r.Id,
                QueuedAtUtc = r.QueuedAt == default ? r.StartedAt : r.QueuedAt
            })
            .FirstOrDefaultAsync(ct);

        _healthMonitor?.RecordBacklogDepth(OptimizationWorkerHealthNames.CoordinatorWorker, queuedRuns);
        _optimizationHealthStore.UpdateMainWorkerState(current => current with
        {
            QueuedRuns = queuedRuns,
            RunningRuns = runningRuns,
            RetryableFailedRuns = retryableFailedRuns,
            AbandonedRuns = abandonedRuns,
            PendingFollowUps = pendingFollowUps,
            PendingCompletionPublications = pendingCompletionPublications,
            ApprovedRunsMissingFollowUps = approvedRunsMissingFollowUps,
            PendingCompletionPreparation = pendingCompletionPreparation,
            StrandedLifecycleRuns = strandedLifecycleRuns,
            LifecycleRepairsLastCycle = reconciliationSummary?.RepairedRuns ?? 0,
            LifecycleBatchesLastCycle = reconciliationSummary?.BatchesProcessed ?? 0,
            ConfigCacheAgeSeconds = lastConfigRefreshUtc == DateTime.MinValue
                ? 0
                : Math.Max(0, (int)(UtcNow - lastConfigRefreshUtc).TotalSeconds),
            ConfigRefreshDueAtUtc = nextConfigRefreshUtc,
            ConfigRefreshIntervalSeconds = Math.Max(1, (int)OptimizationConfigProvider.GetCacheTtl().TotalSeconds),
            LastLifecycleReconciledAtUtc = reconciliationSummary?.LastActivityAtUtc,
            OldestQueuedRunId = oldestQueuedRun?.Id,
            OldestQueuedAtUtc = oldestQueuedRun?.QueuedAtUtc,
            OldestQueuedAgeSeconds = oldestQueuedRun is null
                ? 0
                : Math.Max(0, (int)(UtcNow - oldestQueuedRun.QueuedAtUtc).TotalSeconds),
            OldestRunningRunId = oldestRunningRun?.Id,
            OldestRunningStage = oldestRunningRun?.ExecutionStage,
            OldestRunningStageMessage = oldestRunningRun?.ExecutionStageMessage,
            OldestRunningStageUpdatedAt = oldestRunningRun?.ExecutionStageUpdatedAt,
            OldestStrandedLifecycleRunId = oldestStrandedLifecycleRun?.Id,
            OldestStrandedLifecycleStatus = oldestStrandedLifecycleRun?.Status,
            OldestStrandedLifecycleAnchorAtUtc = oldestStrandedLifecycleRun?.AnchorAtUtc,
        });
    }
}
