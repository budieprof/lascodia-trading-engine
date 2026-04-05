using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Queries the EconomicEvent table for the nearest upcoming High-impact event
/// affecting either currency in the given symbol. Cached for 1 minute per symbol.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(INewsProximityProvider))]
public class NewsProximityProvider : INewsProximityProvider
{
    private readonly IReadApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NewsProximityProvider> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LookAheadWindow = TimeSpan.FromDays(7);

    public NewsProximityProvider(
        IReadApplicationDbContext db,
        IMemoryCache cache,
        ILogger<NewsProximityProvider> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<double> GetMinutesUntilNextEventAsync(string symbol, CancellationToken ct)
    {
        var cacheKey = $"NewsProximity:{symbol}";

        if (_cache.TryGetValue(cacheKey, out double cachedMinutes))
            return cachedMinutes;

        var (baseCurrency, quoteCurrency) = ExtractCurrencies(symbol);
        var now = DateTime.UtcNow;
        var cutoff = now + LookAheadWindow;

        var dbContext = _db.GetDbContext();

        var nextEvent = await dbContext
            .Set<EconomicEvent>()
            .AsNoTracking()
            .Where(e => e.Impact == EconomicImpact.High
                     && !e.IsDeleted
                     && (e.Currency == baseCurrency || e.Currency == quoteCurrency)
                     && e.ScheduledAt > now
                     && e.ScheduledAt < cutoff)
            .OrderBy(e => e.ScheduledAt)
            .FirstOrDefaultAsync(ct);

        double minutes;
        if (nextEvent is not null)
        {
            minutes = (nextEvent.ScheduledAt - DateTime.UtcNow).TotalMinutes;
            _logger.LogDebug("Next High-impact event for {Symbol}: {Title} in {Minutes:F0}m",
                symbol, nextEvent.Title, minutes);
        }
        else
        {
            minutes = double.MaxValue;
            _logger.LogDebug("No High-impact events within 7 days for {Symbol}", symbol);
        }

        _cache.Set(cacheKey, minutes,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });

        return minutes;
    }

    /// <summary>
    /// Extracts base and quote currency codes from a symbol string.
    /// For 6-character symbols (e.g. "EURUSD"), splits into first 3 + last 3.
    /// For other formats, returns the full symbol as base with an empty quote.
    /// </summary>
    private static (string Base, string Quote) ExtractCurrencies(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return (string.Empty, string.Empty);

        if (symbol.Length == 6)
            return (symbol[..3].ToUpperInvariant(), symbol[3..].ToUpperInvariant());

        // Non-6-char symbols (GOLD, US500, etc.) — cannot reliably extract two currencies
        return (string.Empty, string.Empty);
    }
}
