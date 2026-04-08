namespace LascodiaTradingEngine.Application.Optimization;

internal readonly record struct OptimizationWorkerCycleContext(
    OptimizationConfig Config,
    DateTime LastConfigRefreshUtc,
    DateTime NextConfigRefreshUtc,
    bool ShouldRunScheduling);

internal interface IOptimizationWorkerLoopCoordinator
{
    Task WarmStartAsync(CancellationToken ct);
    Task ExecuteCycleAsync(OptimizationWorkerCycleContext cycleContext, CancellationToken ct);
}
