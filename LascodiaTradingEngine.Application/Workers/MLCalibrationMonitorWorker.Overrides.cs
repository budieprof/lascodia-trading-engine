using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

// Partial: override loading, resolution, application, validation. See file-layout
// note in MLCalibrationMonitorWorker.cs for the full per-file split rationale.
public sealed partial class MLCalibrationMonitorWorker
{
    /// <summary>
    /// Loads every per-context override row that could apply to <paramref name="symbol"/>/
    /// <paramref name="timeframe"/> in a single round-trip. The four base wildcard tiers
    /// (<c>{symbol}:{tf}</c>, <c>{symbol}:*</c>, <c>*:{tf}</c>, <c>*:*</c>) are OR'd into
    /// one prefix scan; this also captures any regime-scoped variants that share those
    /// base prefixes (e.g. <c>{symbol}:{tf}:Regime:HighVolatility:{knob}</c>). Caller
    /// resolves precedence in memory via <see cref="ResolveOverride{T}"/>.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, string>> LoadAllPerContextOverridesAsync(
        DbContext db, string symbol, Timeframe timeframe, CancellationToken ct,
        long? modelId = null)
    {
        string tfStr = timeframe.ToString();
        string p1 = $"MLCalibration:Override:{symbol}:{tfStr}:";
        string p2 = $"MLCalibration:Override:{symbol}:*:";
        string p3 = $"MLCalibration:Override:*:{tfStr}:";
        string p4 = "MLCalibration:Override:*:*:";
        string? pModel = modelId.HasValue
            ? $"MLCalibration:Override:Model:{modelId.Value}:"
            : null;

        var query = db.Set<EngineConfig>()
            .AsNoTracking();

        return await (pModel is null
            ? query.Where(c => c.Key.StartsWith(p1)
                         || c.Key.StartsWith(p2)
                         || c.Key.StartsWith(p3)
                         || c.Key.StartsWith(p4))
            : query.Where(c => c.Key.StartsWith(p1)
                         || c.Key.StartsWith(p2)
                         || c.Key.StartsWith(p3)
                         || c.Key.StartsWith(p4)
                         || c.Key.StartsWith(pModel)))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
    }

    /// <summary>
    /// Buckets pre-loaded override rows per unique <c>(Symbol, Timeframe)</c> in the
    /// candidate set. Pure in-memory work; the caller has already done the single
    /// broad-prefix DB scan. The trade-off is that the broad scan loads every override
    /// row even when only some contexts apply — fine in practice because operators
    /// typically write a handful of override rows total.
    /// </summary>
    private static IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>
        BucketOverridesByContext(
            IReadOnlyList<ActiveModelCandidate> models,
            IReadOnlyList<KeyValuePair<string, string>> allOverrideRows)
    {
        var contexts = new HashSet<(string, Timeframe)>(models.Count);
        foreach (var model in models)
        {
            contexts.Add((model.Symbol, model.Timeframe));
        }

        var result = new Dictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>(contexts.Count);
        if (contexts.Count == 0) return result;

        foreach (var (symbol, timeframe) in contexts)
        {
            string tfStr = timeframe.ToString();
            string p1 = $"MLCalibration:Override:{symbol}:{tfStr}:";
            string p2 = $"MLCalibration:Override:{symbol}:*:";
            string p3 = $"MLCalibration:Override:*:{tfStr}:";
            const string p4 = "MLCalibration:Override:*:*:";
            // Model-tier rows are keyed by ModelId, not Symbol/Timeframe. Include them
            // in every bucket; ResolveOverride filters to the iteration's own model id
            // by constructing the exact `Model:{id}:{knob}` key.
            const string pModel = "MLCalibration:Override:Model:";

            var bucket = new Dictionary<string, string>();
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

    /// <summary>
    /// Per-cycle audit of override-key tokens. Scans every override key for two classes
    /// of typo:
    /// <list type="bullet">
    ///   <item><description>Regime tokens (after a <c>:Regime:</c> segment) that don't parse to a <see cref="MarketRegimeEnum"/> value.</description></item>
    ///   <item><description>Knob tokens (the final segment) that aren't in <see cref="ValidOverrideKnobs"/>.</description></item>
    /// </list>
    /// A persistent unmatched set is logged once on first observation; transitions
    /// (additions, removals, replacements) re-log; recovery to the empty set logs a
    /// single Information line. Dedup uses a 64-bit FNV-1a hash of the sorted unmatched
    /// set so steady-state cycles allocate nothing for the dedup check itself.
    /// </summary>
    private void ValidateOverrideTokens(IReadOnlyList<KeyValuePair<string, string>> allOverrideRows)
    {
        if (allOverrideRows.Count == 0)
        {
            HandleValidatorState(unmatched: null);
            return;
        }

        const string regimeMarker = ":Regime:";
        // Lazy-allocate: the steady-state happy path (no typos) never allocates the set.
        SortedSet<string>? unmatched = null;

        foreach (var entry in allOverrideRows)
        {
            string key = entry.Key;

            // 1. Knob check: final segment after the last ':' must be a known knob.
            int lastColon = key.LastIndexOf(':');
            if (lastColon > 0 && lastColon < key.Length - 1)
            {
                ReadOnlySpan<char> knob = key.AsSpan(lastColon + 1);
                if (!IsValidKnobSpan(knob))
                {
                    (unmatched ??= new SortedSet<string>(StringComparer.Ordinal)).Add("[knob] " + key);
                }
            }

            // 2. Regime check: if the key has a `:Regime:` segment, the next token
            //    (until the following ':') must parse to a MarketRegime enum value.
            int idx = key.IndexOf(regimeMarker, StringComparison.Ordinal);
            if (idx < 0) continue;
            int tokenStart = idx + regimeMarker.Length;
            int tokenEnd = key.IndexOf(':', tokenStart);
            if (tokenEnd < 0) continue; // malformed key (no segment after the regime token)

            ReadOnlySpan<char> token = key.AsSpan(tokenStart, tokenEnd - tokenStart);
            if (!Enum.TryParse<MarketRegimeEnum>(token, ignoreCase: false, out _))
                (unmatched ??= new SortedSet<string>(StringComparer.Ordinal)).Add("[regime] " + key);
        }

        HandleValidatorState(unmatched);
    }

    private static bool IsValidKnobSpan(ReadOnlySpan<char> span)
    {
        foreach (var knob in ValidOverrideKnobs)
        {
            if (span.SequenceEqual(knob)) return true;
        }
        return false;
    }

    private void HandleValidatorState(SortedSet<string>? unmatched)
    {
        var (signature, rendered) = ComputeValidatorOutcome(unmatched);

        long previous = _lastUnmatchedTokensSignature;
        if (signature == previous) return;

        _lastUnmatchedTokensSignature = signature;

        if (rendered is not null)
        {
            // Single rendered parameter: the unmatched set is already prefix-annotated
            // ("[knob] {key}" / "[regime] {key}") so a plain join produces the human-
            // readable view. Structured-sink users can split on ", " or look at the
            // rendered string directly; the dual-emit pattern was over-engineered.
            _logger.LogWarning(
                "{Worker}: {Count} override key token(s) don't match any known regime name or knob. These rows will silently fall through to the next override tier (or have no effect at all). Typos: {Typos}",
                WorkerName, unmatched!.Count, rendered);
        }
        else if (previous != 0)
        {
            _logger.LogInformation(
                "{Worker}: all override key tokens now resolve cleanly; previously reported typos appear to have been corrected.",
                WorkerName);
        }
    }

    /// <summary>
    /// Pure computation: turns an unmatched-tokens set into a stable hash signature plus
    /// a comma-joined rendered string (or null when empty). Static so the instance method
    /// only handles state-update + log-emission concerns.
    /// </summary>
    private static (long Signature, string? Rendered) ComputeValidatorOutcome(SortedSet<string>? unmatched)
    {
        if (unmatched is null || unmatched.Count == 0)
            return (0, null);

        long signature = ComputeUnmatchedSignature(unmatched);
        string rendered = string.Join(", ", unmatched);
        return (signature, rendered);
    }

    private static long ComputeUnmatchedSignature(SortedSet<string> unmatched)
    {
        // FNV-1a 64-bit over the sorted set of annotated keys with a separator between
        // entries so {"ab","c"} and {"a","bc"} hash distinctly. Result is purely an
        // in-process equality fingerprint; not a stable ID. Shares the FNV primitives
        // with ParseAuditFlushMode so both validators dedup identically.
        long hash = FnvOffsetBasis;
        foreach (var key in unmatched)
        {
            hash = FnvHashChars(key, hash);
            hash = FnvHashChars("|", hash);
        }
        return FnvHashFold(hash);
    }

    /// <summary>
    /// Walks the override-precedence chain (most-specific → least-specific) for a single
    /// setting and returns the first row that parses cleanly and clears <paramref name="validate"/>.
    /// Tier order:
    /// <list type="number">
    ///   <item><description><c>Model:{id}:{knob}</c> (when <paramref name="modelId"/> is non-null) — production-pin a single problematic model regardless of its context.</description></item>
    ///   <item><description>Four regime-scoped tiers (<paramref name="regime"/> non-null): <c>{Symbol}:{TF}:Regime:{R}</c> → <c>{Symbol}:*:Regime:{R}</c> → <c>*:{TF}:Regime:{R}</c> → <c>*:*:Regime:{R}</c>.</description></item>
    ///   <item><description>Four regime-agnostic tiers: <c>{Symbol}:{TF}</c> → <c>{Symbol}:*</c> → <c>*:{TF}</c> → <c>*:*</c>.</description></item>
    /// </list>
    /// Parsing or validation failures fall through to the next tier, not silent acceptance.
    /// </summary>
    internal static T ResolveOverride<T>(
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        MarketRegimeEnum? regime,
        string settingName,
        Func<string, (bool ok, T value)> tryParse,
        Func<T, bool> validate,
        T globalDefault,
        long? modelId = null)
    {
        // Most specific tier: per-model pin. Wins over every (symbol, timeframe)-derived
        // tier so operators can tighten thresholds on a known-difficult model without
        // affecting peers in the same context.
        if (modelId is not null)
        {
            if (TryResolveTier(overrides, $"MLCalibration:Override:Model:{modelId.Value}:{settingName}", tryParse, validate, out var vModel))
                return vModel;
        }

        string tfStr = timeframe.ToString();
        // Regime-scoped tiers, most specific to least specific. Each tier is built and
        // probed lazily — no upfront array allocation, unused tiers never construct the string.
        if (regime is not null)
        {
            string r = regime.Value.ToString();
            if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:{tfStr}:Regime:{r}:{settingName}", tryParse, validate, out var v1)) return v1;
            if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:*:Regime:{r}:{settingName}", tryParse, validate, out var v2)) return v2;
            if (TryResolveTier(overrides, $"MLCalibration:Override:*:{tfStr}:Regime:{r}:{settingName}", tryParse, validate, out var v3)) return v3;
            if (TryResolveTier(overrides, $"MLCalibration:Override:*:*:Regime:{r}:{settingName}", tryParse, validate, out var v4)) return v4;
        }

        if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:{tfStr}:{settingName}", tryParse, validate, out var v5)) return v5;
        if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:*:{settingName}", tryParse, validate, out var v6)) return v6;
        if (TryResolveTier(overrides, $"MLCalibration:Override:*:{tfStr}:{settingName}", tryParse, validate, out var v7)) return v7;
        if (TryResolveTier(overrides, "MLCalibration:Override:*:*:" + settingName, tryParse, validate, out var v8)) return v8;

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
        // For struct T this is the zero value; for reference T it's null. Callers must
        // honour the bool return rather than dereferencing `value` on a false outcome.
        value = default!;
        return false;
    }

    /// <summary>
    /// Clones <paramref name="settings"/> with every per-context overrideable knob resolved
    /// against <paramref name="overrides"/>. Pass a non-null <paramref name="regime"/> from
    /// per-regime evaluation paths so regime-scoped tiers take precedence.
    /// </summary>
    private static MLCalibrationMonitorWorkerSettings ApplyPerContextOverrides(
        MLCalibrationMonitorWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        MarketRegimeEnum? regime = null,
        long? modelId = null)
    {
        return settings with
        {
            MaxEce = ResolveOverride(overrides, symbol, timeframe, regime, "MaxEce",
                TryParseFiniteDouble,
                v => v >= MinMaxEce && v <= MaxMaxEce,
                settings.MaxEce, modelId),
            DegradationDelta = ResolveOverride(overrides, symbol, timeframe, regime, "DegradationDelta",
                TryParseFiniteDouble,
                v => v >= MinDegradationDelta && v <= MaxDegradationDelta,
                settings.DegradationDelta, modelId),
            RegressionGuardK = ResolveOverride(overrides, symbol, timeframe, regime, "RegressionGuardK",
                TryParseFiniteDouble,
                v => v >= MinRegressionGuardK && v <= MaxRegressionGuardK,
                settings.RegressionGuardK, modelId),
            BootstrapCacheStaleHours = ResolveOverride(overrides, symbol, timeframe, regime, "BootstrapCacheStaleHours",
                TryParseStrictInt,
                v => v >= 0 && v <= MaxBootstrapCacheStaleHours,
                settings.BootstrapCacheStaleHours, modelId),
            RetrainOnBaselineCritical = ResolveOverride(overrides, symbol, timeframe, regime, "RetrainOnBaselineCritical",
                TryParseBoolish,
                _ => true,
                settings.RetrainOnBaselineCritical, modelId),
        };
    }

    // Shared parsers for the override resolvers — strict (no decimal-to-int truncation),
    // invariant culture, finite-double-only.
    private static (bool ok, int value) TryParseStrictInt(string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? (true, v) : (false, 0);

    private static (bool ok, double value) TryParseFiniteDouble(string raw)
        => double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? (true, v) : (false, 0d);

    private static (bool ok, bool value) TryParseBoolish(string raw)
    {
        if (bool.TryParse(raw, out var b)) return (true, b);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return (true, i != 0);
        return (false, false);
    }

    /// <summary>Resolves the effective <c>BootstrapCacheStaleHours</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<int> ResolveBootstrapCacheStaleHoursAsync(
        DbContext db, string symbol, Timeframe timeframe, int globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null, long? modelId = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct, modelId);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "BootstrapCacheStaleHours",
            TryParseStrictInt,
            v => v >= 0 && v <= MaxBootstrapCacheStaleHours,
            globalDefault, modelId);
    }

    /// <summary>Resolves the effective <c>RetrainOnBaselineCritical</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<bool> ResolveRetrainOnBaselineCriticalAsync(
        DbContext db, string symbol, Timeframe timeframe, bool globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null, long? modelId = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct, modelId);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "RetrainOnBaselineCritical",
            TryParseBoolish,
            _ => true,
            globalDefault, modelId);
    }

    /// <summary>Resolves the effective <c>MaxEce</c> ceiling for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<double> ResolveMaxEceAsync(
        DbContext db, string symbol, Timeframe timeframe, double globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null, long? modelId = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct, modelId);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "MaxEce",
            TryParseFiniteDouble,
            v => v >= MinMaxEce && v <= MaxMaxEce,
            globalDefault, modelId);
    }

    /// <summary>Resolves the effective <c>DegradationDelta</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<double> ResolveDegradationDeltaAsync(
        DbContext db, string symbol, Timeframe timeframe, double globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null, long? modelId = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct, modelId);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "DegradationDelta",
            TryParseFiniteDouble,
            v => v >= MinDegradationDelta && v <= MaxDegradationDelta,
            globalDefault, modelId);
    }

    /// <summary>Resolves the effective <c>RegressionGuardK</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<double> ResolveRegressionGuardKAsync(
        DbContext db, string symbol, Timeframe timeframe, double globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null, long? modelId = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct, modelId);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "RegressionGuardK",
            TryParseFiniteDouble,
            v => v >= MinRegressionGuardK && v <= MaxRegressionGuardK,
            globalDefault, modelId);
    }
}
