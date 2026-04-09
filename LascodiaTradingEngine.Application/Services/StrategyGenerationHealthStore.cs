using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationHealthStore))]
public sealed class StrategyGenerationHealthStore : IStrategyGenerationHealthStore
{
    private readonly object _gate = new();
    private StrategyGenerationHealthStateSnapshot _state = new();
    private readonly Dictionary<string, StrategyGenerationPhaseStateSnapshot> _phaseStates = new(StringComparer.Ordinal);

    public void UpdateState(StrategyGenerationHealthStateSnapshot snapshot)
    {
        lock (_gate)
            _state = snapshot;
    }

    public void UpdateState(Func<StrategyGenerationHealthStateSnapshot, StrategyGenerationHealthStateSnapshot> updater)
    {
        lock (_gate)
            _state = updater(_state);
    }

    public void RecordPhaseSuccess(string phaseName, long durationMs, DateTime utcNow)
    {
        lock (_gate)
        {
            _phaseStates.TryGetValue(phaseName, out var current);
            _phaseStates[phaseName] = (current ?? new StrategyGenerationPhaseStateSnapshot
            {
                PhaseName = phaseName
            }) with
            {
                PhaseName = phaseName,
                LastSuccessAtUtc = utcNow,
                ConsecutiveFailures = 0,
                LastDurationMs = Math.Max(0, durationMs),
            };
        }
    }

    public void RecordPhaseFailure(string phaseName, string errorMessage, DateTime utcNow)
    {
        lock (_gate)
        {
            _phaseStates.TryGetValue(phaseName, out var current);
            current ??= new StrategyGenerationPhaseStateSnapshot
            {
                PhaseName = phaseName
            };
            _phaseStates[phaseName] = current with
            {
                PhaseName = phaseName,
                LastFailureAtUtc = utcNow,
                LastFailureMessage = errorMessage.Length <= 500 ? errorMessage : errorMessage[..500],
                ConsecutiveFailures = current.ConsecutiveFailures + 1,
            };
        }
    }

    public StrategyGenerationHealthStateSnapshot GetState()
    {
        lock (_gate)
            return _state;
    }

    public IReadOnlyList<StrategyGenerationPhaseStateSnapshot> GetPhaseStates()
    {
        lock (_gate)
        {
            return _phaseStates.Values
                .OrderBy(phase => phase.PhaseName, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
