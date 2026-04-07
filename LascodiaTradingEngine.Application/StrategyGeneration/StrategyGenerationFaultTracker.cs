using System.Collections.Concurrent;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

internal sealed class StrategyGenerationFaultTracker
{
    private readonly ConcurrentDictionary<StrategyType, int> _faults = new();
    private readonly int _maxFaultsPerType;

    public StrategyGenerationFaultTracker(int maxFaultsPerType)
    {
        _maxFaultsPerType = maxFaultsPerType;
    }

    public void RecordFault(StrategyType type) => _faults.AddOrUpdate(type, 1, (_, count) => count + 1);

    public bool IsTypeDisabled(StrategyType type)
        => _faults.TryGetValue(type, out var count) && count >= _maxFaultsPerType;

    public IReadOnlyDictionary<StrategyType, int> GetFaultCounts()
        => _faults.ToDictionary(kv => kv.Key, kv => kv.Value);
}
