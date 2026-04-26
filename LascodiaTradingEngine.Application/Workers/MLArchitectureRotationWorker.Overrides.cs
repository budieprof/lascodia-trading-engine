using System.Globalization;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Per-context override hierarchy for <see cref="MLArchitectureRotationWorker"/>.
/// </summary>
/// <remarks>
/// Splits override-resolution infrastructure (key bucketing, tier walk, parsers,
/// override-token validator with FNV-1a-hashed dedup) and the per-iteration
/// <see cref="ApplyPerContextOverrides"/> entry point off the main worker file.
/// Same partial-class split pattern used by <c>MLCalibrationMonitorWorker</c> and
/// <c>MLCalibratedEdgeWorker</c>.
/// </remarks>
public sealed partial class MLArchitectureRotationWorker
{
    private const long FnvOffsetBasis = 1469598103934665603L;
    private const long FnvPrime = 1099511628211L;

    /// <summary>
    /// Bucket override rows by (Symbol, Timeframe). For each context, retain rows whose
    /// key matches one of the four tier prefixes — <c>Symbol:Timeframe</c>,
    /// <c>Symbol:*</c>, <c>*:Timeframe</c>, or <c>*:*</c>. The downstream resolver walks
    /// these in narrowest-first order.
    /// </summary>
    private static IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>
        BucketOverridesByContext(
            IReadOnlyList<ActiveContext> contexts,
            IReadOnlyList<KeyValuePair<string, string>> allOverrideRows)
    {
        var result = new Dictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>(contexts.Count);
        if (contexts.Count == 0) return result;

        var seen = new HashSet<(string, Timeframe)>(contexts.Count);
        foreach (var context in contexts)
        {
            if (!seen.Add((context.Symbol, context.Timeframe))) continue;

            string tfStr = context.Timeframe.ToString();
            string p1 = $"MLArchitectureRotation:Override:{context.Symbol}:{tfStr}:";
            string p2 = $"MLArchitectureRotation:Override:{context.Symbol}:*:";
            string p3 = $"MLArchitectureRotation:Override:*:{tfStr}:";
            const string p4 = "MLArchitectureRotation:Override:*:*:";

            var bucket = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in allOverrideRows)
            {
                if (entry.Key.StartsWith(p1, StringComparison.Ordinal) ||
                    entry.Key.StartsWith(p2, StringComparison.Ordinal) ||
                    entry.Key.StartsWith(p3, StringComparison.Ordinal) ||
                    entry.Key.StartsWith(p4, StringComparison.Ordinal))
                {
                    bucket[entry.Key] = entry.Value;
                }
            }

            result[(context.Symbol, context.Timeframe)] = bucket;
        }

        return result;
    }

    /// <summary>
    /// Resolve a per-context override value by walking the 4-tier hierarchy in
    /// narrowest-first order and returning the first valid match. Falls through to
    /// <paramref name="globalDefault"/> when no tier resolves.
    /// </summary>
    internal static T ResolveOverride<T>(
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        string settingName,
        Func<string, (bool ok, T value)> tryParse,
        Func<T, bool> validate,
        T globalDefault)
    {
        string tfStr = timeframe.ToString();
        if (TryResolveTier(overrides, $"MLArchitectureRotation:Override:{symbol}:{tfStr}:{settingName}", tryParse, validate, out var v1)) return v1;
        if (TryResolveTier(overrides, $"MLArchitectureRotation:Override:{symbol}:*:{settingName}", tryParse, validate, out var v2)) return v2;
        if (TryResolveTier(overrides, $"MLArchitectureRotation:Override:*:{tfStr}:{settingName}", tryParse, validate, out var v3)) return v3;
        if (TryResolveTier(overrides, "MLArchitectureRotation:Override:*:*:" + settingName, tryParse, validate, out var v4)) return v4;

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

    /// <summary>
    /// Validate every override row's final-segment knob name against the supported
    /// <see cref="ValidOverrideKnobs"/> list. Logs a warning on the first cycle a typo
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
    /// return the effective per-context settings instance. Each knob is resolved
    /// independently — only those overridden change; the rest flow through unchanged.
    /// </summary>
    private static MLArchitectureRotationWorkerSettings ApplyPerContextOverrides(
        MLArchitectureRotationWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe)
    {
        if (overrides.Count == 0)
            return settings;

        return settings with
        {
            MinRunsPerWindow = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "MinRunsPerWindow",
                TryParseStrictInt,
                value => value >= MinMinRunsPerWindow && value <= MaxMinRunsPerWindow,
                settings.MinRunsPerWindow),
            WindowDays = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "WindowDays",
                TryParseStrictInt,
                value => value >= MinWindowDays && value <= MaxWindowDays,
                settings.WindowDays),
            CooldownMinutes = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "CooldownMinutes",
                TryParseStrictInt,
                value => value >= MinCooldownMinutes && value <= MaxCooldownMinutes,
                settings.CooldownMinutes),
            MaxFailuresPerWindow = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "MaxFailuresPerWindow",
                TryParseStrictInt,
                value => value >= MinMaxFailuresPerWindow && value <= MaxMaxFailuresPerWindow,
                settings.MaxFailuresPerWindow),
            ActiveRunFreshnessHours = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "ActiveRunFreshnessHours",
                TryParseStrictInt,
                value => value >= MinActiveRunFreshnessHours && value <= MaxActiveRunFreshnessHours,
                settings.ActiveRunFreshnessHours),
            InfraFailureLookbackHours = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "InfraFailureLookbackHours",
                TryParseStrictInt,
                value => value >= MinInfraFailureLookbackHours && value <= MaxInfraFailureLookbackHours,
                settings.InfraFailureLookbackHours),
            TrainingDataWindowDays = ResolveOverride(
                overrides,
                symbol,
                timeframe,
                "TrainingDataWindowDays",
                TryParseStrictInt,
                value => value >= MinTrainingWindowDays && value <= MaxTrainingWindowDays,
                settings.TrainingDataWindowDays),
        };
    }
}
