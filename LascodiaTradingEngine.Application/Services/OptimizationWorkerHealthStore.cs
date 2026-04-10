using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Singleton, typeof(IOptimizationWorkerHealthStore))]
public sealed class OptimizationWorkerHealthStore : IOptimizationWorkerHealthStore
{
    private const int QueueWaitSampleLimit = 128;
    private static readonly TimeSpan PhaseEventWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan PhaseBackoffBase = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PhaseBackoffMax = TimeSpan.FromMinutes(15);
    private const int PhaseFailureBackoffThreshold = 3;

    private sealed class PhaseRuntimeState
    {
        public OptimizationWorkerPhaseStateSnapshot Snapshot { get; set; } = new();
        public Queue<DateTime> SuccessTimestampsUtc { get; } = [];
        public Queue<DateTime> FailureTimestampsUtc { get; } = [];
        public Queue<DateTime> SkipTimestampsUtc { get; } = [];
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

    public OptimizationWorkerPhaseExecutionDecision GetPhaseExecutionDecision(string phaseName, DateTime utcNow)
    {
        lock (_gate)
        {
            var phaseState = GetOrCreatePhaseState(phaseName);
            PrunePhaseEvents(phaseState.SuccessTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.FailureTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.SkipTimestampsUtc, utcNow);

            if (phaseState.Snapshot.BackoffUntilUtc.HasValue
                && phaseState.Snapshot.BackoffUntilUtc.Value > utcNow)
            {
                return new OptimizationWorkerPhaseExecutionDecision(
                    ShouldExecute: false,
                    BackoffUntilUtc: phaseState.Snapshot.BackoffUntilUtc,
                    Reason: phaseState.Snapshot.LastSkipReason
                        ?? phaseState.Snapshot.LastFailureMessage
                        ?? $"{phaseName} is temporarily degraded");
            }

            return new OptimizationWorkerPhaseExecutionDecision(
                ShouldExecute: true,
                BackoffUntilUtc: null,
                Reason: null);
        }
    }

    public void RecordPhaseStarted(string phaseName, DateTime utcNow)
    {
        lock (_gate)
        {
            var phaseState = GetOrCreatePhaseState(phaseName);
            PrunePhaseEvents(phaseState.SuccessTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.FailureTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.SkipTimestampsUtc, utcNow);
            phaseState.Snapshot = phaseState.Snapshot with
            {
                PhaseName = phaseName,
                LastStartedAtUtc = utcNow,
                SuccessesLastHour = phaseState.SuccessTimestampsUtc.Count,
                FailuresLastHour = phaseState.FailureTimestampsUtc.Count,
                SkippedExecutionsLastHour = phaseState.SkipTimestampsUtc.Count
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
                FailuresLastHour = phaseState.FailureTimestampsUtc.Count,
                IsDegraded = false,
                BackoffUntilUtc = null,
                LastSkipReason = null,
                SkippedExecutionsLastHour = phaseState.SkipTimestampsUtc.Count
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
            PrunePhaseEvents(phaseState.SkipTimestampsUtc, utcNow);
            phaseState.FailureTimestampsUtc.Enqueue(utcNow);
            int consecutiveFailures = phaseState.Snapshot.ConsecutiveFailures + 1;
            DateTime? backoffUntilUtc = null;
            if (consecutiveFailures >= PhaseFailureBackoffThreshold)
            {
                int exponent = Math.Min(6, consecutiveFailures - PhaseFailureBackoffThreshold);
                long ticks = PhaseBackoffBase.Ticks << exponent;
                if (ticks < 0)
                    ticks = PhaseBackoffMax.Ticks;

                var backoff = TimeSpan.FromTicks(Math.Min(ticks, PhaseBackoffMax.Ticks));
                backoffUntilUtc = utcNow.Add(backoff);
            }

            phaseState.Snapshot = phaseState.Snapshot with
            {
                PhaseName = phaseName,
                LastCompletedAtUtc = utcNow,
                LastFailureAtUtc = utcNow,
                LastFailureType = errorType,
                LastFailureMessage = errorMessage.Length <= 500 ? errorMessage : errorMessage[..500],
                ConsecutiveFailures = consecutiveFailures,
                LastDurationMs = Math.Max(0, durationMs),
                SuccessesLastHour = phaseState.SuccessTimestampsUtc.Count,
                FailuresLastHour = phaseState.FailureTimestampsUtc.Count,
                IsDegraded = true,
                BackoffUntilUtc = backoffUntilUtc,
                SkippedExecutionsLastHour = phaseState.SkipTimestampsUtc.Count,
            };
        }
    }

    public void RecordPhaseSkipped(string phaseName, string reason, DateTime? backoffUntilUtc, DateTime utcNow)
    {
        lock (_gate)
        {
            var phaseState = GetOrCreatePhaseState(phaseName);
            PrunePhaseEvents(phaseState.SuccessTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.FailureTimestampsUtc, utcNow);
            PrunePhaseEvents(phaseState.SkipTimestampsUtc, utcNow);
            phaseState.SkipTimestampsUtc.Enqueue(utcNow);
            phaseState.Snapshot = phaseState.Snapshot with
            {
                PhaseName = phaseName,
                IsDegraded = true,
                BackoffUntilUtc = backoffUntilUtc,
                LastSkippedAtUtc = utcNow,
                LastSkipReason = reason.Length <= 500 ? reason : reason[..500],
                SuccessesLastHour = phaseState.SuccessTimestampsUtc.Count,
                FailuresLastHour = phaseState.FailureTimestampsUtc.Count,
                SkippedExecutionsLastHour = phaseState.SkipTimestampsUtc.Count
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
