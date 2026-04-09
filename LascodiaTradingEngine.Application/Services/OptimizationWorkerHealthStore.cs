using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Singleton, typeof(IOptimizationWorkerHealthStore))]
public sealed class OptimizationWorkerHealthStore : IOptimizationWorkerHealthStore
{
    private const int QueueWaitSampleLimit = 128;
    private static readonly TimeSpan PhaseEventWindow = TimeSpan.FromHours(1);

    private sealed class PhaseRuntimeState
    {
        public OptimizationWorkerPhaseStateSnapshot Snapshot { get; set; } = new();
        public Queue<DateTime> SuccessTimestampsUtc { get; } = [];
        public Queue<DateTime> FailureTimestampsUtc { get; } = [];
    }

    private readonly object _gate = new();
    private OptimizationWorkerHealthStateSnapshot _mainSnapshot = new();
    private readonly Queue<long> _queueWaitSamplesMs = [];
    private readonly Dictionary<string, PhaseRuntimeState> _phaseStates = new(StringComparer.Ordinal);

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

    public void RecordPhaseStarted(string phaseName, DateTime utcNow)
    {
        lock (_gate)
        {
            var phaseState = GetOrCreatePhaseState(phaseName);
            phaseState.Snapshot = phaseState.Snapshot with
            {
                PhaseName = phaseName,
                LastStartedAtUtc = utcNow
            };
        }
    }

    public void RecordPhaseSuccess(string phaseName, long durationMs, DateTime utcNow)
    {
        lock (_gate)
        {
            var phaseState = GetOrCreatePhaseState(phaseName);
            PrunePhaseEvents(phaseState.SuccessTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.FailureTimestampsUtc, utcNow);
            phaseState.SuccessTimestampsUtc.Enqueue(utcNow);
            phaseState.Snapshot = phaseState.Snapshot with
            {
                PhaseName = phaseName,
                LastCompletedAtUtc = utcNow,
                LastSuccessAtUtc = utcNow,
                ConsecutiveFailures = 0,
                LastDurationMs = Math.Max(0, durationMs),
                LastSuccessDurationMs = Math.Max(0, durationMs),
                SuccessesLastHour = phaseState.SuccessTimestampsUtc.Count,
                FailuresLastHour = phaseState.FailureTimestampsUtc.Count
            };
        }
    }

    public void RecordPhaseFailure(string phaseName, string errorType, string errorMessage, long durationMs, DateTime utcNow)
    {
        lock (_gate)
        {
            var phaseState = GetOrCreatePhaseState(phaseName);
            PrunePhaseEvents(phaseState.SuccessTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.FailureTimestampsUtc, utcNow);
            phaseState.FailureTimestampsUtc.Enqueue(utcNow);
            phaseState.Snapshot = phaseState.Snapshot with
            {
                PhaseName = phaseName,
                LastCompletedAtUtc = utcNow,
                LastFailureAtUtc = utcNow,
                LastFailureType = errorType,
                LastFailureMessage = errorMessage.Length <= 500 ? errorMessage : errorMessage[..500],
                ConsecutiveFailures = phaseState.Snapshot.ConsecutiveFailures + 1,
                LastDurationMs = Math.Max(0, durationMs),
                SuccessesLastHour = phaseState.SuccessTimestampsUtc.Count,
                FailuresLastHour = phaseState.FailureTimestampsUtc.Count,
            };
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

    public IReadOnlyList<OptimizationWorkerPhaseStateSnapshot> GetPhaseStates()
    {
        lock (_gate)
        {
            return _phaseStates.Values
                .Select(phase => phase.Snapshot)
                .OrderBy(phase => phase.PhaseName, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private PhaseRuntimeState GetOrCreatePhaseState(string phaseName)
    {
        if (_phaseStates.TryGetValue(phaseName, out var current))
            return current;

        current = new PhaseRuntimeState
        {
            Snapshot = new OptimizationWorkerPhaseStateSnapshot
            {
                PhaseName = phaseName
            }
        };
        _phaseStates[phaseName] = current;
        return current;
    }

    private static void PrunePhaseEvents(Queue<DateTime> timestampsUtc, DateTime nowUtc)
    {
        while (timestampsUtc.Count > 0 && nowUtc - timestampsUtc.Peek() > PhaseEventWindow)
            timestampsUtc.Dequeue();
    }

    private static long GetPercentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
