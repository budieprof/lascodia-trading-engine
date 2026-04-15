using System.Collections.Concurrent;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// In-memory breaker that temporarily disables strategy types after repeated generation faults.
/// </summary>
internal sealed class StrategyGenerationFaultTracker
{
    private readonly ConcurrentDictionary<StrategyType, int> _faults = new();
    private readonly int _maxFaultsPerType;

    public StrategyGenerationFaultTracker(int maxFaultsPerType)
    {
        _maxFaultsPerType = maxFaultsPerType;
    }

    /// <summary>
    /// Records one additional fault against a strategy type.
    /// </summary>
    public void RecordFault(StrategyType type) => _faults.AddOrUpdate(type, 1, (_, count) => count + 1);

    /// <summary>
    /// Returns <c>true</c> when a strategy type has reached the configured fault ceiling.
    /// </summary>
    public bool IsTypeDisabled(StrategyType type)
        => _faults.TryGetValue(type, out var count) && count >= _maxFaultsPerType;

    /// <summary>
    /// Returns the current per-type fault counts for diagnostics and logging.
    /// </summary>
    public IReadOnlyDictionary<StrategyType, int> GetFaultCounts()
        => _faults.ToDictionary(kv => kv.Key, kv => kv.Value);
}
