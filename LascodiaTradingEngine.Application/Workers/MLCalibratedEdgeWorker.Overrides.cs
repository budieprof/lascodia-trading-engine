using System.Globalization;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Per-context override hierarchy for <see cref="MLCalibratedEdgeWorker"/>.
/// </summary>
/// <remarks>
/// Splits override-resolution infrastructure (key bucketing, tier walk, parsers,
/// override-token validator with FNV-1a-hashed dedup) and the per-iteration
/// <see cref="ApplyPerContextOverrides"/> entry point off the main worker file.
/// Same partial-class split pattern used by <c>MLCalibrationMonitorWorker</c>.
/// </remarks>
public sealed partial class MLCalibratedEdgeWorker
{
    private const long FnvOffsetBasis = 1469598103934665603L;
    private const long FnvPrime = 1099511628211L;

    private static IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>
        BucketOverridesByContext(
            IReadOnlyList<ActiveModelCandidate> models,
            IReadOnlyList<KeyValuePair<string, string>> allOverrideRows)
    {
        var contexts = new HashSet<(string, Timeframe)>(models.Count);
        foreach (var model in models) contexts.Add((model.Symbol, model.Timeframe));

        var result = new Dictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>(contexts.Count);
        if (contexts.Count == 0) return result;

        foreach (var (symbol, timeframe) in contexts)
        {
            string tfStr = timeframe.ToString();
            string p1 = $"MLEdge:Override:{symbol}:{tfStr}:";
            string p2 = $"MLEdge:Override:{symbol}:*:";
            string p3 = $"MLEdge:Override:*:{tfStr}:";
            const string p4 = "MLEdge:Override:*:*:";
            const string pModel = "MLEdge:Override:Model:";

            var bucket = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in allOverrideRows)
            {
                if (entry.Key.StartsWith(p1, StringComparison.Ordinal) ||
                    entry.Key.StartsWith(p2, StringComparison.Ordinal) ||
                    entry.Key.StartsWith(p3, StringComparison.Ordinal) ||
                    entry.Key.StartsWith(p4, StringComparison.Ordinal) ||
                    entry.Key.StartsWith(pModel, StringComparison.Ordinal))
                {
                    bucket[entry.Key] = entry.Value;
                }
            }

            result[(symbol, timeframe)] = bucket;
        }

