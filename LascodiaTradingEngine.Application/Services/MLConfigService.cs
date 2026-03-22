using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Cache-aside config lookups, confidence dampening, and regime confidence scaling.
/// Extracted from <see cref="MLSignalScorer"/> for testability and to reduce class size.
/// </summary>
internal sealed class MLConfigService
{
    private static readonly TimeSpan DbQueryTimeout = TimeSpan.FromSeconds(5);

    private const string RegimeAccCacheKeyPrefix = "MLRegimeAcc:";
    private static readonly TimeSpan RegimeAccCacheDuration = TimeSpan.FromMinutes(15);

    private const string RegimePenaltyCacheKeyPrefix = "MLRegimePenalty:";
    private static readonly TimeSpan RegimePenaltyCacheDuration = TimeSpan.FromMinutes(5);

    private const string ColdStartCacheKeyPrefix = "MLColdStart:";
    private static readonly TimeSpan ColdStartCacheDuration = TimeSpan.FromMinutes(5);

    private const string CrossTfCacheKeyPrefix = "MLCrossTf:";
    private static readonly TimeSpan CrossTfCacheDuration = TimeSpan.FromMinutes(5);

    private const string KellyLiveCacheKeyPrefix = "MLKellyLive:";
    private static readonly TimeSpan KellyLiveCacheDuration = TimeSpan.FromMinutes(15);

    private const string CooldownCacheKeyPrefix = "MLCooldownExp:";
    private static readonly TimeSpan CooldownCacheDuration = TimeSpan.FromMinutes(2);

    private const string ConsensusCacheKey = "MLScoring:ConsensusMinModels";
    private static readonly TimeSpan ConsensusCacheDuration = TimeSpan.FromMinutes(5);

    private const string MultiTfWeightsCacheKey = "MLScoring:MultiTfWeights";
    private static readonly TimeSpan MultiTfWeightsCacheDuration = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;
    private readonly ILogger      _logger;

