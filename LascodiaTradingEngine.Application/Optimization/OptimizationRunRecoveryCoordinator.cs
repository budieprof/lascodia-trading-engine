using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class OptimizationRunRecoveryCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<OptimizationRunRecoveryCoordinator> _logger;

    public OptimizationRunRecoveryCoordinator(
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics,
        ILogger<OptimizationRunRecoveryCoordinator> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    }

    internal async Task RecoverStaleRunningRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db = writeCtx.GetDbContext();
            var nowUtc = DateTime.UtcNow;

            var activeStrategyIds = await db.Set<Strategy>()
                .Where(s => !s.IsDeleted)
                .Select(s => s.Id)
                .ToListAsync(ct);
            var activeStrategySet = new HashSet<long>(activeStrategyIds);

            var recoveredRuns = await db.Set<OptimizationRun>()
                .Where(r => r.Status == OptimizationRunStatus.Running
                         && !r.IsDeleted
                         && (r.ExecutionLeaseExpiresAt == null || r.ExecutionLeaseExpiresAt < nowUtc)
                         && activeStrategySet.Contains(r.StrategyId))
                .ToListAsync(ct);
            foreach (var recoveredRun in recoveredRuns)
                OptimizationRunLifecycle.RequeueForRecovery(recoveredRun, nowUtc);

            var orphanedRuns = await db.Set<OptimizationRun>()
                .Where(r => r.Status == OptimizationRunStatus.Running
                         && !r.IsDeleted
                         && (r.ExecutionLeaseExpiresAt == null || r.ExecutionLeaseExpiresAt < nowUtc)
                         && !activeStrategySet.Contains(r.StrategyId))
                .ToListAsync(ct);
            foreach (var orphanedRun in orphanedRuns)
            {
                orphanedRun.FailureCategory = OptimizationFailureCategory.StrategyRemoved;
                OptimizationRunStateMachine.Transition(
                    orphanedRun,
                    OptimizationRunStatus.Failed,
                    nowUtc,
                    "Strategy deleted during optimization run");
            }

            if (recoveredRuns.Count > 0 || orphanedRuns.Count > 0)
                await writeCtx.SaveChangesAsync(ct);

            if (orphanedRuns.Count > 0)
            {
                _logger.LogWarning(
                    "OptimizationRunRecoveryCoordinator: marked {Count} orphaned Running run(s) as Failed (strategy deleted)",
                    orphanedRuns.Count);
            }

            if (recoveredRuns.Count > 0)
            {
                _metrics.OptimizationLeaseReclaims.Add(recoveredRuns.Count);
                _logger.LogWarning(
                    "OptimizationRunRecoveryCoordinator: recovered {Count} stale Running run(s) from prior crash — re-queued",
                    recoveredRuns.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationRunRecoveryCoordinator: crash recovery check failed (non-fatal)");
        }
    }

    internal async Task RequeueExpiredRunningRunsAsync(CancellationToken ct)
    {
        await using var recoveryScope = _scopeFactory.CreateAsyncScope();
        var writeCtx = recoveryScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeCtx.GetDbContext();

        var (requeued, orphaned) = await OptimizationRunClaimer.RequeueExpiredRunsAsync(db, ct);

        if (requeued > 0)
            _metrics.OptimizationLeaseReclaims.Add(requeued);
        if (orphaned > 0)
        {
            _logger.LogWarning(
                "OptimizationRunRecoveryCoordinator: marked {Count} expired run(s) as Failed — strategy deleted",
                orphaned);
        }
    }

    internal async Task RecoverStaleQueuedRunsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var db = writeCtx.GetDbContext();

            var cutoff = DateTime.UtcNow.AddHours(-24);
            var staleQueuedRuns = await db.Set<OptimizationRun>()
                .Where(r => r.Status == OptimizationRunStatus.Queued
                          && !r.IsDeleted
                          && r.QueuedAt < cutoff
                          && (r.DeferredUntilUtc == null || r.DeferredUntilUtc < DateTime.UtcNow))
                .ToListAsync(ct);

            foreach (var run in staleQueuedRuns)
            {
                run.Status = OptimizationRunStatus.Failed;
                run.ErrorMessage = "Stale: queued for over 24 hours without being claimed";
                run.FailureCategory = OptimizationFailureCategory.Transient;
                run.CompletedAt = DateTime.UtcNow;
                _logger.LogWarning("OptimizationRunRecoveryCoordinator: marking stale queued run {RunId} as Failed", run.Id);
            }

            if (staleQueuedRuns.Count > 0)
                await writeCtx.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OptimizationRunRecoveryCoordinator: stale queued run recovery failed (non-fatal)");
        }
    }

    internal async Task RetryFailedRunsAsync(OptimizationConfig config, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        int maxRetryAttempts = Math.Max(0, config.MaxRetryAttempts);
        var nowUtc = DateTime.UtcNow;

        var retryableRuns = await writeDb.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Failed
                     && !r.IsDeleted
                     && r.RetryCount < maxRetryAttempts
                     && r.FailureCategory != OptimizationFailureCategory.ConfigError
                     && r.FailureCategory != OptimizationFailureCategory.SearchExhausted
                     && r.FailureCategory != OptimizationFailureCategory.StrategyRemoved
                     && r.CompletedAt != null
                     && r.CompletedAt.Value.AddMinutes(15 << r.RetryCount) <= nowUtc)
            .OrderBy(r => r.CompletedAt)
            .ToListAsync(ct);

        var retryableStrategyIds = retryableRuns
            .Select(r => r.StrategyId)
            .Distinct()
            .ToList();

        var activeStrategySet = retryableStrategyIds.Count == 0
            ? new HashSet<long>()
            : (await writeDb.Set<OptimizationRun>()
                .Where(r => retryableStrategyIds.Contains(r.StrategyId)
                         && !r.IsDeleted
                         && (r.Status == OptimizationRunStatus.Queued || r.Status == OptimizationRunStatus.Running))
                .Select(r => r.StrategyId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

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

            if (activeStrategySet.Contains(run.StrategyId))
            {
                _logger.LogInformation(
                    "OptimizationRunRecoveryCoordinator: skipped retry for run {RunId} — strategy {StrategyId} already has an active optimization run",
                    run.Id,
                    run.StrategyId);
                continue;
            }

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
                    DateTime.UtcNow,
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

            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, DateTime.UtcNow);
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
                    : $"{run.ErrorMessage} [Marked non-retryable: config error]",
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
                DateTime.UtcNow,
                abandonmentMessage);
        }

        if (abandoned > 0)
            await writeCtx.SaveChangesAsync(ct);

        if (abandoned <= 0)
            return;

        _logger.LogWarning(
            "OptimizationRunRecoveryCoordinator: moved {Count} permanently failed run(s) to dead-letter (Abandoned) — retry budget exhausted, manual investigation required",
            abandoned);

        try
        {
            bool recentAlertExists = await writeDb.Set<Alert>()
                .AnyAsync(a => a.Symbol == "OptimizationWorker:DeadLetter"
                           && a.LastTriggeredAt != null
                           && a.LastTriggeredAt >= DateTime.UtcNow.AddHours(-24)
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
                Symbol = "OptimizationWorker:DeadLetter",
                Channel = AlertChannel.Webhook,
                Destination = string.Empty,
                Severity = AlertSeverity.High,
                IsActive = true,
                LastTriggeredAt = DateTime.UtcNow,
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

            await alertDispatcher.DispatchBySeverityAsync(
                alert,
                $"{abandoned} optimization run(s) permanently failed after {maxRetryAttempts} retries — moved to dead-letter queue",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationRunRecoveryCoordinator: dead-letter alert dispatch failed (non-fatal)");
        }
    }

    private static bool IsActiveQueueConstraintViolation(DbUpdateException ex)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("IX_OptimizationRun_ActivePerStrategy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               && message.Contains("OptimizationRun", StringComparison.OrdinalIgnoreCase)
               && message.Contains("StrategyId", StringComparison.OrdinalIgnoreCase);
    }
}
