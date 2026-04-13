using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class OptimizationRunRecoveryCoordinator
{
    internal sealed record StaleRunningRecoverySummary(
        int RequeuedRuns,
        int OrphanedRuns,
        DateTime ExecutedAtUtc);

    internal sealed record LifecycleReconciliationSummary(
        int RepairedRuns,
        int BatchesProcessed,
        int MissingCompletionPayloadRepairs,
        int MalformedCompletionPayloadRepairs,
        int FollowUpRepairs,
        int ConfigSnapshotRepairs,
        int BestParameterRepairs,
        DateTime StartedAtUtc,
        DateTime? LastActivityAtUtc);

    private const int LifecycleReconciliationBatchLimit = 50;
    private const int LifecycleReconciliationMaxBatchesPerCycle = 4;
    private static readonly TimeSpan StaleQueuedWarningCooldown = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<OptimizationRunRecoveryCoordinator> _logger;
    private readonly OptimizationFollowUpCoordinator _followUpCoordinator;
    private readonly OptimizationRunScopedConfigService _runScopedConfigService;
    private readonly TimeProvider _timeProvider;
    private readonly object _staleQueuedWarningGate = new();
    private DateTime? _lastStaleQueuedWarningAtUtc;
    private long? _lastStaleQueuedWarningRunId;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationRunRecoveryCoordinator(
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        ILogger<OptimizationRunRecoveryCoordinator> logger,
        OptimizationFollowUpCoordinator followUpCoordinator,
        OptimizationRunScopedConfigService runScopedConfigService,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
        _followUpCoordinator = followUpCoordinator;
        _runScopedConfigService = runScopedConfigService;
        _timeProvider = timeProvider;
    }

    internal async Task<StaleRunningRecoverySummary> RecoverStaleRunningRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var nowUtc = UtcNow;

        var (requeued, orphaned) = await OptimizationRunClaimer.RequeueStaleRunningRunsAsync(db, nowUtc, ct);

        if (orphaned > 0)
        {
            _logger.LogWarning(
                "OptimizationRunRecoveryCoordinator: marked {Count} orphaned stale Running run(s) as Failed (strategy deleted)",
                orphaned);
        }

        if (requeued > 0)
        {
            _metrics.OptimizationLeaseReclaims.Add(requeued);
            _logger.LogWarning(
                "OptimizationRunRecoveryCoordinator: recovered {Count} stale Running run(s) — re-queued",
                requeued);
        }

        return new StaleRunningRecoverySummary(requeued, orphaned, nowUtc);
    }

    internal async Task<StaleRunningRecoverySummary> RequeueExpiredRunningRunsAsync(CancellationToken ct)
        => await RecoverStaleRunningRunsAsync(ct);

    internal async Task RecoverStaleQueuedRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

        var nowUtc = UtcNow;
        var cutoff = nowUtc.AddHours(-24);
        var oldestStarvedQueuedRun = await db.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Queued
                      && !r.IsDeleted
                      && (r.QueuedAt == default ? r.StartedAt : r.QueuedAt) < cutoff
                      && (r.DeferredUntilUtc == null || r.DeferredUntilUtc <= nowUtc))
            .OrderBy(r => r.QueuedAt == default ? r.StartedAt : r.QueuedAt)
            .Select(r => new
            {
                r.Id,
                AnchorAtUtc = r.QueuedAt == default ? r.StartedAt : r.QueuedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (oldestStarvedQueuedRun is not null && ShouldEmitStaleQueuedWarning(oldestStarvedQueuedRun.Id, nowUtc))
        {
            _logger.LogWarning(
                "OptimizationRunRecoveryCoordinator: queued run starvation detected for run {RunId} (age={AgeHours:F1}h) — leaving it queued because queue age alone is not a corruption signal",
                oldestStarvedQueuedRun.Id,
                (nowUtc - oldestStarvedQueuedRun.AnchorAtUtc).TotalHours);
        }
    }

    internal async Task RetryFailedRunsAsync(OptimizationConfig config, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeCtx.GetDbContext();

        int maxRetryAttempts = Math.Max(0, config.MaxRetryAttempts);
        var nowUtc = UtcNow;

        var retryableRuns = await OptimizationRetryPlanner.QueryRetryReadyRuns(writeDb, maxRetryAttempts, nowUtc)
            .OrderBy(r => r.CompletedAt)
            .ToListAsync(ct);

        var retryableStrategyIds = retryableRuns
            .Select(r => r.StrategyId)
            .Distinct()
            .ToList();

        var terminalRunOrderLookup = retryableStrategyIds.Count == 0
            ? new Dictionary<long, List<(long RunId, DateTime AnchorUtc)>>()
            : (await writeDb.Set<OptimizationRun>()
                .Where(r => retryableStrategyIds.Contains(r.StrategyId)
                         && !r.IsDeleted
                         && r.CompletedAt != null)
                .Select(r => new { r.Id, r.StrategyId, r.CompletedAt })
                .ToListAsync(ct))
            .GroupBy(r => r.StrategyId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (RunId: r.Id, AnchorUtc: r.CompletedAt!.Value))
                    .OrderByDescending(x => x.AnchorUtc)
                    .ToList());

        int retried = 0;
        int superseded = 0;
        foreach (var run in retryableRuns)
        {
            ct.ThrowIfCancellationRequested();

            if (terminalRunOrderLookup.TryGetValue(run.StrategyId, out var terminalRuns)
                && terminalRuns.Any(other => other.RunId != run.Id
                    && (other.AnchorUtc > (run.CompletedAt ?? DateTime.MinValue)
                        || (other.AnchorUtc == (run.CompletedAt ?? DateTime.MinValue) && other.RunId > run.Id))))
            {
                var newerRun = terminalRuns
                    .First(other => other.RunId != run.Id
                        && (other.AnchorUtc > (run.CompletedAt ?? DateTime.MinValue)
                            || (other.AnchorUtc == (run.CompletedAt ?? DateTime.MinValue) && other.RunId > run.Id)));
                OptimizationRunStateMachine.Transition(
                    run,
                    OptimizationRunStatus.Abandoned,
                    nowUtc,
                    $"Superseded by newer optimization attempt {newerRun.RunId} completed at {newerRun.AnchorUtc:O}");
                await writeCtx.SaveChangesAsync(ct);
                superseded++;
                _logger.LogInformation(
                    "OptimizationRunRecoveryCoordinator: archived superseded failed run {RunId} — strategy {StrategyId} already has a newer completed optimization attempt {NewerRunId}",
                    run.Id,
                    run.StrategyId,
                    newerRun.RunId);
                continue;
            }

            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, nowUtc);
            run.RetryCount++;
            run.DeferredUntilUtc = null;

            try
            {
                await writeCtx.SaveChangesAsync(ct);
                retried++;
            }
            catch (DbUpdateException ex) when (IsActiveQueueConstraintViolation(ex))
            {
                await writeDb.Entry(run).ReloadAsync(ct);
                _logger.LogInformation(
                    "OptimizationRunRecoveryCoordinator: skipped retry for run {RunId} — another worker queued or claimed the strategy first",
                    run.Id);
            }
        }

        if (retried > 0)
        {
            _logger.LogInformation(
                "OptimizationRunRecoveryCoordinator: re-queued {Count} failed run(s) for retry",
                retried);
        }

        if (superseded > 0)
        {
            _logger.LogInformation(
                "OptimizationRunRecoveryCoordinator: archived {Count} superseded failed run(s) that were replaced by newer completed attempts",
                superseded);
        }

        var abandonedRuns = await writeDb.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Failed
                     && !r.IsDeleted
                     && (r.FailureCategory == OptimizationFailureCategory.SearchExhausted
                         || r.FailureCategory == OptimizationFailureCategory.ConfigError
                         || r.FailureCategory == OptimizationFailureCategory.StrategyRemoved
                         || r.RetryCount >= maxRetryAttempts))
            .ToListAsync(ct);
        int abandoned = abandonedRuns.Count;

        foreach (var run in abandonedRuns)
        {
            string abandonmentMessage = run.FailureCategory switch
            {
                OptimizationFailureCategory.SearchExhausted => string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? "Search space exhausted — moved to dead-letter queue [Marked non-retryable]"
                    : $"{run.ErrorMessage} [Marked non-retryable: search exhausted]",
                OptimizationFailureCategory.ConfigError => string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? "Configuration error — moved to dead-letter queue [Marked non-retryable]"
                    : $"{NormalizeConfigErrorMessage(run.ErrorMessage)} [Marked non-retryable: config error]",
                OptimizationFailureCategory.StrategyRemoved => string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? "Strategy removed — moved to dead-letter queue [Marked non-retryable]"
                    : $"{run.ErrorMessage} [Marked non-retryable: strategy removed]",
                _ => string.IsNullOrWhiteSpace(run.ErrorMessage)
                    ? $"Retry budget exhausted — moved to dead-letter queue [Abandoned after {run.RetryCount} retries]"
                    : $"{run.ErrorMessage} [Abandoned after {run.RetryCount} retries]"
            };
            OptimizationRunStateMachine.Transition(
                run,
                OptimizationRunStatus.Abandoned,
                nowUtc,
                abandonmentMessage);
        }

        if (abandoned > 0)
            await writeCtx.SaveChangesAsync(ct);

        if (abandoned <= 0)
            return;

        var alertCutoffUtc = nowUtc.AddHours(-24);
        _logger.LogWarning(
            "OptimizationRunRecoveryCoordinator: moved {Count} permanently failed run(s) to dead-letter (Abandoned) — retry budget exhausted, manual investigation required",
            abandoned);

        try
        {
                bool recentAlertExists = await writeDb.Set<Alert>()
                    .AnyAsync(a => a.DeduplicationKey == "OptimizationWorker:DeadLetter"
                               && a.LastTriggeredAt != null
                               && a.LastTriggeredAt >= alertCutoffUtc
                               && !a.IsDeleted, ct);

            if (recentAlertExists)
            {
                _logger.LogDebug(
                    "OptimizationRunRecoveryCoordinator: suppressed duplicate dead-letter alert ({Count} run(s)) — recent alert exists",
                    abandoned);
                return;
            }

            var alertDispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
                var alert = new Alert
                {
                AlertType = AlertType.OptimizationLifecycleIssue,
                Severity = AlertSeverity.High,
                DeduplicationKey = "OptimizationWorker:DeadLetter",
                IsActive = true,
                LastTriggeredAt = nowUtc,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    Type = "OptimizationDeadLetter",
                    AbandonedCount = abandoned,
                    MaxRetryAttempts = maxRetryAttempts,
                    Message = $"{abandoned} optimization run(s) moved to dead-letter queue after exhausting {maxRetryAttempts} retry attempts. Manual investigation required."
                }),
            };
            writeDb.Set<Alert>().Add(alert);
            await writeCtx.SaveChangesAsync(ct);

            await alertDispatcher.DispatchAsync(
                alert,
                $"{abandoned} optimization run(s) permanently failed after {maxRetryAttempts} retries — moved to dead-letter queue",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationRunRecoveryCoordinator: dead-letter alert dispatch failed (non-fatal)");
        }
    }

    internal async Task<LifecycleReconciliationSummary> ReconcileLifecycleStateAsync(
        OptimizationConfig config,
        CancellationToken ct)
    {
        var startedAtUtc = UtcNow;
        var summary = new LifecycleReconciliationSummary(
            RepairedRuns: 0,
            BatchesProcessed: 0,
            MissingCompletionPayloadRepairs: 0,
            MalformedCompletionPayloadRepairs: 0,
            FollowUpRepairs: 0,
            ConfigSnapshotRepairs: 0,
            BestParameterRepairs: 0,
            StartedAtUtc: startedAtUtc,
            LastActivityAtUtc: null);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var writeDb = writeCtx.GetDbContext();
            for (int batchNumber = 0; batchNumber < LifecycleReconciliationMaxBatchesPerCycle; batchNumber++)
            {
                ct.ThrowIfCancellationRequested();
                var nowUtc = UtcNow;

                var candidateRuns = await writeDb.Set<OptimizationRun>()
                    .Where(r => !r.IsDeleted && (
                        ((r.Status == OptimizationRunStatus.Completed
                          || r.Status == OptimizationRunStatus.Approved
                          || r.Status == OptimizationRunStatus.Rejected)
                         && (r.LifecycleReconciledAt == null
                          || (r.CompletedAt != null && r.ResultsPersistedAt == null)
                          || ((r.Status == OptimizationRunStatus.Approved || r.Status == OptimizationRunStatus.Rejected) && r.ApprovalEvaluatedAt == null)
                          || (r.ResultsPersistedAt != null && r.CompletionPublicationPayloadJson == null)
                          || (r.CompletionPublicationPayloadJson != null && r.CompletionPublicationStatus == null)
                          || (r.CompletionPublicationPayloadJson != null && r.CompletionPublicationPreparedAt == null)
                          || r.CompletionPublicationErrorMessage != null
                          || (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Published && r.CompletionPublicationCompletedAt == null)
                          || (r.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Published && r.CompletionPublicationErrorMessage != null)))
                        || (r.Status == OptimizationRunStatus.Approved
                            && (r.ValidationFollowUpsCreatedAt == null
                             || r.ValidationFollowUpStatus == null
                             || r.BestParametersJson == null))
                        || (r.Status == OptimizationRunStatus.Rejected
                            && (r.ValidationFollowUpsCreatedAt != null
                             || r.ValidationFollowUpStatus != null
                             || r.NextFollowUpCheckAt != null
                             || r.FollowUpLastCheckedAt != null
                             || r.FollowUpRepairAttempts > 0
                             || r.FollowUpLastStatusCode != null
                             || r.FollowUpLastStatusMessage != null
                             || r.FollowUpStatusUpdatedAt != null))))
                    .OrderBy(r => r.CompletedAt ?? r.ApprovedAt ?? r.ExecutionStartedAt ?? r.ClaimedAt ?? (DateTime?)r.QueuedAt ?? r.StartedAt)
                    .Take(LifecycleReconciliationBatchLimit)
                    .ToListAsync(ct);

                if (candidateRuns.Count == 0)
                    break;

                var strategyIds = candidateRuns.Select(r => r.StrategyId).Distinct().ToList();
                var strategies = await writeDb.Set<Strategy>()
                    .Where(s => strategyIds.Contains(s.Id) && !s.IsDeleted)
                    .ToDictionaryAsync(s => s.Id, ct);

                int repairedThisBatch = 0;
                int missingCompletionPayloadRepairs = 0;
                int malformedCompletionPayloadRepairs = 0;
                int followUpRepairs = 0;
                int configSnapshotRepairs = 0;
                int bestParameterRepairs = 0;

                foreach (var run in candidateRuns)
                {
                    ct.ThrowIfCancellationRequested();

                    bool changed = false;

                    // Mark runs whose strategy has been deleted as Abandoned
                    if (!strategies.ContainsKey(run.StrategyId)
                        && run.Status is not (OptimizationRunStatus.Abandoned or OptimizationRunStatus.Failed))
                    {
                        OptimizationRunStateMachine.Transition(
                            run,
                            OptimizationRunStatus.Abandoned,
                            nowUtc,
                            "Strategy deleted or not found — marking run as abandoned");
                        run.FailureCategory = OptimizationFailureCategory.StrategyRemoved;
                        run.LifecycleReconciledAt = nowUtc;
                        repairedThisBatch++;
                        continue;
                    }

                    if (run.CompletedAt.HasValue && run.ResultsPersistedAt == null)
                    {
                        run.ResultsPersistedAt = run.CompletedAt.Value;
                        changed = true;
                    }

                    if (run.Status is OptimizationRunStatus.Approved or OptimizationRunStatus.Rejected
                        && run.ApprovalEvaluatedAt == null)
                    {
                        run.ApprovalEvaluatedAt = run.ApprovedAt ?? run.CompletedAt ?? nowUtc;
                        changed = true;
                    }

                    if (run.Status == OptimizationRunStatus.Approved
                        && string.IsNullOrWhiteSpace(run.BestParametersJson)
                        && strategies.TryGetValue(run.StrategyId, out var strategyForBestParams)
                        && !string.IsNullOrWhiteSpace(strategyForBestParams.ParametersJson))
                    {
                        run.BestParametersJson = CanonicalParameterJson.Normalize(strategyForBestParams.ParametersJson);
                        changed = true;
                        bestParameterRepairs++;
                    }

                    if (!string.IsNullOrWhiteSpace(run.ConfigSnapshotJson)
                        && !_runScopedConfigService.TryLoadRunScopedConfigSnapshot(run, out _))
                    {
                        run.ConfigSnapshotJson = OptimizationRunContracts.SerializeConfigSnapshot(config);
                        OptimizationRunProgressTracker.RecordOperationalIssue(
                            run,
                            "MalformedConfigSnapshot",
                            "Recovered malformed run-scoped configuration snapshot using the current optimization config.",
                            nowUtc);
                        changed = true;
                        configSnapshotRepairs++;
                    }

                    if (run.Status == OptimizationRunStatus.Rejected && HasFollowUpState(run))
                    {
                        ClearFollowUpState(run);
                        changed = true;
                        followUpRepairs++;
                    }

                    if (NeedsCompletionPayloadRepair(run, strategies, out var rebuiltCompletedEvent, out bool hadMalformedPayload))
                    {
                        run.CompletionPublicationPayloadJson = JsonSerializer.Serialize(rebuiltCompletedEvent);
                        run.CompletionPublicationPreparedAt ??= nowUtc;
                        run.CompletionPublicationStatus ??= run.CompletionPublicationCompletedAt.HasValue
                            ? OptimizationCompletionPublicationStatus.Published
                            : OptimizationCompletionPublicationStatus.Pending;
                        if (run.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Published)
                        {
                            run.CompletionPublicationCompletedAt ??= run.CompletionPublicationLastAttemptAt
                                ?? run.CompletionPublicationPreparedAt
                                ?? run.ResultsPersistedAt
                                ?? run.ApprovedAt
                                ?? run.CompletedAt
                                ?? nowUtc;
                            run.CompletionPublicationErrorMessage = null;
                        }

                        changed = true;
                        if (hadMalformedPayload)
                            malformedCompletionPayloadRepairs++;
                        else
                            missingCompletionPayloadRepairs++;
                    }
                    else
                    {
                        if (run.CompletionPublicationPayloadJson != null && run.CompletionPublicationPreparedAt == null)
                        {
                            run.CompletionPublicationPreparedAt = run.CompletionPublicationLastAttemptAt
                                ?? run.ResultsPersistedAt
                                ?? run.CompletedAt
                                ?? run.ApprovedAt
                                ?? nowUtc;
                            changed = true;
                        }

                        if (run.CompletionPublicationPayloadJson != null && run.CompletionPublicationStatus == null)
                        {
                            run.CompletionPublicationStatus = run.CompletionPublicationCompletedAt.HasValue
                                ? OptimizationCompletionPublicationStatus.Published
                                : OptimizationCompletionPublicationStatus.Pending;
                            changed = true;
                        }

                        if (run.CompletionPublicationStatus == OptimizationCompletionPublicationStatus.Published)
                        {
                            if (run.CompletionPublicationPreparedAt == null)
                            {
                                run.CompletionPublicationPreparedAt = run.CompletionPublicationLastAttemptAt
                                    ?? run.ResultsPersistedAt
                                    ?? run.CompletedAt
                                    ?? run.ApprovedAt
                                    ?? nowUtc;
                                changed = true;
                            }

                            if (run.CompletionPublicationCompletedAt == null)
                            {
                                run.CompletionPublicationCompletedAt = run.CompletionPublicationLastAttemptAt
                                    ?? run.CompletionPublicationPreparedAt
                                    ?? run.ResultsPersistedAt
                                    ?? run.ApprovedAt
                                    ?? run.CompletedAt
                                    ?? nowUtc;
                                changed = true;
                            }

                            if (!string.IsNullOrWhiteSpace(run.CompletionPublicationErrorMessage))
                            {
                                run.CompletionPublicationErrorMessage = null;
                                changed = true;
                            }
                        }
                    }

                    if (run.Status == OptimizationRunStatus.Approved
                        && (run.ValidationFollowUpsCreatedAt == null || run.ValidationFollowUpStatus == null)
                        && strategies.TryGetValue(run.StrategyId, out var strategyForFollowUps))
                    {
                        var repairConfig = config;
                        if (!string.IsNullOrWhiteSpace(run.ConfigSnapshotJson)
                            && _runScopedConfigService.TryLoadRunScopedConfigSnapshot(run, out var runScopedConfig))
                        {
                            repairConfig = runScopedConfig;
                        }

                        bool followUpsAlreadyPresent = await _followUpCoordinator.EnsureValidationFollowUpsAsync(
                            writeDb,
                            run,
                            strategyForFollowUps,
                            repairConfig,
                            ct);
                        if (followUpsAlreadyPresent)
                            _metrics.OptimizationDuplicateFollowUpsPrevented.Add(1);

                        changed = true;
                        followUpRepairs++;
                    }

                    if (!changed && run.LifecycleReconciledAt == null)
                    {
                        run.LifecycleReconciledAt = nowUtc;
                        changed = true;
                    }

                    if (changed)
                    {
                        run.LifecycleReconciledAt = nowUtc;
                        repairedThisBatch++;
                    }
                }

                summary = summary with
                {
                    RepairedRuns = summary.RepairedRuns + repairedThisBatch,
                    BatchesProcessed = summary.BatchesProcessed + 1,
                    MissingCompletionPayloadRepairs = summary.MissingCompletionPayloadRepairs + missingCompletionPayloadRepairs,
                    MalformedCompletionPayloadRepairs = summary.MalformedCompletionPayloadRepairs + malformedCompletionPayloadRepairs,
                    FollowUpRepairs = summary.FollowUpRepairs + followUpRepairs,
                    ConfigSnapshotRepairs = summary.ConfigSnapshotRepairs + configSnapshotRepairs,
                    BestParameterRepairs = summary.BestParameterRepairs + bestParameterRepairs,
                    LastActivityAtUtc = repairedThisBatch > 0 ? nowUtc : summary.LastActivityAtUtc
                };

                if (repairedThisBatch > 0)
                    await writeCtx.SaveChangesAsync(ct);

                if (candidateRuns.Count < LifecycleReconciliationBatchLimit || repairedThisBatch == 0)
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, (batchNumber + 1) * 50)), ct);
            }

            if (summary.RepairedRuns > 0)
            {
                _logger.LogInformation(
                    "OptimizationRunRecoveryCoordinator: reconciled lifecycle state for {Count} optimization run(s) across {Batches} batch(es) " +
                    "(missing payloads={MissingPayloads}, malformed payloads={MalformedPayloads}, follow-ups={FollowUps}, config snapshots={Snapshots}, best-parameter repairs={BestParams})",
                    summary.RepairedRuns,
                    summary.BatchesProcessed,
                    summary.MissingCompletionPayloadRepairs,
                    summary.MalformedCompletionPayloadRepairs,
                    summary.FollowUpRepairs,
                    summary.ConfigSnapshotRepairs,
                    summary.BestParameterRepairs);
            }

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationRunRecoveryCoordinator: lifecycle reconciliation sweep failed (non-fatal)");
            return summary;
        }
    }

    private bool ShouldEmitStaleQueuedWarning(long runId, DateTime nowUtc)
    {
        lock (_staleQueuedWarningGate)
        {
            if (_lastStaleQueuedWarningRunId == runId
                && _lastStaleQueuedWarningAtUtc.HasValue
                && nowUtc - _lastStaleQueuedWarningAtUtc.Value < StaleQueuedWarningCooldown)
            {
                return false;
            }

            _lastStaleQueuedWarningRunId = runId;
            _lastStaleQueuedWarningAtUtc = nowUtc;
            return true;
        }
    }

    private static void ClearFollowUpState(OptimizationRun run)
    {
        run.ValidationFollowUpsCreatedAt = null;
        run.ValidationFollowUpStatus = null;
        run.NextFollowUpCheckAt = null;
        run.FollowUpLastCheckedAt = null;
        run.FollowUpRepairAttempts = 0;
        run.FollowUpLastStatusCode = null;
        run.FollowUpLastStatusMessage = null;
        run.FollowUpStatusUpdatedAt = null;
    }

    private static bool HasFollowUpState(OptimizationRun run)
        => run.ValidationFollowUpsCreatedAt != null
        || run.ValidationFollowUpStatus != null
        || run.NextFollowUpCheckAt != null
        || run.FollowUpLastCheckedAt != null
        || run.FollowUpRepairAttempts > 0
        || !string.IsNullOrWhiteSpace(run.FollowUpLastStatusCode)
        || !string.IsNullOrWhiteSpace(run.FollowUpLastStatusMessage)
        || run.FollowUpStatusUpdatedAt != null;

    private static bool NeedsCompletionPayloadRepair(
        OptimizationRun run,
        IReadOnlyDictionary<long, Strategy> strategies,
        out OptimizationCompletedIntegrationEvent? completedEvent,
        out bool hadMalformedPayload)
    {
        completedEvent = null;
        hadMalformedPayload = false;

        if (!run.ResultsPersistedAt.HasValue
            && run.Status is not (OptimizationRunStatus.Approved
                or OptimizationRunStatus.Rejected
                or OptimizationRunStatus.Completed))
            return false;

        bool missingPayload = string.IsNullOrWhiteSpace(run.CompletionPublicationPayloadJson);
        bool malformedPayload = !missingPayload
            && (!TryDeserializeCompletionPayload(run.CompletionPublicationPayloadJson, out var existingEvent)
                || !IsCompletionPayloadConsistent(existingEvent, run, strategies.GetValueOrDefault(run.StrategyId)));

        if (!missingPayload && !malformedPayload)
            return false;

        if (!strategies.TryGetValue(run.StrategyId, out var strategy))
            return false;

        completedEvent = BuildCompletionEvent(run, strategy);
        hadMalformedPayload = malformedPayload;
        return true;
    }

    private static bool TryDeserializeCompletionPayload(
        string? payloadJson,
        out OptimizationCompletedIntegrationEvent? completedEvent)
    {
        completedEvent = null;
        if (string.IsNullOrWhiteSpace(payloadJson))
            return false;

        try
        {
            completedEvent = JsonSerializer.Deserialize<OptimizationCompletedIntegrationEvent>(payloadJson);
            return completedEvent is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCompletionPayloadConsistent(
        OptimizationCompletedIntegrationEvent? completedEvent,
        OptimizationRun run,
        Strategy? strategy)
    {
        if (completedEvent is null)
            return false;

        if (completedEvent.OptimizationRunId != run.Id || completedEvent.StrategyId != run.StrategyId)
            return false;

        if (completedEvent.CompletedAt == default)
            return false;

        if (strategy is null)
            return !string.IsNullOrWhiteSpace(completedEvent.Symbol);

        return string.Equals(completedEvent.Symbol, strategy.Symbol, StringComparison.Ordinal)
            && completedEvent.Timeframe == strategy.Timeframe;
    }

    private static OptimizationCompletedIntegrationEvent BuildCompletionEvent(
        OptimizationRun run,
        Strategy strategy)
        => new()
        {
            OptimizationRunId = run.Id,
            StrategyId = run.StrategyId,
            Symbol = strategy.Symbol,
            Timeframe = strategy.Timeframe,
            Iterations = run.Iterations,
            BaselineScore = run.BaselineHealthScore ?? 0m,
            BestOosScore = run.BestHealthScore ?? 0m,
            CompletedAt = run.CompletedAt
                ?? run.ApprovedAt
                ?? run.ResultsPersistedAt
                ?? run.ExecutionStartedAt
                ?? run.ClaimedAt
                ?? (DateTime?)run.QueuedAt
                ?? run.StartedAt,
        };

    private static string NormalizeConfigErrorMessage(string message)
        => message.Contains("invalid configuration", StringComparison.OrdinalIgnoreCase)
            ? message
            : $"Invalid configuration: {message}";

    private static bool IsActiveQueueConstraintViolation(DbUpdateException ex)
        => OptimizationDbExceptionClassifier.IsActiveQueueConstraintViolation(ex);
}