    internal MLConfigService(IMemoryCache cache, ILogger logger)
    {
        _cache  = cache;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Confidence dampening pipeline
    // ═══════════════════════════════════════════════════════════════════════════

    internal async Task<double> ApplyAllDampeningsAsync(
        double confidence, long modelId, string? currentRegime,
        string symbol, Timeframe timeframe,
        DbContext db, CancellationToken cancellationToken)
    {
        // Regime confidence scaling
        if (currentRegime is not null)
        {
            confidence = await ApplyRegimeConfidenceScaling(
                confidence, modelId, currentRegime, symbol, timeframe,
                db, cancellationToken);
        }

        // Regime-transition dampening
        confidence = await ApplyConfigDampening(
            confidence, $"{RegimePenaltyCacheKeyPrefix}{symbol}:{timeframe}",
            $"MLRegimeTransition:{symbol}:{timeframe}:PenaltyFactor",
            RegimePenaltyCacheDuration, "RegimeTransitionDampening",
            symbol, timeframe, modelId, db, cancellationToken);

        // Cold-start dampening
        confidence = await ApplyConfigDampening(
            confidence, $"{ColdStartCacheKeyPrefix}{symbol}:{timeframe}:Factor",
            $"MLColdStart:{symbol}:{timeframe}:Factor",
            ColdStartCacheDuration, "ColdStartDampening",
            symbol, timeframe, modelId, db, cancellationToken);

        // Cross-timeframe consistency dampening
        confidence = await ApplyConfigDampening(
            confidence, $"{CrossTfCacheKeyPrefix}{symbol}:{timeframe}:ConsistencyFactor",
            $"MLCrossTimeframe:{symbol}:{timeframe}:ConsistencyFactor",
            CrossTfCacheDuration, "CrossTfConsistency",
            symbol, timeframe, modelId, db, cancellationToken);

        return confidence;
    }

    /// <summary>Returns true if scoring should be suppressed due to active cooldown.</summary>
    internal async Task<bool> IsCooldownActiveAsync(
        string symbol, Timeframe timeframe, long modelId,
        DbContext db, CancellationToken cancellationToken)
    {
        var cdCacheKey  = $"{CooldownCacheKeyPrefix}{symbol}:{timeframe}:ExpiresAt";
        var cdConfigKey = $"MLCooldown:{symbol}:{timeframe}:ExpiresAt";
        var cooldownExpiry = await GetCachedConfigNullableAsync<DateTime>(
            cdCacheKey, cdConfigKey,
            s => DateTime.TryParse(s,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed) ? parsed : null,
            CooldownCacheDuration, db, cancellationToken);

        if (cooldownExpiry.HasValue && DateTime.UtcNow < cooldownExpiry.Value)
        {
            _logger.LogInformation(
                "Cooldown gate: {Symbol}/{Tf} model {Id} — signal suppressed until {Exp:HH:mm} UTC.",
                symbol, timeframe, modelId, cooldownExpiry.Value);
            return true;
        }
        return false;
    }

    internal async Task<int> GetConsensusMinModelsAsync(
        DbContext db, CancellationToken cancellationToken)
    {
        return await GetCachedConfigAsync(
            ConsensusCacheKey, "MLScoring:ConsensusMinModels", 1,
            s => int.TryParse(s, out var v) && v >= 1 ? v : (int?)null,
            ConsensusCacheDuration, db, cancellationToken);
    }

    internal async Task<double> ApplyLiveKellyMultiplierAsync(
        double kellyFraction, string symbol, Timeframe timeframe, long modelId,
        DbContext db, CancellationToken cancellationToken)
    {
        var klCacheKey  = $"{KellyLiveCacheKeyPrefix}{symbol}:{timeframe}:LiveMultiplier";
        var klConfigKey = $"MLKelly:{symbol}:{timeframe}:LiveMultiplier";
        var kellyLiveMult = await GetCachedConfigAsync(
            klCacheKey, klConfigKey, 1.0,
            s => double.TryParse(s,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) ? v : (double?)null,
            KellyLiveCacheDuration, db, cancellationToken);

        if (kellyLiveMult < 1.0)
        {
            kellyFraction *= kellyLiveMult;
            _logger.LogDebug(
                "KellyLiveAdvisor: {Symbol}/{Tf} model {Id} — liveMultiplier={Mult:F3} kelly→{Kelly:F4}",
                symbol, timeframe, modelId, kellyLiveMult, kellyFraction);
        }

        return kellyFraction;
    }

    /// <summary>
    /// Returns the timeframe weight for the multi-TF probability blend.
    /// Reads from EngineConfig key <c>MLScoring:MultiTfWeight:{Tf}</c> with
    /// defaults: D1=3, H4=2, all others=1.
    /// </summary>
    internal async Task<double> GetMultiTfWeightAsync(
        Timeframe tf, DbContext db, CancellationToken cancellationToken)
    {
        double defaultWeight = tf switch
        {
            Timeframe.D1 => 3.0,
            Timeframe.H4 => 2.0,
            _            => 1.0,
        };

        var cacheKey  = $"{MultiTfWeightsCacheKey}:{tf}";
        var configKey = $"MLScoring:MultiTfWeight:{tf}";
        return await GetCachedConfigAsync(
            cacheKey, configKey, defaultWeight,
            s => double.TryParse(s,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) && v > 0.0 ? v : (double?)null,
            MultiTfWeightsCacheDuration, db, cancellationToken);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<double> ApplyRegimeConfidenceScaling(
        double confidence, long modelId, string currentRegime,
        string symbol, Timeframe timeframe,
        DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var regimeCacheKey = $"{RegimeAccCacheKeyPrefix}{modelId}:{currentRegime}";
            if (!_cache.TryGetValue<double?>(regimeCacheKey, out var regimeAcc))
            {
                double? fetched = null;
                if (Enum.TryParse<MarketRegimeEnum>(currentRegime, out var regimeEnum))
                {
                    using var cts = CreateLinkedTimeout(cancellationToken);
                    var row = await db.Set<MLModelRegimeAccuracy>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            r => r.MLModelId == modelId && r.Regime == regimeEnum,
                            cts.Token);
                    fetched = row?.Accuracy;
                }
                regimeAcc = fetched;
                _cache.Set(regimeCacheKey, regimeAcc, RegimeAccCacheDuration);
            }

            if (regimeAcc.HasValue && regimeAcc.Value < 0.5)
            {
                double scale = 0.5 + regimeAcc.Value;
                scale = Math.Clamp(scale, 0.5, 1.0);
                confidence *= scale;
                _logger.LogDebug(
                    "RegimeConfidenceScale: {Symbol}/{Tf} model {Id} regime={Regime} " +
                    "regimeAcc={Acc:P1} → scale={Scale:F3} confidence={Conf:F4}",
                    symbol, timeframe, modelId, currentRegime,
                    regimeAcc.Value, scale, confidence);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Regime accuracy lookup timed out for {Symbol}/{Tf}", symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Regime accuracy lookup failed for {Symbol}/{Tf} — skipping regime scaling",
                symbol, timeframe);
        }

        return confidence;
    }

