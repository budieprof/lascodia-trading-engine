using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Tracks which parameters changed most between baseline and approved params across
/// optimization runs. Over time this builds a map of which parameters matter most per
/// strategy type, enabling smarter initial sampling.
/// </summary>
internal static class ParameterImportanceTracker
{
    /// <summary>
    /// Computes the relative change per parameter between baseline and optimized params.
    /// Returns a dictionary of parameter name -> normalized importance (0-1).
    /// </summary>
    internal static Dictionary<string, double> ComputeParameterDeltas(
        string? baselineJson, string? optimizedJson)
    {
        var deltas = new Dictionary<string, double>();
        if (string.IsNullOrWhiteSpace(baselineJson) || string.IsNullOrWhiteSpace(optimizedJson))
            return deltas;

        Dictionary<string, JsonElement>? baseline, optimized;
        try
        {
            baseline = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baselineJson);
            optimized = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optimizedJson);
        }
        catch { return deltas; }

        if (baseline is null || optimized is null) return deltas;

        foreach (var (key, baseVal) in baseline)
        {
            if (!optimized.TryGetValue(key, out var optVal)) continue;
            if (!baseVal.TryGetDouble(out double baseD) || !optVal.TryGetDouble(out double optD)) continue;

            double denom = Math.Max(Math.Abs(baseD), Math.Abs(optD));
            if (denom == 0.0)
            {
                deltas[key] = 0.0;
                continue;
            }

            deltas[key] = Math.Abs(optD - baseD) / denom;
        }

        return deltas;
    }

    /// <summary>
    /// Aggregates parameter deltas across multiple runs into a cumulative importance map.
    /// Parameters that change more frequently and by larger amounts are ranked higher.
    /// </summary>
    internal static Dictionary<string, double> AggregateImportance(
        IEnumerable<Dictionary<string, double>> allDeltas)
    {
        var sumImportance = new Dictionary<string, double>();
        var counts = new Dictionary<string, int>();

        foreach (var deltas in allDeltas)
        {
            foreach (var (key, delta) in deltas)
            {
                sumImportance[key] = sumImportance.GetValueOrDefault(key) + delta;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        // Normalize: multiply average delta by frequency ratio
        var result = new Dictionary<string, double>();
        int maxCount = counts.Values.Count > 0 ? counts.Values.Max() : 1;

        foreach (var (key, sum) in sumImportance)
        {
            int count = counts[key];
            double avgDelta = sum / count;
            double frequencyBoost = (double)count / maxCount;
            result[key] = avgDelta * (0.7 + 0.3 * frequencyBoost); // Weight: 70% magnitude, 30% frequency
        }

        // Normalize to [0, 1]
        double maxImportance = result.Values.Count > 0 ? result.Values.Max() : 1.0;
        if (maxImportance > 0)
        {
            foreach (var key in result.Keys.ToList())
                result[key] /= maxImportance;
        }

        return result;
    }

    /// <summary>
    /// Serializes the importance map to JSON for storage in EngineConfig or run metadata.
    /// </summary>
    internal static string Serialize(Dictionary<string, double> importance)
        => JsonSerializer.Serialize(importance.OrderByDescending(kv => kv.Value)
            .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4)));

    /// <summary>
    /// Deserializes a stored importance map.
    /// </summary>
    internal static Dictionary<string, double> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, double>();
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new(); }
        catch { return new Dictionary<string, double>(); }
    }
}
