using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton, typeof(IOptimizationWorkerLoopCoordinator))]
internal sealed class OptimizationWorkerLoopCoordinator : IOptimizationWorkerLoopCoordinator
{
    /// <summary>Per-phase timeout defaults (minutes). Configurable via <see cref="OptimizationConfig"/>.</summary>
    private static class PhaseTimeoutDefaults
    {
        internal const int Recovery = 10;
        internal const int Scheduling = 5;
        internal const int Execution = 30;
        internal const int FollowUp = 10;
        internal const int HealthRecording = 5;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OptimizationWorkerLoopCoordinator> _logger;
    private readonly OptimizationRunRecoveryCoordinator _recoveryCoordinator;
    private readonly OptimizationFollowUpCoordinator _followUpCoordinator;
    private readonly OptimizationSchedulingCoordinator _schedulingCoordinator;
    private readonly OptimizationWorkerHealthRecorder _healthRecorder;
    private readonly IOptimizationWorkerHealthStore _optimizationHealthStore;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationWorkerLoopCoordinator(
        IServiceScopeFactory scopeFactory,
        ILogger<OptimizationWorkerLoopCoordinator> logger,
        OptimizationRunRecoveryCoordinator recoveryCoordinator,
        OptimizationFollowUpCoordinator followUpCoordinator,
        OptimizationSchedulingCoordinator schedulingCoordinator,
        OptimizationWorkerHealthRecorder healthRecorder,
        IOptimizationWorkerHealthStore optimizationHealthStore,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _recoveryCoordinator = recoveryCoordinator;
        _followUpCoordinator = followUpCoordinator;
        _schedulingCoordinator = schedulingCoordinator;
        _healthRecorder = healthRecorder;
        _optimizationHealthStore = optimizationHealthStore;
        _timeProvider = timeProvider;
    }

    public Task WarmStartAsync(CancellationToken ct)
        => ExecutePhaseWithTimeoutAsync(
            OptimizationWorkerHealthNames.Phases.WarmStart,
            PhaseTimeoutDefaults.Recovery,
            (linkedCt) => _recoveryCoordinator.RecoverStaleRunningRunsAsync(linkedCt),
            ct);

    public async Task ExecuteCycleAsync(OptimizationWorkerCycleContext cycleContext, CancellationToken ct)
    {
        var staleRunningSummary = await ExecuteCyclePhaseAsync(
            OptimizationWorkerHealthNames.Phases.StaleRunningRecovery,
            PhaseTimeoutDefaults.Recovery,
            (linkedCt) => _recoveryCoordinator.RecoverStaleRunningRunsAsync(linkedCt),
            ct);
        await ExecuteCyclePhaseAsync(
            OptimizationWorkerHealthNames.Phases.StaleQueuedDetection,
            PhaseTimeoutDefaults.Recovery,
            (linkedCt) => _recoveryCoordinator.RecoverStaleQueuedRunsAsync(linkedCt),
            ct);
        await ExecuteCyclePhaseAsync(
            OptimizationWorkerHealthNames.Phases.RetryFailedRuns,
            PhaseTimeoutDefaults.Recovery,
            (linkedCt) => _recoveryCoordinator.RetryFailedRunsAsync(cycleContext.Config, linkedCt),
            ct);
        var reconciliationSummary = await ExecuteCyclePhaseAsync(
            OptimizationWorkerHealthNames.Phases.LifecycleReconciliation,
            PhaseTimeoutDefaults.Execution,
            (linkedCt) => _recoveryCoordinator.ReconcileLifecycleStateAsync(cycleContext.Config, linkedCt),
            ct);
        await ExecuteCyclePhaseAsync(
            OptimizationWorkerHealthNames.Phases.FollowUpMonitoring,
            PhaseTimeoutDefaults.FollowUp,
            (linkedCt) => _followUpCoordinator.MonitorAsync(cycleContext.Config, linkedCt),
            ct);

        if (cycleContext.ShouldRunScheduling)
        {
            await ExecuteCyclePhaseAsync(
                OptimizationWorkerHealthNames.Phases.AutoScheduling,
                PhaseTimeoutDefaults.Scheduling,
                async (linkedCt) =>
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    await _schedulingCoordinator.AutoScheduleUnderperformersAsync(readCtx, writeCtx, cycleContext.Config, linkedCt);
                },
                ct);
        }