    private async Task<double> ApplyConfigDampening(
        double confidence, string cacheKey, string configKey, TimeSpan cacheDuration,
        string label, string symbol, Timeframe timeframe, long modelId,
        DbContext db, CancellationToken cancellationToken)
    {
        var factor = await GetCachedConfigAsync(
            cacheKey, configKey, 1.0,
            s => double.TryParse(s,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pv) ? pv : (double?)null,
            cacheDuration, db, cancellationToken);

        if (factor < 1.0)
        {
            double before = confidence;
            confidence *= factor;
            _logger.LogDebug(
                "{Label}: {Symbol}/{Tf} model {Id} — factor={Factor:F3} confidence {Before:F4}→{After:F4}",
                label, symbol, timeframe, modelId, factor, before, confidence);
        }

        return confidence;
    }

    private async Task<T> GetCachedConfigAsync<T>(
        string cacheKey, string configKey, T defaultValue,
        Func<string, T?> parser, TimeSpan cacheDuration,
        DbContext db, CancellationToken cancellationToken)
        where T : struct
    {
        if (_cache.TryGetValue<T>(cacheKey, out var cached))
            return cached;

        T value = defaultValue;
        try
        {
            using var cts = CreateLinkedTimeout(cancellationToken);
            var entry = await db.Set<EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == configKey, cts.Token);

            if (entry?.Value is not null)
            {
                var parsed = parser(entry.Value);
                if (parsed.HasValue) value = parsed.Value;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("{ConfigKey} lookup timed out — using default", configKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{ConfigKey} lookup failed — using default", configKey);
        }

        _cache.Set(cacheKey, value, cacheDuration);
        return value;
    }

    private async Task<T?> GetCachedConfigNullableAsync<T>(
        string cacheKey, string configKey,
        Func<string, T?> parser, TimeSpan cacheDuration,
        DbContext db, CancellationToken cancellationToken)
        where T : struct
    {
        if (_cache.TryGetValue<T?>(cacheKey, out var cached))
            return cached;

        T? value = null;
        try
        {
            using var cts = CreateLinkedTimeout(cancellationToken);
            var entry = await db.Set<EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == configKey, cts.Token);

            if (entry?.Value is not null)
                value = parser(entry.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("{ConfigKey} lookup timed out", configKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{ConfigKey} lookup failed", configKey);
        }

        _cache.Set(cacheKey, value, cacheDuration);
        return value;
    }

    private static CancellationTokenSource CreateLinkedTimeout(CancellationToken parent)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cts.CancelAfter(DbQueryTimeout);
        return cts;
    }
}