        return result;
    }

    internal static T ResolveOverride<T>(
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        string settingName,
        Func<string, (bool ok, T value)> tryParse,
        Func<T, bool> validate,
        T globalDefault,
        long? modelId = null)
    {
        if (modelId is not null)
        {
            if (TryResolveTier(overrides, $"MLEdge:Override:Model:{modelId.Value}:{settingName}", tryParse, validate, out var vModel))
                return vModel;
        }

        string tfStr = timeframe.ToString();
        if (TryResolveTier(overrides, $"MLEdge:Override:{symbol}:{tfStr}:{settingName}", tryParse, validate, out var v1)) return v1;
        if (TryResolveTier(overrides, $"MLEdge:Override:{symbol}:*:{settingName}", tryParse, validate, out var v2)) return v2;
        if (TryResolveTier(overrides, $"MLEdge:Override:*:{tfStr}:{settingName}", tryParse, validate, out var v3)) return v3;
        if (TryResolveTier(overrides, "MLEdge:Override:*:*:" + settingName, tryParse, validate, out var v4)) return v4;

        return globalDefault;
    }

    private static bool TryResolveTier<T>(
        IReadOnlyDictionary<string, string> overrides,
        string key,
        Func<string, (bool ok, T value)> tryParse,
        Func<T, bool> validate,
        out T value)
    {
        if (overrides.TryGetValue(key, out var raw) && raw is not null)
        {
            var (ok, parsed) = tryParse(raw);
            if (ok && validate(parsed))
            {
                value = parsed;
                return true;
            }
        }

        value = default!;
        return false;
    }

    private static (bool ok, bool value) TryParseBoolish(string raw)
    {
        if (bool.TryParse(raw, out var b)) return (true, b);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return (true, i != 0);
        return (false, false);
    }

    private static (bool ok, int value) TryParseStrictInt(string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? (true, value)
            : (false, 0);

    private static (bool ok, double value) TryParseFiniteDouble(string raw)
        => double.TryParse(
                raw,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var value)
            && double.IsFinite(value)
            ? (true, value)
            : (false, 0d);

    private void ValidateOverrideTokens(IReadOnlyList<KeyValuePair<string, string>> allOverrideRows)
    {
        if (allOverrideRows.Count == 0)
        {
            HandleValidatorState(unmatched: null);
            return;
        }

        SortedSet<string>? unmatched = null;
        foreach (var entry in allOverrideRows)
        {
            string key = entry.Key;
            int lastColon = key.LastIndexOf(':');
            if (lastColon <= 0 || lastColon >= key.Length - 1) continue;

            ReadOnlySpan<char> knob = key.AsSpan(lastColon + 1);
            bool valid = false;
            foreach (var supported in ValidOverrideKnobs)
            {
                if (knob.Equals(supported.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    valid = true;
                    break;
                }
            }

            if (!valid)
                (unmatched ??= new SortedSet<string>(StringComparer.Ordinal)).Add("[knob] " + key);
        }

        HandleValidatorState(unmatched);
    }

    private void HandleValidatorState(SortedSet<string>? unmatched)
    {
        long signature = unmatched is null || unmatched.Count == 0 ? 0 : ComputeUnmatchedSignature(unmatched);
        long previous = _lastUnmatchedTokensSignature;
        if (signature == previous) return;

        _lastUnmatchedTokensSignature = signature;

        if (signature != 0 && unmatched is { Count: > 0 })
        {
            string rendered = string.Join(", ", unmatched);
            _logger.LogWarning(
                "{Worker}: {Count} override key token(s) do not match any known knob. These rows will fall through to the next override tier or have no effect. Typos: {Typos}",
                WorkerName,
                unmatched.Count,
                rendered);
        }
        else if (previous != 0)
        {
            _logger.LogInformation(
                "{Worker}: all override key tokens now resolve cleanly; previously reported typos appear to have been corrected.",
                WorkerName);
        }
    }

    private static long ComputeUnmatchedSignature(SortedSet<string> unmatched)
    {
        long hash = FnvOffsetBasis;
        foreach (var key in unmatched)
        {
            hash = FnvHashChars(key, hash);
            hash = FnvHashChars("|", hash);
        }

        return hash == 0 ? 1 : hash;
    }

    private static long FnvHashChars(string value, long state)
    {
        unchecked
        {
            foreach (char c in value) state = (state ^ c) * FnvPrime;
            return state;
        }
    }

    private static MLCalibratedEdgeWorkerSettings ApplyPerContextOverrides(
        MLCalibratedEdgeWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        long modelId)
    {
        if (overrides.Count == 0)
            return settings;

        int minSamples = ResolveOverride(
            overrides,
            symbol,
            timeframe,
            "MinSamples",
            TryParseStrictInt,
            value => value >= MinMinSamples && value <= MaxMinSamples,
            settings.MinSamples,
            modelId);

        int maxResolvedPerModel = ResolveOverride(
            overrides,
            symbol,
            timeframe,
            "MaxResolvedPerModel",
            TryParseStrictInt,
            value => value >= MinMaxResolvedPerModel && value <= MaxMaxResolvedPerModel,
            settings.MaxResolvedPerModel,
            modelId);

        // A scoped MaxResolvedPerModel below MinSamples makes the model impossible to
        // evaluate. Lift the cap to the effective minimum instead of silently skipping it.
        maxResolvedPerModel = Math.Max(maxResolvedPerModel, minSamples);

        return settings with
        {
            MinSamples = minSamples,
            WarnExpectedValuePips = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "WarnEvPips",
                TryParseFiniteDouble,
                value => value >= MinWarnEvPips && value <= MaxWarnEvPips,
                settings.WarnExpectedValuePips,
                modelId),
            MaxResolvedPerModel = maxResolvedPerModel,
            TrainingDataWindowDays = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "TrainingDataWindowDays",
                TryParseStrictInt,
                value => value >= MinTrainingDataWindowDays && value <= MaxTrainingDataWindowDays,
                settings.TrainingDataWindowDays,
                modelId),
            MinTimeBetweenRetrainsHours = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "MinTimeBetweenRetrainsHours",
                TryParseStrictInt,
                value => value >= MinMinTimeBetweenRetrainsHours && value <= MaxMinTimeBetweenRetrainsHours,
                settings.MinTimeBetweenRetrainsHours,
                modelId),
            MaxRetrainsPerCycle = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "MaxRetrainsPerCycle",
                TryParseStrictInt,
                value => value >= MinMaxRetrainsPerCycle && value <= MaxMaxRetrainsPerCycle,
                settings.MaxRetrainsPerCycle,
                modelId),
            ConsecutiveSkipAlertThreshold = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "ConsecutiveSkipAlertThreshold",
                TryParseStrictInt,
                value => value >= MinConsecutiveSkipAlertThreshold && value <= MaxConsecutiveSkipAlertThreshold,
                settings.ConsecutiveSkipAlertThreshold,
                modelId),
            MaxAlertsPerCycle = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "MaxAlertsPerCycle",
                TryParseStrictInt,
                value => value >= MinMaxAlertsPerCycle && value <= MaxMaxAlertsPerCycle,
                settings.MaxAlertsPerCycle,
                modelId),
            RegressionGuardK = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "RegressionGuardK",
                TryParseFiniteDouble,
                value => value >= MinRegressionGuardK && value <= MaxRegressionGuardK,
                settings.RegressionGuardK,
                modelId),
            BootstrapResamples = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "BootstrapResamples",
                TryParseStrictInt,
                value => value >= MinBootstrapResamples && value <= MaxBootstrapResamples,
                settings.BootstrapResamples,
                modelId),
            ChronicCriticalThreshold = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "ChronicCriticalThreshold",
                TryParseStrictInt,
                value => value >= MinChronicCriticalThreshold && value <= MaxChronicCriticalThreshold,
                settings.ChronicCriticalThreshold,
                modelId),
            SuppressRetrainOnChronic = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "SuppressRetrainOnChronic",
                TryParseBoolish,
                _ => true,
                settings.SuppressRetrainOnChronic,
                modelId),
        };
    }
}
