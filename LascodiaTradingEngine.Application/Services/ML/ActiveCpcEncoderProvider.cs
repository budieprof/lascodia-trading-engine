using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Resolves the active <see cref="MLCpcEncoder"/> for a (symbol, timeframe, regime) triple
/// from the read-context, caching for a short TTL to absorb the high call rate from the
/// inference path.
///
/// <para>
/// Regime-aware lookup: a non-null regime first tries a regime-specific encoder and, on miss,
/// falls back to the global (null-regime) encoder. The global result is cached under the
/// regime-specific key so repeat misses don't requery — cache eviction on encoder promotion
/// is handled by the short 30-minute TTL.
/// </para>
///
/// <para>
/// Registered as a singleton so the cache is shared across all scopes. A fresh DI scope is
/// created per cache miss so a scoped <see cref="IReadApplicationDbContext"/> can resolve
/// without leaking a long-lived DbContext.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IActiveCpcEncoderProvider))]
public sealed class ActiveCpcEncoderProvider : IActiveCpcEncoderProvider
{
    private const string CacheKeyPrefix = "MLCpcEncoder:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache          _cache;
    private readonly IServiceScopeFactory  _scopeFactory;

    public ActiveCpcEncoderProvider(IMemoryCache cache, IServiceScopeFactory scopeFactory)
    {
        _cache        = cache;
        _scopeFactory = scopeFactory;
    }

    public async Task<MLCpcEncoder?> GetAsync(
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime,
        CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(symbol, timeframe, regime);
        if (_cache.TryGetValue<MLCpcEncoder>(cacheKey, out var cached) && cached is not null)
            return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var ctx = readDb.GetDbContext();

        MLCpcEncoder? encoder = null;
        if (regime is not null)
        {
            encoder = await LoadAsync(ctx, symbol, timeframe, regime, ct);
        }
        encoder ??= await LoadAsync(ctx, symbol, timeframe, null, ct);

        if (encoder is not null)
        {
            _cache.Set(
                cacheKey,
                encoder,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CacheDuration,
                    Size = 1
                });
        }

        return encoder;
    }

    public void Invalidate(
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime)
    {
        if (regime is not null)
        {
            _cache.Remove(BuildCacheKey(symbol, timeframe, regime));
            return;
        }

        _cache.Remove(BuildCacheKey(symbol, timeframe, null));
        foreach (var concreteRegime in Enum.GetValues<global::LascodiaTradingEngine.Domain.Enums.MarketRegime>())
            _cache.Remove(BuildCacheKey(symbol, timeframe, concreteRegime));
    }

    private static async Task<MLCpcEncoder?> LoadAsync(
        DbContext ctx,
        string symbol,
        Timeframe timeframe,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime,
        CancellationToken ct)
    {
        var q = ctx.Set<MLCpcEncoder>()
            .AsNoTracking()
            .Where(e => e.Symbol    == symbol &&
                        e.Timeframe == timeframe &&
                        e.IsActive  && !e.IsDeleted &&
                        e.EncoderBytes != null);

        q = regime is null
            ? q.Where(e => e.Regime == null)
            : q.Where(e => e.Regime == regime);

        return await q
            .OrderByDescending(e => e.TrainedAt)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string BuildCacheKey(string symbol, Timeframe timeframe, global::LascodiaTradingEngine.Domain.Enums.MarketRegime? regime)
        => regime is null
            ? $"{CacheKeyPrefix}{symbol}:{timeframe}:global"
            : $"{CacheKeyPrefix}{symbol}:{timeframe}:{regime}";
}
