using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

// Partial: DB-side bootstrap-stderr cache (load + append). The pure bootstrap
// computation lives in MLCalibrationSignalEvaluator. See file-layout note in
// MLCalibrationMonitorWorker.cs.
public sealed partial class MLCalibrationMonitorWorker
{
    /// <summary>
    /// Returns the cached bootstrap stderr for this (model, regime) scope when both the
    /// wall-clock staleness window AND the model's <c>RowVersion</c> match. Returns
    /// <c>null</c> on any mismatch (cache missing, time-stale, or model bytes replaced via
    /// retrain promotion) so the caller recomputes. Per-regime cache lives under
    /// <c>:Regime:{name}:</c> keys keyed identically to the global path.
    /// </summary>
    private static async Task<double?> LoadFreshBootstrapStderrAsync(
        DbContext db,
        long modelId,
        MarketRegimeEnum? regime,
        uint currentRowVersion,
        DateTime nowUtc,
        int staleHours,
        CancellationToken ct)
    {
        if (staleHours <= 0) return null;

        // Use the integer enum value, not the string name — renaming a regime enum member
        // (e.g. Trending → TrendingUp) keeps the underlying integer stable so cached entries
        // survive the rename. Stable across enum reordering only if the int values are
        // explicit; the codebase's MarketRegime is explicitly numbered for this reason.
        string scope = regime is null
            ? $"MLCalibration:Model:{modelId}"
            : $"MLCalibration:Model:{modelId}:Regime:{(int)regime.Value}";

        string stderrKey = $"{scope}:EceStderr";
        string computedAtKey = $"{scope}:EceStderrComputedAt";
        string rowVersionKey = $"{scope}:EceStderrModelRowVersion";

        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key == stderrKey || c.Key == computedAtKey || c.Key == rowVersionKey)
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        // RowVersion check: invalidate cache when model bytes change (champion swap, retrain
        // promotion). A wall-clock-fresh cache from a stale snapshot is worse than no cache.
        if (!rows.TryGetValue(rowVersionKey, out var rvRaw) ||
            !uint.TryParse(rvRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cachedRowVersion) ||
            cachedRowVersion != currentRowVersion)
        {
            return null;
        }

        // RoundtripKind on its own is sufficient for ISO-8601 "O" format strings written by
        // PersistBootstrapCacheAsync; combining with AssumeUniversal throws ArgumentException.
        if (!rows.TryGetValue(computedAtKey, out var atRaw) ||
            !DateTime.TryParse(atRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var computedAt))
        {
            return null;
        }

        if ((nowUtc - computedAt.ToUniversalTime()).TotalHours > staleHours)
            return null;

        if (!rows.TryGetValue(stderrKey, out var stderrRaw) ||
            !double.TryParse(stderrRaw, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var stderr) ||
            !double.IsFinite(stderr) || stderr < 0)
        {
            return null;
        }

        return stderr;
    }

    /// <summary>
    /// Appends bootstrap-cache specs (stderr, computed-at, RowVersion fingerprint) to the
    /// caller-supplied accumulator. Caller flushes the full set in one batched upsert at the
    /// end of the cycle, so a model with N matched regimes produces a single round-trip
    /// instead of (1 + N) per-scope round-trips.
    /// </summary>
    private static void AppendBootstrapCacheSpecs(
        List<EngineConfigUpsertSpec> pending,
        long modelId,
        MarketRegimeEnum? regime,
        double stderr,
        uint rowVersion,
        DateTime nowUtc)
    {
        // Use the integer enum value, not the string name — renaming a regime enum member
        // (e.g. Trending → TrendingUp) keeps the underlying integer stable so cached entries
        // survive the rename. Stable across enum reordering only if the int values are
        // explicit; the codebase's MarketRegime is explicitly numbered for this reason.
        string scope = regime is null
            ? $"MLCalibration:Model:{modelId}"
            : $"MLCalibration:Model:{modelId}:Regime:{(int)regime.Value}";

        pending.Add(new($"{scope}:EceStderr",
            stderr.ToString("F6", CultureInfo.InvariantCulture),
            ConfigDataType.Decimal,
            "Bootstrap-derived ECE stderr cached for the trend-signal stderr gate.",
            false));
        pending.Add(new($"{scope}:EceStderrComputedAt",
            nowUtc.ToString("O", CultureInfo.InvariantCulture),
            ConfigDataType.String,
            "UTC timestamp when the bootstrap-derived ECE stderr was last recomputed.",
            false));
        pending.Add(new($"{scope}:EceStderrModelRowVersion",
            rowVersion.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "MLModel.RowVersion at the time the cached stderr was computed; mismatches invalidate the cache.",
            false));
    }
}
