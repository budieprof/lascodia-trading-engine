using System.Diagnostics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Scoped)]
internal sealed class OptimizationRunProcessor
{
    private static readonly ActivitySource s_activitySource = new("LascodiaTradingEngine.Optimization");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OptimizationRunProcessor> _logger;
    private readonly TradingMetrics _metrics;
    private readonly OptimizationConfigProvider _configProvider;
    private readonly OptimizationRunPreflightService _preflightService;
    private readonly OptimizationRunExecutor _runExecutor;
    private readonly OptimizationRunLeaseManager _leaseManager;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationRunProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<OptimizationRunProcessor> logger,
        TradingMetrics metrics,
        OptimizationConfigProvider configProvider,
        OptimizationRunPreflightService preflightService,
        OptimizationRunExecutor runExecutor,
        OptimizationRunLeaseManager leaseManager,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _configProvider = configProvider;
        _preflightService = preflightService;
        _runExecutor = runExecutor;
        _leaseManager = leaseManager;
        _timeProvider = timeProvider;
    }

    internal async Task ProcessNextQueuedRunAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var alertDispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
        var eventService = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        var db = readCtx.GetDbContext();
        var writeDb = writeCtx.GetDbContext();

        int maxConcurrentRuns;
        {
            var config0 = await _configProvider.LoadAsync(db, ct);
            maxConcurrentRuns = config0.MaxConcurrentRuns;
        }

        var claimResult = await OptimizationRunClaimer.ClaimNextRunAsync(
            writeDb,
            maxConcurrentRuns,
            OptimizationExecutionLeasePolicy.LeaseDuration,
            UtcNow,
            ct);

        if (!claimResult.RunId.HasValue)
        {
            bool anyActive = await db.Set<Strategy>()
                .AnyAsync(s => s.Status == StrategyStatus.Active && !s.IsDeleted, ct);
            if (!anyActive)
            {
                _logger.LogInformation(
                    "OptimizationRunProcessor: no queued runs and no active strategies — system may be in cold start. Ensure strategies are created and activated via StrategyGenerationWorker or manual configuration");
            }
            return;
        }

        var run = await writeDb.Set<OptimizationRun>()
            .FirstOrDefaultAsync(x => x.Id == claimResult.RunId.Value, ct);
        if (run is null)
            return;

        if (claimResult.WasDeferred)
            _metrics.OptimizationDeferredRechecks.Add(1);

        var sw = Stopwatch.StartNew();
        OptimizationConfig? config = null;
        CancellationTokenSource? runCts = null;
        CancellationTokenSource? leaseHeartbeatCts = null;
        Task? leaseHeartbeatTask = null;
        var runCt = ct;
        Guid claimedLeaseToken = claimResult.LeaseToken;

        using var runActivity = s_activitySource.StartActivity("optimization.run");
        runActivity?.SetTag("run.id", run.Id);
        runActivity?.SetTag("strategy.id", run.StrategyId);

        try
        {
            leaseHeartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            leaseHeartbeatTask = _leaseManager.MaintainExecutionLeaseAsync(run.Id, claimedLeaseToken, leaseHeartbeatCts.Token);

            config = await _preflightService.PrepareAsync(run, db, writeCtx, ct);
            if (config is null)
                return;

            _logger.LogInformation(
                "OptimizationRunProcessor: processing run {RunId} for strategy {StrategyId}",
                run.Id,
                run.StrategyId);
            runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            runCts.CancelAfter(TimeSpan.FromMinutes(config.MaxRunTimeoutMinutes));
            runCt = runCts.Token;

            var strategy = await db.Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, runCt);
            if (strategy is null)
                throw new OptimizationStrategyRemovedException(run.StrategyId);

            await _runExecutor.ExecuteAsync(
                run,
                strategy,
                config,
                db,
                writeDb,
                writeCtx,
                mediator,
                alertDispatcher,
                eventService,
                sw,
                ct,
                runCt);
        }
        catch (DataQualityException dqEx)
        {
            _logger.LogWarning(
                "OptimizationRunProcessor: run {RunId} deferred due to data quality issue — {Reason}",
                run.Id,
                dqEx.Message);
            _metrics.OptimizationRunsDeferred.Add(1, new KeyValuePair<string, object?>("reason", "data_quality"));
            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Queued, UtcNow);
            run.FailureCategory = OptimizationFailureCategory.DataQuality;
            run.DeferredUntilUtc = UtcNow.AddHours(1);
            SetRunStage(run, OptimizationExecutionStage.Queued, $"Deferred because source data is not yet usable: {dqEx.Message}", UtcNow);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            if (await _leaseManager.HasLeaseOwnershipChangedAsync(writeDb, run.Id, claimedLeaseToken, ct))
            {
                _logger.LogWarning(
                    ex,
                    "OptimizationRunProcessor: stopping stale owner for run {RunId} after lease ownership changed",
                    run.Id);
                return;
            }

            if (await HasDurablePersistedTerminalResultAsync(writeDb, run.Id, CancellationToken.None))
            {
                _logger.LogError(ex,
                    "OptimizationRunProcessor: post-completion concurrency conflict for run {RunId} after result persistence — keeping status {Status}",
                    run.Id,
                    run.Status);
                return;
            }

            _logger.LogError(ex, "OptimizationRunProcessor: concurrency conflict while processing run {RunId}", run.Id);
            if (OptimizationRunStateMachine.CanTransition(run.Status, OptimizationRunStatus.Failed))
            {
                run.FailureCategory = OptimizationFailureCategory.Transient;
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Failed, UtcNow, ex.Message);
            }

            await writeCtx.SaveChangesAsync(ct);
            _metrics.OptimizationRunsFailed.Add(1);

            await TryLogDecisionAsync(
                mediator,
                new LogDecisionCommand
                {
                    EntityType = "OptimizationRun",
                    EntityId = run.Id,
                    DecisionType = "OptimizationFailed",
                    Outcome = "ConcurrencyConflict",
                    Reason = ex.Message,
                    Source = "OptimizationWorker"
                },
                ct,
                "OptimizationRunProcessor: failure audit log failed for run {RunId} after concurrency conflict (non-fatal)",
                run.Id);
        }
        catch (OperationCanceledException) when (runCts is not null && runCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (await HasDurablePersistedTerminalResultAsync(writeDb, run.Id, CancellationToken.None))
            {
                _logger.LogWarning(
                    "OptimizationRunProcessor: aggregate timeout fired after completion persistence for run {RunId} — keeping status {Status}",
                    run.Id,
                    run.Status);
                return;
            }

            _logger.LogWarning(
                "OptimizationRunProcessor: run {RunId} exceeded aggregate timeout of {Minutes}min",
                run.Id,
                config?.MaxRunTimeoutMinutes ?? 0);
            if (OptimizationRunStateMachine.CanTransition(run.Status, OptimizationRunStatus.Failed))
            {
                run.FailureCategory = OptimizationFailureCategory.Timeout;
                OptimizationRunStateMachine.Transition(
                    run,
                    OptimizationRunStatus.Failed,
                    UtcNow,
                    $"Aggregate timeout exceeded ({config?.MaxRunTimeoutMinutes ?? 0} minutes)");
            }
            await writeCtx.SaveChangesAsync(ct);
            _metrics.OptimizationRunsFailed.Add(1);

            await TryLogDecisionAsync(
                mediator,
                new LogDecisionCommand
                {
                    EntityType = "OptimizationRun",
                    EntityId = run.Id,
                    DecisionType = "OptimizationFailed",
                    Outcome = "Timeout",
                    Reason = run.ErrorMessage ?? $"Aggregate timeout exceeded ({config?.MaxRunTimeoutMinutes ?? 0} minutes)",
                    Source = "OptimizationWorker"
                },
                ct,
                "OptimizationRunProcessor: failure audit log failed for timed-out run {RunId} (non-fatal)",
                run.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (await HasDurablePersistedTerminalResultAsync(writeDb, run.Id, CancellationToken.None))
            {
                _logger.LogWarning(
                    "OptimizationRunProcessor: shutdown cancellation arrived after completion persistence for run {RunId} — keeping status {Status}",
                    run.Id,
                    run.Status);
                return;
            }

            _logger.LogWarning(
                "OptimizationRunProcessor: run {RunId} interrupted by shutdown — re-queuing with intermediate results",
                run.Id);
            OptimizationRunLifecycle.RequeueForRecovery(run, UtcNow);

            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await writeCtx.SaveChangesAsync(shutdownCts.Token);
                _logger.LogInformation(
                    "OptimizationRunProcessor: run {RunId} successfully re-queued with checkpoint data",
                    run.Id);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(
                    saveEx,
                    "OptimizationRunProcessor: failed to re-queue run {RunId} during shutdown — crash recovery will reclaim it on next startup",
                    run.Id);
            }
        }
        catch (Exception ex)
        {
            if (await HasDurablePersistedTerminalResultAsync(writeDb, run.Id, CancellationToken.None))
            {
                _logger.LogError(ex,
                    "OptimizationRunProcessor: post-completion step failed for run {RunId} after result persistence — keeping status {Status}",
                    run.Id,
                    run.Status);
                return;
            }

            _logger.LogError(ex, "OptimizationRunProcessor: run {RunId} failed", run.Id);
            if (OptimizationRunStateMachine.CanTransition(run.Status, OptimizationRunStatus.Failed))
            {
                run.FailureCategory = OptimizationFailureClassifier.Classify(ex);
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Failed, UtcNow, ex.Message);
            }
            else
            {
                _logger.LogWarning(
                    "OptimizationRunProcessor: preserving terminal run status {Status} for run {RunId} after downstream error",
                    run.Status,
                    run.Id);
            }
            await writeCtx.SaveChangesAsync(ct);

            _metrics.OptimizationRunsFailed.Add(1);

            await TryLogDecisionAsync(
                mediator,
                new LogDecisionCommand
                {
                    EntityType = "OptimizationRun",
                    EntityId = run.Id,
                    DecisionType = "OptimizationFailed",
                    Outcome = "Failed",
                    Reason = ex.Message,
                    Source = "OptimizationWorker"
                },
                ct,
                "OptimizationRunProcessor: failure audit log failed for run {RunId} (non-fatal)",
                run.Id);
        }
        finally
        {
            if (leaseHeartbeatCts is not null)
            {
                leaseHeartbeatCts.Cancel();
                if (leaseHeartbeatTask is not null)
                {
                    try
                    {
                        await leaseHeartbeatTask;
                    }
                    catch (OperationCanceledException) when (leaseHeartbeatCts.IsCancellationRequested)
                    {
                        // Normal shutdown for the background lease heartbeat.
                    }
                }

                leaseHeartbeatCts.Dispose();
            }

            runCts?.Dispose();
        }
    }

    private async Task TryLogDecisionAsync(
        IMediator mediator,
        LogDecisionCommand command,
        CancellationToken ct,
        string failureMessage,
        params object?[] args)
    {
        try
        {
            await mediator.Send(command, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureMessage, args);
        }
    }

    private static async Task<bool> HasDurablePersistedTerminalResultAsync(
        DbContext writeDb,
        long runId,
        CancellationToken ct)
    {
        var current = await writeDb.Set<OptimizationRun>()
            .AsNoTracking()
            .Where(r => r.Id == runId && !r.IsDeleted)
            .Select(r => new
            {
                r.Status,
                r.ResultsPersistedAt
            })
            .FirstOrDefaultAsync(ct);

        return current is not null
            && current.ResultsPersistedAt.HasValue
            && current.Status is OptimizationRunStatus.Completed
                or OptimizationRunStatus.Approved
                or OptimizationRunStatus.Rejected;
    }

    private static void SetRunStage(
        OptimizationRun run,
        OptimizationExecutionStage stage,
        string? message,
        DateTime utcNow)
        => OptimizationRunProgressTracker.SetStage(run, stage, message, utcNow);
}
