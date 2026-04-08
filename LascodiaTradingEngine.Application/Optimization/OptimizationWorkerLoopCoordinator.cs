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

    public OptimizationWorkerLoopCoordinator(
        IServiceScopeFactory scopeFactory,
        OptimizationRunRecoveryCoordinator recoveryCoordinator,
        OptimizationFollowUpCoordinator followUpCoordinator,
        OptimizationSchedulingCoordinator schedulingCoordinator,
        OptimizationWorkerHealthRecorder healthRecorder)
    {
        _scopeFactory = scopeFactory;
        _recoveryCoordinator = recoveryCoordinator;
        _followUpCoordinator = followUpCoordinator;
        _schedulingCoordinator = schedulingCoordinator;
        _healthRecorder = healthRecorder;
    }

    public Task WarmStartAsync(CancellationToken ct)
        => _recoveryCoordinator.RecoverStaleRunningRunsAsync(ct);

    public async Task ExecuteCycleAsync(OptimizationWorkerCycleContext cycleContext, CancellationToken ct)
    {
        await _recoveryCoordinator.RequeueExpiredRunningRunsAsync(ct);
        await _recoveryCoordinator.RecoverStaleQueuedRunsAsync(ct);
        await _recoveryCoordinator.RetryFailedRunsAsync(cycleContext.Config, ct);
        var reconciliationSummary = await _recoveryCoordinator.ReconcileLifecycleStateAsync(cycleContext.Config, ct);
        await _followUpCoordinator.MonitorAsync(cycleContext.Config, ct);

        if (cycleContext.ShouldRunScheduling)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            await _schedulingCoordinator.AutoScheduleUnderperformersAsync(readCtx, writeCtx, cycleContext.Config, ct);
        }

        await _healthRecorder.RecordAsync(
            cycleContext.Config,
            cycleContext.LastConfigRefreshUtc,
            cycleContext.NextConfigRefreshUtc,
            reconciliationSummary,
            ct);
    }
}
