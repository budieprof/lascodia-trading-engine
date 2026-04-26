using System.Globalization;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Per-context override hierarchy for <see cref="MLConformalCalibrationWorker"/>.
/// </summary>
/// <remarks>
/// Splits override-resolution infrastructure (key bucketing, tier walk, parsers,
/// override-token validator with FNV-1a-hashed dedup) and the per-iteration
/// <see cref="ApplyPerContextOverrides"/> entry point off the main worker file.
/// Same partial-class split pattern used by the calibration / edge / rotation workers.
/// </remarks>
public sealed partial class MLConformalCalibrationWorker
{
    private const long FnvOffsetBasis = 1469598103934665603L;
    private const long FnvPrime = 1099511628211L;

    /// <summary>
    /// Bucket override rows by (Symbol, Timeframe). For each context, retain rows whose
    /// key matches one of the four context-tier prefixes — <c>Symbol:Timeframe</c>,
    /// <c>Symbol:*</c>, <c>*:Timeframe</c>, <c>*:*</c> — plus the <c>Model:{id}</c>
    /// tier (matched against any of the candidate models). The downstream resolver
    /// walks these in narrowest-first order: Model:{id} → Symbol:Timeframe → Symbol:* →
    /// *:Timeframe → *:*.
    /// </summary>
    private static IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>
        BucketOverridesByContext(
            IReadOnlyList<ActiveModelCandidate> models,
            IReadOnlyList<KeyValuePair<string, string>> allOverrideRows)
    {
        var result = new Dictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>();
        if (models.Count == 0) return result;

        var seen = new HashSet<(string, Timeframe)>(models.Count);
        foreach (var model in models)
        {
            if (!seen.Add((model.Symbol, model.Timeframe))) continue;

            string tfStr = model.Timeframe.ToString();
            string p1 = $"MLConformalCalibration:Override:{model.Symbol}:{tfStr}:";
            string p2 = $"MLConformalCalibration:Override:{model.Symbol}:*:";
            string p3 = $"MLConformalCalibration:Override:*:{tfStr}:";
            const string p4 = "MLConformalCalibration:Override:*:*:";
            const string pModel = "MLConformalCalibration:Override:Model:";

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

            result[(model.Symbol, model.Timeframe)] = bucket;
        }

        return result;
    }

    /// <summary>
    /// Resolve a per-context override value by walking the 6-tier hierarchy in
    /// narrowest-first order — <c>Model:{id}</c> first when modelId is non-null —
    /// and returning the first valid match. Falls through to <paramref name="globalDefault"/>
    /// when no tier resolves.
    /// </summary>
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
        if (modelId is not null
            && TryResolveTier(overrides, $"MLConformalCalibration:Override:Model:{modelId.Value}:{settingName}", tryParse, validate, out var vModel))
        {
            return vModel;
        }

        string tfStr = timeframe.ToString();
        if (TryResolveTier(overrides, $"MLConformalCalibration:Override:{symbol}:{tfStr}:{settingName}", tryParse, validate, out var v1)) return v1;
        if (TryResolveTier(overrides, $"MLConformalCalibration:Override:{symbol}:*:{settingName}", tryParse, validate, out var v2)) return v2;
        if (TryResolveTier(overrides, $"MLConformalCalibration:Override:*:{tfStr}:{settingName}", tryParse, validate, out var v3)) return v3;
        if (TryResolveTier(overrides, "MLConformalCalibration:Override:*:*:" + settingName, tryParse, validate, out var v4)) return v4;

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

    private static (bool ok, bool value) TryParseBoolish(string raw)
    {
        if (bool.TryParse(raw, out var b)) return (true, b);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return (true, i != 0);
        return (false, false);
    }

    /// <summary>
    /// Validate every override row's final-segment knob name against the supported
    /// <see cref="ValidOverrideKnobs"/> list. Logs a warning the first cycle a typo
    /// appears, an info message when typos are corrected, and dedupes via an FNV-1a
    /// hash of the unmatched-key set so we don't re-warn every cycle.
    /// </summary>
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

    /// <summary>
    /// Apply the per-context override hierarchy on top of the cycle-wide settings and
    /// return the effective per-model settings instance. Each knob is resolved
    /// independently — only those overridden change; the rest flow through unchanged.
    /// </summary>
    private static MLConformalCalibrationWorkerSettings ApplyPerContextOverrides(
        MLConformalCalibrationWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        long modelId)
    {
        if (overrides.Count == 0)
            return settings;

        int minLogs = ResolveOverride(
            overrides,
            symbol,
            timeframe,
            "MinLogs",
            TryParseStrictInt,
            value => value >= 10 && value <= 100_000,
            settings.MinLogs,
            modelId);

        int maxLogs = ResolveOverride(
            overrides,
            symbol,
            timeframe,
            "MaxLogs",
            TryParseStrictInt,
            value => value >= 10 && value <= 100_000,
            settings.MaxLogs,
            modelId);

        // A scoped MaxLogs below MinLogs makes the model impossible to calibrate. Lift
        // the cap to the effective minimum instead of silently skipping.
        maxLogs = Math.Max(maxLogs, minLogs);

        return settings with
        {
            MinLogs = minLogs,
            MaxLogs = maxLogs,
            MaxLogAgeDays = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "MaxLogAgeDays",
                TryParseStrictInt,
                value => value >= 1 && value <= 3650,
                settings.MaxLogAgeDays,
                modelId),
            MaxCalibrationAgeDays = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "MaxCalibrationAgeDays",
                TryParseStrictInt,
                value => value >= 1 && value <= 3650,
                settings.MaxCalibrationAgeDays,
                modelId),
            TargetCoverage = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "TargetCoverage",
                TryParseFiniteDouble,
                value => value >= 0.50 && value <= 0.999999,
                settings.TargetCoverage,
                modelId),
            RequirePostActivationLogs = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "RequirePostActivationLogs",
                TryParseBoolish,
                _ => true,
                settings.RequirePostActivationLogs,
                modelId),
        };
    }
}
