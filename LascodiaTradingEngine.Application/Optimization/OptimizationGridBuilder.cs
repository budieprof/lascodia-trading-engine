using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Builds and manages parameter grids for strategy optimization, including grid construction
/// per strategy type, midpoint expansion, TPE bounds extraction, adaptive bounds narrowing,
/// and parameter type conversions.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class OptimizationGridBuilder
{
    private readonly ILogger<OptimizationGridBuilder> _logger;

    public OptimizationGridBuilder(ILogger<OptimizationGridBuilder> logger) => _logger = logger;

    /// <summary>Data-density-dependent evaluation protocol.</summary>
    internal sealed record DataProtocol(double TrainRatio, int KFolds, decimal ScorePenalty);

    /// <summary>Selects evaluation protocol based on data density.</summary>
    internal static DataProtocol GetDataProtocol(int candleCount, int scarcityThreshold) => candleCount switch
    {
        >= 500 => new(TrainRatio: 0.70, KFolds: 5, ScorePenalty: 0m),
        >= 200 => new(TrainRatio: 0.75, KFolds: 3, ScorePenalty: 0m),
        _      => new(TrainRatio: 0.80, KFolds: 2, ScorePenalty: 0.05m),
    };

    /// <summary>Extracts TPE bounds from the parameter grid (min/max per key).</summary>
    internal static Dictionary<string, (double Min, double Max, bool IsInteger)> ExtractTpeBounds(
        List<Dictionary<string, object>> grid)
    {
        var bounds = new Dictionary<string, (double Min, double Max, bool IsInteger)>();
        foreach (var paramSet in grid)
        foreach (var (key, value) in paramSet)
        {
            double dVal = value is int i ? i : value is double d ? d : 0;
            bool isInt  = value is int;
            if (!bounds.ContainsKey(key))
                bounds[key] = (dVal, dVal, isInt);
            else
            {
                var (min, max, wasInt) = bounds[key];
                bounds[key] = (Math.Min(min, dVal), Math.Max(max, dVal), wasInt && isInt);
            }
        }
        return bounds;
    }

    /// <summary>Narrows TPE bounds using historically approved parameter regions.</summary>
    internal static Dictionary<string, (double Min, double Max, bool IsInteger)> NarrowBoundsFromHistory(
        Dictionary<string, (double Min, double Max, bool IsInteger)> original,
        List<Dictionary<string, double>> history)
    {
        var result = new Dictionary<string, (double Min, double Max, bool IsInteger)>(original);
        foreach (var (key, (min, max, isInt)) in original)
        {
            var vals = history.Where(p => p.ContainsKey(key)).Select(p => p[key]).ToList();
            if (vals.Count < 3) continue;

            double range  = max - min;
            double margin = range * 0.1;
            double nMin   = Math.Max(min, vals.Min() - margin);
            double nMax   = Math.Min(max, vals.Max() + margin);

            if (nMax - nMin < range * 0.2)
            {
                double center = (vals.Min() + vals.Max()) / 2.0;
                nMin = Math.Max(min, center - range * 0.1);
                nMax = Math.Min(max, center + range * 0.1);
            }
            result[key] = (nMin, nMax, isInt);
        }
        return result;
    }

    /// <summary>Loads historically approved parameter sets as doubles for adaptive bounds.</summary>
    internal static async Task<List<Dictionary<string, double>>> LoadHistoricalApprovedParamsAsync(
        DbContext db, Strategy strategy, CancellationToken ct)
    {
        var approved = await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == strategy.Id
                     && r.Status == OptimizationRunStatus.Approved
                     && r.BestParametersJson != null && !r.IsDeleted)
            .OrderByDescending(r => r.ApprovedAt)
            .Take(20)
            .Select(r => r.BestParametersJson!)
            .ToListAsync(ct);

        var result = new List<Dictionary<string, double>>();
        foreach (var json in approved)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(CanonicalParameterJson.Normalize(json));
                if (dict is null) continue;
                var dblDict = new Dictionary<string, double>();
                foreach (var (k, v) in dict)
                    if (v.TryGetDouble(out double d)) dblDict[k] = d;
                if (dblDict.Count > 0) result.Add(dblDict);
            }
            catch { /* skip malformed */ }
        }
        return result;
    }

    /// <summary>Converts a grid param set (int/double values) to TPE doubles.</summary>
    internal static Dictionary<string, double> ParamSetToDoubles(Dictionary<string, object> paramSet)
        => paramSet.ToDictionary(kv => kv.Key, kv => kv.Value is int i ? (double)i : kv.Value is double d ? d : 0.0);

    /// <summary>Converts TPE doubles back to typed param set for JSON serialisation.</summary>
    internal static Dictionary<string, object> DoublesToParamSet(
        Dictionary<string, double> suggestion,
        Dictionary<string, (double Min, double Max, bool IsInteger)> bounds)
        => suggestion.ToDictionary(
            kv => kv.Key,
            kv => bounds.TryGetValue(kv.Key, out var b) && b.IsInteger
                ? (object)(int)Math.Round(kv.Value) : (object)kv.Value);

    /// <summary>
    /// Builds the parameter grid, reading ranges from EngineConfig with hardcoded fallbacks.
    /// All 7 strategy types have explicit grids. Unknown types log a warning and return empty.
    /// </summary>
    internal async Task<List<Dictionary<string, object>>> BuildParameterGridAsync(
        DbContext db, StrategyType strategyType, CancellationToken ct)
    {
        var grid = new List<Dictionary<string, object>>();

        switch (strategyType)
        {
            case StrategyType.MovingAverageCrossover:
            {
                var fastPeriods = await GetGridValuesAsync(db, "Optimization:Grid:MovingAverageCrossover:FastPeriods", [5, 9, 12, 20], ct);
                var slowPeriods = await GetGridValuesAsync(db, "Optimization:Grid:MovingAverageCrossover:SlowPeriods", [20, 21, 26, 50], ct);
                foreach (var fast in fastPeriods)
                foreach (var slow in slowPeriods)
                {
                    if (fast >= slow) continue;
                    grid.Add(new Dictionary<string, object>
                    {
                        ["FastPeriod"] = fast, ["SlowPeriod"] = slow, ["MaPeriod"] = 50
                    });
                }
                break;
            }

            case StrategyType.RSIReversion:
            {
                var periods    = await GetGridValuesAsync(db, "Optimization:Grid:RSIReversion:Periods", [7, 10, 14, 21], ct);
                var oversold   = await GetGridValuesAsync(db, "Optimization:Grid:RSIReversion:OversoldLevels", [25, 30, 35], ct);
                var overbought = await GetGridValuesAsync(db, "Optimization:Grid:RSIReversion:OverboughtLevels", [65, 70, 75], ct);
                foreach (var p in periods)
                foreach (var os in oversold)
                foreach (var ob in overbought)
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["Period"] = p, ["Oversold"] = os, ["Overbought"] = ob
                    });
                }
                break;
            }

            case StrategyType.BreakoutScalper:
            {
                var lookbacks   = await GetGridValuesAsync(db, "Optimization:Grid:BreakoutScalper:LookbackBars", [10, 15, 20, 30], ct);
                var multipliers = await GetGridDoubleValuesAsync(db, "Optimization:Grid:BreakoutScalper:Multipliers", [1.0, 1.5, 2.0], ct);
                foreach (var lb in lookbacks)
                foreach (var mult in multipliers)
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["LookbackBars"] = lb, ["BreakoutMultiplier"] = mult
                    });
                }
                break;
            }

            case StrategyType.BollingerBandReversion:
            {
                var periods     = await GetGridValuesAsync(db, "Optimization:Grid:BollingerBand:Periods", [14, 20, 30], ct);
                var stdDevMults = await GetGridDoubleValuesAsync(db, "Optimization:Grid:BollingerBand:StdDevMultipliers", [1.5, 2.0, 2.5, 3.0], ct);
                foreach (var p in periods)
                foreach (var sd in stdDevMults)
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["Period"] = p, ["StdDevMultiplier"] = sd
                    });
                }
                break;
            }

            case StrategyType.MomentumTrend:
            {
                var adxPeriods    = await GetGridValuesAsync(db, "Optimization:Grid:MomentumTrend:AdxPeriods", [10, 14, 20], ct);
                var adxThresholds = await GetGridValuesAsync(db, "Optimization:Grid:MomentumTrend:AdxThresholds", [18, 22, 25, 30], ct);
                foreach (var p in adxPeriods)
                foreach (var t in adxThresholds)
                {
                    grid.Add(new Dictionary<string, object>
                    {
                        ["AdxPeriod"] = p, ["AdxThreshold"] = t
                    });
                }
                break;
            }

            case StrategyType.MACDDivergence:
            {
                var fastPeriods   = await GetGridValuesAsync(db, "Optimization:Grid:MACD:FastPeriods", [8, 12, 16], ct);
                var slowPeriods   = await GetGridValuesAsync(db, "Optimization:Grid:MACD:SlowPeriods", [21, 26, 30], ct);
                var signalPeriods = await GetGridValuesAsync(db, "Optimization:Grid:MACD:SignalPeriods", [7, 9, 12], ct);
                foreach (var fast in fastPeriods)
                foreach (var slow in slowPeriods)
                foreach (var signal in signalPeriods)
                {
                    if (fast >= slow) continue;
                    grid.Add(new Dictionary<string, object>
                    {
                        ["FastPeriod"] = fast, ["SlowPeriod"] = slow,
                        ["SignalPeriod"] = signal, ["DivergenceLookback"] = 20
                    });
                }
                break;
            }

            case StrategyType.SessionBreakout:
            {
                foreach (var rangeStart in new[] { 0, 2, 4 })
                foreach (var rangeEnd in new[] { 6, 8 })
                foreach (var thresholdMult in new[] { 0.3, 0.5, 0.8 })
                {
                    if (rangeStart >= rangeEnd) continue;
                    grid.Add(new Dictionary<string, object>
                    {
                        ["RangeStartHourUtc"] = rangeStart, ["RangeEndHourUtc"] = rangeEnd,
                        ["BreakoutStartHour"] = rangeEnd, ["BreakoutEndHour"] = rangeEnd + 8,
                        ["ThresholdMultiplier"] = thresholdMult
                    });
                }
                break;
            }

            case StrategyType.NewsFade:
            {
                foreach (var minMin in new[] { 2, 5, 10 })
                foreach (var maxMin in new[] { 15, 30, 45 })
                foreach (var threshold in new[] { 0.7, 1.0, 1.3 })
                {
                    if (minMin >= maxMin) continue;
                    grid.Add(new Dictionary<string, object>
                    {
                        ["MinMinutesSinceEvent"] = minMin,
                        ["MaxMinutesSinceEvent"] = maxMin,
                        ["MomentumAtrThreshold"] = threshold,
                    });
                }
                break;
            }

            case StrategyType.CalendarEffect:
            {
                // ModeId is an integer-coded CalendarEffectMode (0=MonthEnd, 1=LondonNyOverlap)
                // so TPE bound-extraction (int/double only) can tune the mode choice alongside
                // numeric params. Evaluator accepts either "Mode" (string) or "ModeId" (int).
                foreach (var modeId in new[] { 0, 1 })
                foreach (var lookback in new[] { 3, 5, 8 })
                foreach (var threshold in new[] { 0.8, 1.0, 1.5 })
                foreach (var secondary in new[] { 2, 3, 4 })
                {
                    var entry = new Dictionary<string, object>
                    {
                        ["ModeId"] = modeId,
                        ["LookbackBars"] = lookback,
                        ["MomentumAtrThreshold"] = threshold,
                    };
                    if (modeId == 0)
                    {
                        entry["MonthEndBusinessDays"] = secondary;
                    }
                    else
                    {
                        // OverlapStartHourUtc 12 or 13, OverlapEndHourUtc 16 or 17 — map secondary
                        entry["OverlapStartHourUtc"] = secondary <= 2 ? 12 : 13;
                        entry["OverlapEndHourUtc"]   = secondary <= 3 ? 16 : 17;
                    }
                    grid.Add(entry);
                }
                break;
            }

            default:
                throw new InvalidOperationException(
                    $"No parameter grid defined for StrategyType={strategyType}. " +
                    "Add a grid definition to OptimizationGridBuilder.BuildParameterGridAsync before optimising this type.");
        }

        return grid;
    }

    /// <summary>
    /// When all grid candidates have been previously tried, expands by interpolating
    /// midpoints between existing parameter values across ALL dimensions.
    /// Returns only fresh combinations, capped at 50.
    /// </summary>
    internal static List<Dictionary<string, object>> ExpandGridWithMidpoints(
        List<Dictionary<string, object>> originalGrid,
        HashSet<string> previousParamSet)
    {
        if (originalGrid.Count == 0) return [];

        var valuesPerKey = new Dictionary<string, SortedSet<double>>();
        foreach (var paramSet in originalGrid)
        {
            foreach (var (key, value) in paramSet)
            {
                if (!valuesPerKey.ContainsKey(key))
                    valuesPerKey[key] = [];
                if (value is int i) valuesPerKey[key].Add(i);
                else if (value is double d) valuesPerKey[key].Add(d);
            }
        }

        var expandedValues = new Dictionary<string, List<object>>();
        foreach (var (key, values) in valuesPerKey)
        {
            var expanded = new List<double>(values);
            var sorted   = values.ToList();
            double lowerBound = sorted[0];
            double upperBound = sorted[^1];
            for (int idx = 0; idx < sorted.Count - 1; idx++)
            {
                double mid = (sorted[idx] + sorted[idx + 1]) / 2.0;
                // Clip to bounds in case of floating-point drift
                mid = Math.Clamp(mid, lowerBound, upperBound);
                if (!values.Contains(mid))
                    expanded.Add(mid);
            }

            // Detect integer params from the original grid values rather than naming conventions.
            // If all original values for this key are whole numbers, treat as integer.
            bool isIntParam = values.All(v => Math.Abs(v - Math.Round(v)) < 1e-9);
            expandedValues[key] = expanded
                .Select(v => isIntParam ? (object)(int)Math.Round(v) : v)
                .Distinct()
                .ToList();
        }

        var template = originalGrid[0];
        var keys     = template.Keys.Where(k => expandedValues.ContainsKey(k)).ToList();

        const int maxIntermediateSize = 500;
        var candidates = new List<Dictionary<string, object>> { new(template) };
        foreach (var key in keys)
        {
            var vals = expandedValues[key];
            var next = new List<Dictionary<string, object>>(Math.Min(candidates.Count * vals.Count, maxIntermediateSize));
            bool capped = false;
            foreach (var current in candidates)
            {
                if (capped) break;
                foreach (var v in vals)
                {
                    next.Add(new Dictionary<string, object>(current) { [key] = v });
                    if (next.Count >= maxIntermediateSize) { capped = true; break; }
                }
            }
            candidates = next;
            if (candidates.Count >= maxIntermediateSize) break;
        }

        return candidates
            .Where(c => !previousParamSet.Contains(CanonicalParameterJson.Serialize(c)))
            .Take(50)
            .ToList();
    }

    // ── Config helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Loads all config entries matching the given keys in a single DB query and returns
    /// a dictionary of key → raw string value. Keys not present in the DB are omitted.
    /// Used by <see cref="OptimizationWorker.LoadConfigurationAsync"/> to avoid N+1 queries.
    /// </summary>
    internal static async Task<Dictionary<string, string>> GetConfigBatchAsync(
        DbContext ctx, IEnumerable<string> keys, CancellationToken ct)
    {
        var keyList = keys.ToList();
        var entries = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => keyList.Contains(c.Key) && c.Value != null)
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        return entries.ToDictionary(e => e.Key, e => e.Value!);
    }

    /// <summary>Extracts a typed value from a pre-fetched config batch, falling back to <paramref name="defaultValue"/>.</summary>
    internal static T GetConfigValue<T>(Dictionary<string, string> batch, string key, T defaultValue)
    {
        if (!batch.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        try   { return (T)Convert.ChangeType(raw, typeof(T)); }
        catch { return defaultValue; }
    }

    internal static T? GetConfigValueNullable<T>(Dictionary<string, string> batch, string key)
        where T : struct
    {
        if (!batch.TryGetValue(key, out var raw) || raw is null) return null;
        try   { return (T)Convert.ChangeType(raw, typeof(T)); }
        catch { return null; }
    }

    internal static async Task<T> GetConfigAsync<T>(
        DbContext ctx, string key, T defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    internal static async Task<int[]> GetGridValuesAsync(
        DbContext ctx, string key, int[] defaults, CancellationToken ct)
    {
        var raw = await GetConfigAsync<string>(ctx, key, "", ct);
        if (string.IsNullOrWhiteSpace(raw)) return defaults;

        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();

        return parsed.Length > 0 ? parsed : defaults;
    }

    internal static async Task<double[]> GetGridDoubleValuesAsync(
        DbContext ctx, string key, double[] defaults, CancellationToken ct)
    {
        var raw = await GetConfigAsync<string>(ctx, key, "", ct);
        if (string.IsNullOrWhiteSpace(raw)) return defaults;

        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();

        return parsed.Length > 0 ? parsed : defaults;
    }
}
