using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Queries the SentimentSnapshot table for the latest sentiment scores
/// for a symbol's base and quote currencies. Cached for 30 minutes per symbol.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ISentimentProvider))]
public class SentimentProvider : ISentimentProvider
{
    private readonly IReadApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SentimentProvider> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    public SentimentProvider(
        IReadApplicationDbContext db,
        IMemoryCache cache,
        ILogger<SentimentProvider> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<(decimal BaseSentiment, decimal QuoteSentiment)> GetSentimentAsync(
        string symbol, CancellationToken ct)
    {
        var cacheKey = $"Sentiment:{symbol}";

        if (_cache.TryGetValue(cacheKey, out (decimal Base, decimal Quote) cached))
            return cached;

        var (baseCurrency, quoteCurrency) = ExtractCurrencies(symbol);
        var dbContext = _db.GetDbContext();

        var baseSentiment = await dbContext
            .Set<SentimentSnapshot>()
            .AsNoTracking()
            .Where(s => s.Currency == baseCurrency && !s.IsDeleted)
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefaultAsync(ct);

        var quoteSentiment = await dbContext
            .Set<SentimentSnapshot>()
            .AsNoTracking()
            .Where(s => s.Currency == quoteCurrency && !s.IsDeleted)
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefaultAsync(ct);

        var result = (
            BaseSentiment: baseSentiment?.SentimentScore ?? 0m,
            QuoteSentiment: quoteSentiment?.SentimentScore ?? 0m
        );

        _logger.LogDebug("Sentiment for {Symbol}: base={Base}, quote={Quote}",
            symbol, result.BaseSentiment, result.QuoteSentiment);

        _cache.Set(cacheKey, result,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });

        return result;
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

        // Non-6-char symbols — cannot reliably extract two currencies
        return (string.Empty, string.Empty);
    }
}