        await ExecuteCyclePhaseAsync(
            OptimizationWorkerHealthNames.Phases.HealthRecording,
            PhaseTimeoutDefaults.HealthRecording,
            (linkedCt) => _healthRecorder.RecordAsync(
                cycleContext.Config,
                cycleContext.LastConfigRefreshUtc,
                cycleContext.NextConfigRefreshUtc,
                staleRunningSummary,
                reconciliationSummary,
                linkedCt),
            ct);
    }

    private async Task ExecuteCyclePhaseAsync(
        string phaseName,
        int timeoutMinutes,
        Func<CancellationToken, Task> work,
        CancellationToken ct)
    {
        var decision = _optimizationHealthStore.GetPhaseExecutionDecision(phaseName, UtcNow);
        if (!decision.ShouldExecute)
        {
            _optimizationHealthStore.RecordPhaseSkipped(
                phaseName,
                decision.Reason ?? $"{phaseName} skipped because backoff is active",
                decision.BackoffUntilUtc,
                UtcNow);
            return;
        }

        try
        {
            await ExecutePhaseWithTimeoutAsync(phaseName, timeoutMinutes, work, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Phase timeout — already recorded as failure in ExecutePhaseWithTimeoutAsync.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Phase failure is already captured in the typed phase health snapshot.
        }
    }

    private async Task<T?> ExecuteCyclePhaseAsync<T>(
        string phaseName,
        int timeoutMinutes,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        var decision = _optimizationHealthStore.GetPhaseExecutionDecision(phaseName, UtcNow);
        if (!decision.ShouldExecute)
        {
            _optimizationHealthStore.RecordPhaseSkipped(
                phaseName,
                decision.Reason ?? $"{phaseName} skipped because backoff is active",
                decision.BackoffUntilUtc,
                UtcNow);
            return default;
        }

        try
        {
            return await ExecutePhaseWithTimeoutAsync(phaseName, timeoutMinutes, work, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Phase timeout — already recorded as failure in ExecutePhaseWithTimeoutAsync.
            return default;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return default;
        }
    }

    private async Task ExecutePhaseWithTimeoutAsync(
        string phaseName,
        int timeoutMinutes,
        Func<CancellationToken, Task> work,
        CancellationToken ct)
    {
        using var phaseTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, phaseTimeout.Token);
        var stopwatch = Stopwatch.StartNew();
        _optimizationHealthStore.RecordPhaseStarted(phaseName, UtcNow);
        try
        {
            await work(linked.Token);
            _optimizationHealthStore.RecordPhaseSuccess(phaseName, stopwatch.ElapsedMilliseconds, UtcNow);
        }
        catch (OperationCanceledException) when (phaseTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Phase {Phase} timed out after {Minutes} minutes",
                phaseName,
                timeoutMinutes);
            _optimizationHealthStore.RecordPhaseFailure(
                phaseName,
                "PhaseTimeout",
                $"Phase timed out after {timeoutMinutes} minutes",
                stopwatch.ElapsedMilliseconds,
                UtcNow);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _optimizationHealthStore.RecordPhaseFailure(phaseName, ex.GetType().Name, ex.Message, stopwatch.ElapsedMilliseconds, UtcNow);
            throw;
        }
    }

    private async Task<T> ExecutePhaseWithTimeoutAsync<T>(
        string phaseName,
        int timeoutMinutes,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        using var phaseTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, phaseTimeout.Token);
        var stopwatch = Stopwatch.StartNew();
        _optimizationHealthStore.RecordPhaseStarted(phaseName, UtcNow);
        try
        {
            var result = await work(linked.Token);
            _optimizationHealthStore.RecordPhaseSuccess(phaseName, stopwatch.ElapsedMilliseconds, UtcNow);
            return result;
        }
        catch (OperationCanceledException) when (phaseTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Phase {Phase} timed out after {Minutes} minutes",
                phaseName,
                timeoutMinutes);
            _optimizationHealthStore.RecordPhaseFailure(
                phaseName,
                "PhaseTimeout",
                $"Phase timed out after {timeoutMinutes} minutes",
                stopwatch.ElapsedMilliseconds,
                UtcNow);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _optimizationHealthStore.RecordPhaseFailure(phaseName, ex.GetType().Name, ex.Message, stopwatch.ElapsedMilliseconds, UtcNow);
            throw;
        }
    }
}
