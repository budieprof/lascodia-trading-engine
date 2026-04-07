using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Options;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCorrelationCoordinator))]
internal sealed class StrategyGenerationCorrelationCoordinator : IStrategyGenerationCorrelationCoordinator
{
    private readonly string[][] _correlationGroups;

    public StrategyGenerationCorrelationCoordinator(CorrelationGroupOptions correlationGroups)
    {
        _correlationGroups = correlationGroups.Groups;
    }

    public Dictionary<int, int> BuildInitialCounts(IReadOnlyList<string> activeSymbols)
    {
        var counts = new Dictionary<int, int>();
        foreach (var symbol in activeSymbols)
        {
            int? groupIdx = FindCorrelationGroupIndex(symbol);
            if (groupIdx.HasValue)
                counts[groupIdx.Value] = counts.GetValueOrDefault(groupIdx.Value) + 1;
        }

        return counts;
    }

    public bool IsSaturated(string symbol, Dictionary<int, int> groupCounts, int maxPerGroup)
    {
        int? groupIdx = FindCorrelationGroupIndex(symbol);
        return groupIdx.HasValue && groupCounts.GetValueOrDefault(groupIdx.Value) >= maxPerGroup;
    }

    public void IncrementCount(string symbol, Dictionary<int, int> groupCounts)
    {
        int? groupIdx = FindCorrelationGroupIndex(symbol);
        if (groupIdx.HasValue)
            groupCounts[groupIdx.Value] = groupCounts.GetValueOrDefault(groupIdx.Value) + 1;
    }

    private int? FindCorrelationGroupIndex(string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        for (int i = 0; i < _correlationGroups.Length; i++)
        {
            if (_correlationGroups[i].Any(s => s.Equals(upper, StringComparison.OrdinalIgnoreCase)))
                return i;
        }

        return null;
    }
}
