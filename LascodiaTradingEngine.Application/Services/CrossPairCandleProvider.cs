using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Queries the Candle table for correlated pairs defined in <see cref="MLFeatureHelper.CrossPairMap"/>
/// and caches the results for 5 minutes to reduce repeated database hits during batch scoring.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICrossPairCandleProvider))]
public class CrossPairCandleProvider : ICrossPairCandleProvider
{
    private readonly IReadApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CrossPairCandleProvider> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public CrossPairCandleProvider(
        IReadApplicationDbContext db,
        IMemoryCache cache,
        ILogger<CrossPairCandleProvider> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Candle>>> GetCrossPairCandlesAsync(
        string primarySymbol, Timeframe timeframe, DateTime asOf, int barCount, CancellationToken ct)
    {
        var cacheKey = $"CrossPairCandles:{primarySymbol}:{timeframe}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, IReadOnlyList<Candle>>? cached) && cached is not null)
            return cached;

        if (!MLFeatureHelper.CrossPairMap.TryGetValue(primarySymbol, out var correlatedSymbols))
        {
            _logger.LogDebug("No cross-pair map entry for {Symbol}; returning empty dictionary", primarySymbol);
            return new Dictionary<string, IReadOnlyList<Candle>>();
        }

        var result = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);
        var dbContext = _db.GetDbContext();

        // Query up to 3 correlated pairs
        foreach (var symbol in correlatedSymbols.Take(3))
        {
            try
            {
                var candles = await dbContext
                    .Set<Candle>()
                    .AsNoTracking()
                    .Where(c => c.Symbol == symbol
                             && c.Timeframe == timeframe
                             && c.Timestamp < asOf
                             && !c.IsDeleted)
                    .OrderByDescending(c => c.Timestamp)
                    .Take(barCount)
                    .OrderBy(c => c.Timestamp)
                    .ToListAsync(ct);

                if (candles.Count > 0)
                {
                    result[symbol] = candles;
                }
                else
                {
                    _logger.LogDebug("No candles found for correlated pair {Symbol}/{Timeframe} before {AsOf}",
                        symbol, timeframe, asOf);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to load candles for correlated pair {Symbol}/{Timeframe}", symbol, timeframe);
            }
        }

        _cache.Set(cacheKey, (IReadOnlyDictionary<string, IReadOnlyList<Candle>>)result,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });

        return result;
    }
}
