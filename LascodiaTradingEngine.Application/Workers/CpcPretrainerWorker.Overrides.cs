using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Per-context override hierarchy for <see cref="CpcPretrainerWorker"/>. Operators write
/// <c>EngineConfig</c> rows under <c>MLCpc:Override:*</c> to pin per-pair (and optionally
/// per-regime) values for five training knobs. The hierarchy is resolved with first-hit-wins
/// semantics; the global <see cref="MLCpcRuntimeConfig"/> values are the final fallback.
/// </summary>
public sealed partial class CpcPretrainerWorker
{
    private const string OverridePrefix = "MLCpc:Override:";

    private const string KnobMinCandles            = "MinCandles";
    private const string KnobMaxAcceptableLoss     = "MaxAcceptableLoss";
    private const string KnobMinImprovement        = "MinImprovement";
    private const string KnobMaxValidationLoss     = "MaxValidationLoss";
    private const string KnobMinValidationSequences = "MinValidationSequences";

    private static readonly string[] OverrideKnobs =
    [
        KnobMinCandles,
        KnobMaxAcceptableLoss,
        KnobMinImprovement,
        KnobMaxValidationLoss,
        KnobMinValidationSequences,
    ];

    /// <summary>
    /// Subset of <see cref="MLCpcRuntimeConfig"/> exposed as a per-(symbol, timeframe, regime)
    /// resolved view. The remaining knobs in the cycle config are intentionally global —
    /// per-pair overrides for things like <c>SequenceLength</c> would invalidate cross-pair
    /// embedding compatibility.
    /// </summary>
    internal readonly record struct EffectiveTrainingSettings(
        int    MinCandles,
        double MaxAcceptableLoss,
        double MinImprovement,
        double MaxValidationLoss,
        int    MinValidationSequences);

    internal sealed class ContextOverrideMap
    {
        // Key → value, keyed by the *fully qualified* EngineConfig key. Empty when the
        // feature is disabled, so the resolver becomes a pass-through that returns global
        // defaults.
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Loads every <c>MLCpc:Override:*</c> row in one indexed query and emits warnings for
    /// keys whose knob suffix is not in <see cref="OverrideKnobs"/> — the most common cause
    /// of "operator set an override but training behavior didn't change" tickets.
    /// </summary>
    private async Task<ContextOverrideMap> LoadOverridesAsync(DbContext readCtx, CancellationToken ct)
    {
        var map = new ContextOverrideMap();
        var rows = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted && c.Key.StartsWith(OverridePrefix))
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (!OverrideKnobs.Any(k => row.Key.EndsWith(":" + k, StringComparison.Ordinal)))
            {
                LogOverrideKeyUnrecognised(row.Key);
                continue;
            }
            if (row.Value is { } v)
                map.Values[row.Key] = v;
        }
        return map;
    }

    private static EffectiveTrainingSettings ResolveEffectiveSettings(
        ContextOverrideMap overrides,
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime,
        MLCpcRuntimeConfig config)
    {
        return new EffectiveTrainingSettings(
            MinCandles: ResolveIntOverride(overrides, symbol, timeframe, regime, KnobMinCandles, config.MinCandles),
            MaxAcceptableLoss: ResolveDoubleOverride(overrides, symbol, timeframe, regime, KnobMaxAcceptableLoss, config.MaxAcceptableLoss),
            MinImprovement: ResolveDoubleOverride(overrides, symbol, timeframe, regime, KnobMinImprovement, config.MinImprovement),
            MaxValidationLoss: ResolveDoubleOverride(overrides, symbol, timeframe, regime, KnobMaxValidationLoss, config.MaxValidationLoss),
            MinValidationSequences: ResolveIntOverride(overrides, symbol, timeframe, regime, KnobMinValidationSequences, config.MinValidationSequences));
    }

    private static int ResolveIntOverride(
        ContextOverrideMap overrides,
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime,
        string knob,
        int defaultValue)
    {
        foreach (var key in EnumerateOverrideKeys(symbol, timeframe, regime, knob))
        {
            if (overrides.Values.TryGetValue(key, out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return defaultValue;
    }

    private static double ResolveDoubleOverride(
        ContextOverrideMap overrides,
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime,
        string knob,
        double defaultValue)
    {
        foreach (var key in EnumerateOverrideKeys(symbol, timeframe, regime, knob))
        {
            if (overrides.Values.TryGetValue(key, out var raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed))
                return parsed;
        }
        return defaultValue;
    }

    /// <summary>
    /// First-hit-wins ordering: most specific to least specific. The regime tier is only
    /// consulted when a regime is actually present on the candidate; otherwise the search
    /// jumps straight to symbol+timeframe.
    /// </summary>
    private static IEnumerable<string> EnumerateOverrideKeys(
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime,
        string knob)
    {
        if (regime is { } r)
            yield return $"{OverridePrefix}Symbol:{symbol}:Timeframe:{timeframe}:Regime:{r}:{knob}";
        yield return $"{OverridePrefix}Symbol:{symbol}:Timeframe:{timeframe}:{knob}";
        yield return $"{OverridePrefix}Symbol:{symbol}:{knob}";
        yield return $"{OverridePrefix}Timeframe:{timeframe}:{knob}";
    }
}
