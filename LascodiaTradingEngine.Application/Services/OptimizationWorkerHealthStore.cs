using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Singleton, typeof(IOptimizationWorkerHealthStore))]
public sealed class OptimizationWorkerHealthStore : IOptimizationWorkerHealthStore
{
    private const int QueueWaitSampleLimit = 128;
    private readonly object _gate = new();
    private OptimizationWorkerHealthStateSnapshot _mainSnapshot = new();
    private readonly Queue<long> _queueWaitSamplesMs = [];

    public void UpdateMainWorkerState(OptimizationWorkerHealthStateSnapshot snapshot)
    {
        lock (_gate)
            _mainSnapshot = snapshot;
    }

    public void UpdateMainWorkerState(Func<OptimizationWorkerHealthStateSnapshot, OptimizationWorkerHealthStateSnapshot> updater)
    {
        lock (_gate)
            _mainSnapshot = updater(_mainSnapshot);
    }

    public void RecordQueueWaitSample(long queueWaitMs)
    {
        lock (_gate)
        {
            _queueWaitSamplesMs.Enqueue(Math.Max(0, queueWaitMs));
            while (_queueWaitSamplesMs.Count > QueueWaitSampleLimit)
                _queueWaitSamplesMs.Dequeue();
        }
    }

    public QueueWaitPercentileSnapshot GetQueueWaitPercentiles()
    {
        lock (_gate)
        {
            var samples = _queueWaitSamplesMs.ToArray();
            Array.Sort(samples);
            return new QueueWaitPercentileSnapshot(
                GetPercentile(samples, 0.50),
                GetPercentile(samples, 0.95),
                GetPercentile(samples, 0.99));
        }
    }

    public OptimizationWorkerHealthStateSnapshot GetMainWorkerState()
    {
        lock (_gate)
            return _mainSnapshot;
    }

    private static long GetPercentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        var index = (int)Math.Floor(percentile * (sorted.Length - 1));
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
