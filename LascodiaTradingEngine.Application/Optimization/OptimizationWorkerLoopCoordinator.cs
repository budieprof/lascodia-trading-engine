using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton, typeof(IOptimizationWorkerLoopCoordinator))]
internal sealed class OptimizationWorkerLoopCoordinator : IOptimizationWorkerLoopCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OptimizationRunRecoveryCoordinator _recoveryCoordinator;
    private readonly OptimizationFollowUpCoordinator _followUpCoordinator;
    private readonly OptimizationSchedulingCoordinator _schedulingCoordinator;
    private readonly OptimizationWorkerHealthRecorder _healthRecorder;
    private readonly IOptimizationWorkerHealthStore _optimizationHealthStore;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationWorkerLoopCoordinator(
        IServiceScopeFactory scopeFactory,
        OptimizationRunRecoveryCoordinator recoveryCoordinator,
        OptimizationFollowUpCoordinator followUpCoordinator,
        OptimizationSchedulingCoordinator schedulingCoordinator,
        OptimizationWorkerHealthRecorder healthRecorder,
        IOptimizationWorkerHealthStore optimizationHealthStore,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _recoveryCoordinator = recoveryCoordinator;
        _followUpCoordinator = followUpCoordinator;
        _schedulingCoordinator = schedulingCoordinator;
        _healthRecorder = healthRecorder;
        _optimizationHealthStore = optimizationHealthStore;
        _timeProvider = timeProvider;
    }

    public Task WarmStartAsync(CancellationToken ct)
        => ExecutePhaseAsync(
            OptimizationWorkerHealthNames.Phases.WarmStart,
            () => _recoveryCoordinator.RecoverStaleRunningRunsAsync(ct));

    public async Task ExecuteCycleAsync(OptimizationWorkerCycleContext cycleContext, CancellationToken ct)
    {
        var staleRunningSummary = await ExecutePhaseAsync(
            OptimizationWorkerHealthNames.Phases.StaleRunningRecovery,
            () => _recoveryCoordinator.RecoverStaleRunningRunsAsync(ct));
        await ExecutePhaseAsync(
            OptimizationWorkerHealthNames.Phases.StaleQueuedDetection,
            () => _recoveryCoordinator.RecoverStaleQueuedRunsAsync(ct));
        await ExecutePhaseAsync(
            OptimizationWorkerHealthNames.Phases.RetryFailedRuns,
            () => _recoveryCoordinator.RetryFailedRunsAsync(cycleContext.Config, ct));
        var reconciliationSummary = await ExecutePhaseAsync(
            OptimizationWorkerHealthNames.Phases.LifecycleReconciliation,
            () => _recoveryCoordinator.ReconcileLifecycleStateAsync(cycleContext.Config, ct));
        await ExecutePhaseAsync(
            OptimizationWorkerHealthNames.Phases.FollowUpMonitoring,
            () => _followUpCoordinator.MonitorAsync(cycleContext.Config, ct));

        if (cycleContext.ShouldRunScheduling)
        {
            await ExecutePhaseAsync(
                OptimizationWorkerHealthNames.Phases.AutoScheduling,
                async () =>
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                    var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    await _schedulingCoordinator.AutoScheduleUnderperformersAsync(readCtx, writeCtx, cycleContext.Config, ct);
                });
        }

        await ExecutePhaseAsync(
            OptimizationWorkerHealthNames.Phases.HealthRecording,
            () => _healthRecorder.RecordAsync(
                cycleContext.Config,
                cycleContext.LastConfigRefreshUtc,
                cycleContext.NextConfigRefreshUtc,
                staleRunningSummary,
                reconciliationSummary,
                ct));
    }

    private async Task ExecutePhaseAsync(string phaseName, Func<Task> work)
    {
        var stopwatch = Stopwatch.StartNew();
        _optimizationHealthStore.RecordPhaseStarted(phaseName, UtcNow);
        try
        {
            await work();
            _optimizationHealthStore.RecordPhaseSuccess(phaseName, stopwatch.ElapsedMilliseconds, UtcNow);
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

    private async Task<T> ExecutePhaseAsync<T>(string phaseName, Func<Task<T>> work)
    {
        var stopwatch = Stopwatch.StartNew();
        _optimizationHealthStore.RecordPhaseStarted(phaseName, UtcNow);
        try
        {
            var result = await work();
            _optimizationHealthStore.RecordPhaseSuccess(phaseName, stopwatch.ElapsedMilliseconds, UtcNow);
            return result;
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
