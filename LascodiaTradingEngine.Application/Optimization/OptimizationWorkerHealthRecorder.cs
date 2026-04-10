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
        OptimizationRunRecoveryCoordinator.StaleRunningRecoverySummary? staleRunningSummary,
        OptimizationRunRecoveryCoordinator.LifecycleReconciliationSummary? reconciliationSummary,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb = writeCtx.GetDbContext();
        var nowUtc = UtcNow;
        var starvationCutoffUtc = nowUtc.AddHours(-24);
        var deferredRunsStartedCutoffUtc = nowUtc.AddHours(-1);

        // --- Batched status counts: single GROUP BY query instead of 3+ separate COUNT queries ---
        var statusCounts = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && (r.Status == OptimizationRunStatus.Queued
                      || r.Status == OptimizationRunStatus.Running
                      || r.Status == OptimizationRunStatus.Abandoned))
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int totalQueuedRuns = statusCounts.FirstOrDefault(s => s.Status == OptimizationRunStatus.Queued)?.Count ?? 0;
        int runningRuns = statusCounts.FirstOrDefault(s => s.Status == OptimizationRunStatus.Running)?.Count ?? 0;
        int abandonedRuns = statusCounts.FirstOrDefault(s => s.Status == OptimizationRunStatus.Abandoned)?.Count ?? 0;

        var eligibleQueuedRunsQuery = readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == OptimizationRunStatus.Queued
                     && (r.DeferredUntilUtc == null || r.DeferredUntilUtc <= nowUtc));
        var deferredQueuedRunsQuery = readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == OptimizationRunStatus.Queued
                     && r.DeferredUntilUtc != null
                     && r.DeferredUntilUtc > nowUtc);
        var runningRunsQuery = readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == OptimizationRunStatus.Running);
        var starvedQueuedRunsQuery = eligibleQueuedRunsQuery
            .Where(r => (r.QueuedAt == default ? r.StartedAt : r.QueuedAt) < starvationCutoffUtc);
        var retryReadyRunsQuery = OptimizationRetryPlanner.QueryRetryReadyRuns(
            readDb,
            Math.Max(0, config.MaxRetryAttempts),
            nowUtc);

        // --- Batched running-run lease breakdown: single query with conditional counts ---
        var runningLeaseBreakdown = await runningRunsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ActiveLeased = g.Count(r => r.ExecutionLeaseToken != null
                                          && r.ExecutionLeaseExpiresAt != null
                                          && r.ExecutionLeaseExpiresAt >= nowUtc),
                Stale = g.Count(r => r.ExecutionLeaseToken != null
                                   && r.ExecutionLeaseExpiresAt != null
                                   && r.ExecutionLeaseExpiresAt < nowUtc),
                LeaseMissing = g.Count(r => r.ExecutionLeaseToken == null
                                          || r.ExecutionLeaseExpiresAt == null),
            })
            .FirstOrDefaultAsync(ct);

        int activeLeasedRunningRuns = runningLeaseBreakdown?.ActiveLeased ?? 0;
        int staleRunningRuns = runningLeaseBreakdown?.Stale ?? 0;
        int leaseMissingRunningRuns = runningLeaseBreakdown?.LeaseMissing ?? 0;

        int queuedRuns = await eligibleQueuedRunsQuery.CountAsync(ct);
        int deferredQueuedRuns = await deferredQueuedRunsQuery.CountAsync(ct);
        int retryableFailedRuns = await retryReadyRunsQuery.CountAsync(ct);
        int starvedQueuedRuns = await starvedQueuedRunsQuery.CountAsync(ct);

        // --- Batched deferral / resume counts: single query ---
        var deferralResumeCounts = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && ((r.DeferredAtUtc != null && r.DeferredAtUtc >= deferredRunsStartedCutoffUtc)
                      || (r.LastResumedAtUtc != null && r.LastResumedAtUtc >= deferredRunsStartedCutoffUtc)))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                DeferredStarted = g.Count(r => r.DeferredAtUtc != null && r.DeferredAtUtc >= deferredRunsStartedCutoffUtc),
                DeferredResumed = g.Count(r => r.LastResumedAtUtc != null && r.LastResumedAtUtc >= deferredRunsStartedCutoffUtc),
            })
            .FirstOrDefaultAsync(ct);

        int deferredRunsStartedLastHour = deferralResumeCounts?.DeferredStarted ?? 0;
        int deferredRunsResumedLastHour = deferralResumeCounts?.DeferredResumed ?? 0;
        int repeatedlyDeferredQueuedRuns = await deferredQueuedRunsQuery
            .CountAsync(r => r.DeferralCount >= 2, ct);

        // --- Batched approved-run follow-up counts: single query ---
        var approvedFollowUpCounts = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted && r.Status == OptimizationRunStatus.Approved)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PendingFollowUps = g.Count(r => r.ValidationFollowUpStatus == ValidationFollowUpStatus.Pending
                                             || r.ValidationFollowUpStatus == null),
                MissingFollowUps = g.Count(r => r.ValidationFollowUpsCreatedAt == null
                                             || r.ValidationFollowUpStatus == null),
            })
            .FirstOrDefaultAsync(ct);

        int pendingFollowUps = approvedFollowUpCounts?.PendingFollowUps ?? 0;
        int approvedRunsMissingFollowUps = approvedFollowUpCounts?.MissingFollowUps ?? 0;

        // --- Batched completion pipeline counts: single query ---
        var completionCounts = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && (r.Status == OptimizationRunStatus.Completed
                      || r.Status == OptimizationRunStatus.Approved
                      || r.Status == OptimizationRunStatus.Rejected)
                     && r.ResultsPersistedAt != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PendingPreparation = g.Count(r => r.CompletionPublicationPayloadJson == null),
                PendingPublication = g.Count(r => r.CompletionPublicationPayloadJson != null
                                               && (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Pending
                                                || r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Failed)),
            })
            .FirstOrDefaultAsync(ct);

        int pendingCompletionPreparation = completionCounts?.PendingPreparation ?? 0;
        int pendingCompletionPublications = completionCounts?.PendingPublication ?? 0;

        int strandedLifecycleRuns = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
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

        // --- "Oldest" queries: limited to .Take(100) to prevent full-table scans ---
        var oldestStrandedLifecycleRun = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
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
            .Take(100)
            .Select(r => new
            {
                r.Id,
                r.Status,
                AnchorAtUtc = r.CompletedAt ?? r.ApprovedAt ?? r.ResultsPersistedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt
            })
            .FirstOrDefaultAsync(ct);
        var oldestRunningRun = await runningRunsQuery
            .OrderBy(r => r.ExecutionStageUpdatedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                r.ExecutionStage,
                r.ExecutionStageMessage,
                r.ExecutionStageUpdatedAt
            })
            .FirstOrDefaultAsync(ct);
        var oldestQueuedRun = await eligibleQueuedRunsQuery
            .OrderBy(r => r.QueuedAt == default ? r.StartedAt : r.QueuedAt)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                QueuedAtUtc = r.QueuedAt == default ? r.StartedAt : r.QueuedAt
            })
            .FirstOrDefaultAsync(ct);
        var oldestDeferredQueuedRun = await deferredQueuedRunsQuery
            .OrderBy(r => r.QueuedAt == default ? r.StartedAt : r.QueuedAt)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                QueuedAtUtc = r.QueuedAt == default ? r.StartedAt : r.QueuedAt,
                r.DeferredUntilUtc
            })
            .FirstOrDefaultAsync(ct);
        var oldestActiveDeferral = await deferredQueuedRunsQuery
            .Where(r => r.DeferredAtUtc != null)
            .OrderBy(r => r.DeferredAtUtc)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                r.DeferredAtUtc
            })
            .FirstOrDefaultAsync(ct);
        var deferredQueuedRunsByReason = await deferredQueuedRunsQuery
            .GroupBy(r => r.DeferralReason ?? OptimizationDeferralReason.Unknown)
            .Select(g => new OptimizationDeferralReasonCountSnapshot(g.Key, g.Count()))
            .OrderBy(x => x.Reason)
            .ToListAsync(ct);
        var mostDeferredQueuedRun = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted
                     && r.Status == OptimizationRunStatus.Queued
                     && r.DeferralCount > 0)
            .OrderByDescending(r => r.DeferralCount)
            .ThenBy(r => r.QueuedAt == default ? r.StartedAt : r.QueuedAt)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                r.DeferralCount
            })
            .FirstOrDefaultAsync(ct);
        var mostRecentDeferredResume = await readDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => !r.IsDeleted && r.LastResumedAtUtc != null)
            .OrderByDescending(r => r.LastResumedAtUtc)
            .Take(100)
            .Select(r => new
            {
                r.Id,
                r.LastResumedAtUtc
            })
            .FirstOrDefaultAsync(ct);
        var oldestStarvedQueuedRun = await starvedQueuedRunsQuery
            .OrderBy(r => r.QueuedAt == default ? r.StartedAt : r.QueuedAt)
            .Take(100)
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
            DeferredQueuedRuns = deferredQueuedRuns,
            RunningRuns = runningRuns,
            ActiveLeasedRunningRuns = activeLeasedRunningRuns,
            StaleRunningRuns = staleRunningRuns,
            LeaseMissingRunningRuns = leaseMissingRunningRuns,
            RetryableFailedRuns = retryableFailedRuns,
            AbandonedRuns = abandonedRuns,
            PendingFollowUps = pendingFollowUps,
            PendingCompletionPublications = pendingCompletionPublications,
            ApprovedRunsMissingFollowUps = approvedRunsMissingFollowUps,
            PendingCompletionPreparation = pendingCompletionPreparation,
            StrandedLifecycleRuns = strandedLifecycleRuns,
            LifecycleRepairsLastCycle = reconciliationSummary?.RepairedRuns ?? 0,
            LifecycleBatchesLastCycle = reconciliationSummary?.BatchesProcessed ?? 0,
            LifecycleMissingCompletionPayloadRepairsLastCycle = reconciliationSummary?.MissingCompletionPayloadRepairs ?? 0,
            LifecycleMalformedCompletionPayloadRepairsLastCycle = reconciliationSummary?.MalformedCompletionPayloadRepairs ?? 0,
            LifecycleFollowUpRepairsLastCycle = reconciliationSummary?.FollowUpRepairs ?? 0,
            LifecycleConfigSnapshotRepairsLastCycle = reconciliationSummary?.ConfigSnapshotRepairs ?? 0,
            LifecycleBestParameterRepairsLastCycle = reconciliationSummary?.BestParameterRepairs ?? 0,
            LeaseReclaimsLastCycle = staleRunningSummary?.RequeuedRuns ?? 0,
            OrphanedStaleRunningRunsLastCycle = staleRunningSummary?.OrphanedRuns ?? 0,
            ConfigCacheAgeSeconds = lastConfigRefreshUtc == DateTime.MinValue
                ? 0
                : Math.Max(0, (int)(nowUtc - lastConfigRefreshUtc).TotalSeconds),
            ConfigRefreshDueAtUtc = nextConfigRefreshUtc,
            ConfigRefreshIntervalSeconds = Math.Max(1, (int)OptimizationConfigProvider.GetCacheTtl().TotalSeconds),
            LastLifecycleReconciledAtUtc = reconciliationSummary?.LastActivityAtUtc,
            OldestQueuedRunId = oldestQueuedRun?.Id,
            OldestQueuedAtUtc = oldestQueuedRun?.QueuedAtUtc,
            OldestQueuedAgeSeconds = oldestQueuedRun is null
                ? 0
                : Math.Max(0, (int)(nowUtc - oldestQueuedRun.QueuedAtUtc).TotalSeconds),
            OldestDeferredQueuedRunId = oldestDeferredQueuedRun?.Id,
            OldestDeferredQueuedAtUtc = oldestDeferredQueuedRun?.QueuedAtUtc,
            OldestDeferredUntilUtc = oldestDeferredQueuedRun?.DeferredUntilUtc,
            OldestDeferredQueuedAgeSeconds = oldestDeferredQueuedRun is null
                ? 0
                : Math.Max(0, (int)(nowUtc - oldestDeferredQueuedRun.QueuedAtUtc).TotalSeconds),
            MostDeferredQueuedRunId = mostDeferredQueuedRun?.Id,
            MostDeferredQueuedDeferralCount = mostDeferredQueuedRun?.DeferralCount ?? 0,
            MostRecentDeferredResumeRunId = mostRecentDeferredResume?.Id,
            MostRecentDeferredResumeAtUtc = mostRecentDeferredResume?.LastResumedAtUtc,
            DeferredRunsStartedLastHour = deferredRunsStartedLastHour,
            DeferredRunsResumedLastHour = deferredRunsResumedLastHour,
            RepeatedlyDeferredQueuedRuns = repeatedlyDeferredQueuedRuns,
            OldestActiveDeferralRunId = oldestActiveDeferral?.Id,
            OldestActiveDeferralAtUtc = oldestActiveDeferral?.DeferredAtUtc,
            OldestActiveDeferralAgeSeconds = oldestActiveDeferral?.DeferredAtUtc is DateTime oldestActiveDeferralAtUtc
                ? Math.Max(0, (int)(nowUtc - oldestActiveDeferralAtUtc).TotalSeconds)
                : 0,
            DeferredQueuedRunsByReason = deferredQueuedRunsByReason,
            StarvedQueuedRuns = starvedQueuedRuns,
            OldestStarvedQueuedRunId = oldestStarvedQueuedRun?.Id,
            OldestStarvedQueuedAtUtc = oldestStarvedQueuedRun?.QueuedAtUtc,
            OldestStarvedQueuedAgeSeconds = oldestStarvedQueuedRun is null
                ? 0
                : Math.Max(0, (int)(nowUtc - oldestStarvedQueuedRun.QueuedAtUtc).TotalSeconds),
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
