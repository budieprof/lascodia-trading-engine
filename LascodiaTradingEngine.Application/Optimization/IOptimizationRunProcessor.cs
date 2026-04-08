namespace LascodiaTradingEngine.Application.Optimization;

internal interface IOptimizationRunProcessor
{
    Task<bool> ProcessNextQueuedRunAsync(CancellationToken ct);
}
