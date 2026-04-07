using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Singleton, typeof(IOptimizationWorkerHealthStore))]
public sealed class OptimizationWorkerHealthStore : IOptimizationWorkerHealthStore
{
    private readonly object _gate = new();
    private OptimizationWorkerHealthStateSnapshot _mainSnapshot = new();

    public void UpdateMainWorkerState(OptimizationWorkerHealthStateSnapshot snapshot)
    {
        lock (_gate)
            _mainSnapshot = snapshot;
    }

    public OptimizationWorkerHealthStateSnapshot GetMainWorkerState()
    {
        lock (_gate)
            return _mainSnapshot;
    }
}
