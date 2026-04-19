using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

using MarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;

/// <summary>
/// Resolves regime-specific <see cref="OptimizationHealthScorer.HealthWeights"/> vectors
/// from <see cref="EngineConfig"/>, with validation (weights must sum to 1.0 ± tolerance,
/// each in [0, 1]) and hot-reload via a short TTL cache. Falls back per-regime to the
/// static defaults in <see cref="OptimizationHealthScorer.RegimeWeights"/>.
///
/// <para>
/// Config key format: <c>StrategyHealth:RegimeWeights:&lt;Regime&gt;</c> (e.g.
/// <c>StrategyHealth:RegimeWeights:Trending</c>) with a JSON object value
/// <c>{"WinRate":0.15,"ProfitFactor":0.30,"Drawdown":0.15,"Sharpe":0.20,"SampleSize":0.20}</c>.
/// </para>
///
/// <para>
/// Invalid entries (malformed JSON, missing fields, NaN, out-of-range weights, sum ≠ 1.0)
/// are logged and discarded so the regime falls back to the default. This keeps a single
/// bad config entry from taking the whole health pipeline down.
/// </para>
///
/// <para>
/// Registered as a singleton so the memory cache survives across requests. Callers hit
/// the cache-based API; the DB is read at most once per <see cref="CacheTtl"/> per regime.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class RegimeHealthWeightsProvider
{
    internal const string ConfigKeyPrefix = "StrategyHealth:RegimeWeights:";
    internal const decimal SumTolerance = 0.005m;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RegimeHealthWeightsProvider> _logger;

    public RegimeHealthWeightsProvider(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<RegimeHealthWeightsProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    /// <summary>
    /// Returns the resolved weight map for all regimes. Entries missing / invalid in
    /// config are omitted so callers fall back to the static defaults at lookup time.
    /// The map is cached for <see cref="CacheTtl"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<MarketRegime, OptimizationHealthScorer.HealthWeights>> GetAsync(CancellationToken ct)
    {
        const string cacheKey = "regime-health-weights";
        if (_cache.TryGetValue<IReadOnlyDictionary<MarketRegime, OptimizationHealthScorer.HealthWeights>>(cacheKey, out var cached)
            && cached is not null)
        {
            return cached;
        }

        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        var resolved = new Dictionary<MarketRegime, OptimizationHealthScorer.HealthWeights>();

        foreach (MarketRegime regime in Enum.GetValues<MarketRegime>())
        {
            var key = ConfigKeyPrefix + regime;
            var rawValue = await readCtx.GetDbContext()
                .Set<EngineConfig>()
                .AsNoTracking()
                .Where(c => c.Key == key)
                .Select(c => c.Value)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            if (TryParseWeights(rawValue, out var parsed, out var reason))
            {
                resolved[regime] = parsed;
            }
            else
            {
                _logger.LogWarning(
                    "RegimeHealthWeightsProvider: EngineConfig {Key}={RawValue} is invalid ({Reason}); using default weights for {Regime}",
                    key, rawValue, reason, regime);
            }
        }

        _cache.Set(cacheKey, (IReadOnlyDictionary<MarketRegime, OptimizationHealthScorer.HealthWeights>)resolved, CacheTtl);
        return resolved;
    }

    /// <summary>
    /// Parses a JSON weights object and validates it. Returns true on success.
    /// Validation: each weight in [0, 1], no NaN/Infinity, sum within <see cref="SumTolerance"/> of 1.0.
    /// </summary>
    internal static bool TryParseWeights(
        string json,
        out OptimizationHealthScorer.HealthWeights weights,
        out string failureReason)
    {
        weights = default;
        failureReason = "";

        WeightsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WeightsDto>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            failureReason = $"malformed JSON: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            failureReason = "JSON deserialized to null";
            return false;
        }

        decimal[] values = { dto.WinRate, dto.ProfitFactor, dto.Drawdown, dto.Sharpe, dto.SampleSize };
        foreach (var v in values)
        {
            double d = (double)v;
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                failureReason = $"weight is NaN/Infinity: {v}";
                return false;
            }
            if (v < 0m || v > 1m)
            {
                failureReason = $"weight {v} outside [0, 1]";
                return false;
            }
        }

        decimal sum = values.Sum();
        if (Math.Abs(sum - 1m) > SumTolerance)
        {
            failureReason = $"weights sum to {sum:F4} (expected 1.0 ± {SumTolerance:F4})";
            return false;
        }

        weights = new OptimizationHealthScorer.HealthWeights(
            WinRate:      dto.WinRate,
            ProfitFactor: dto.ProfitFactor,
            Drawdown:     dto.Drawdown,
            Sharpe:       dto.Sharpe,
            SampleSize:   dto.SampleSize);
        return true;
    }

    private sealed class WeightsDto
    {
        public decimal WinRate      { get; set; }
        public decimal ProfitFactor { get; set; }
        public decimal Drawdown     { get; set; }
        public decimal Sharpe       { get; set; }
        public decimal SampleSize   { get; set; }
    }
}
