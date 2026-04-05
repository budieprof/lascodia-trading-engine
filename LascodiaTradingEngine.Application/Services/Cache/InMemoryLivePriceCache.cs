using System.Collections.Concurrent;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services.Cache;

/// <summary>
/// In-memory implementation of <see cref="ILivePriceCache"/> using a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Provides O(1) thread-safe reads and writes for live price lookups.
/// Prices older than <see cref="MaxStalenessSeconds"/> are treated as stale and excluded from <see cref="Get"/>.
/// </summary>
public class InMemoryLivePriceCache : ILivePriceCache
{
    /// <summary>
    /// Maximum age (in seconds) before a cached price is considered stale.
    /// Stale prices return null from <see cref="Get"/> to prevent risk checks
    /// and strategy evaluators from operating on outdated data.
    /// </summary>
    private const int MaxStalenessSeconds = 120;

    private readonly ConcurrentDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> _store = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Update(string symbol, decimal bid, decimal ask, DateTime timestamp)
        => _store[symbol] = (bid, ask, timestamp);

    /// <inheritdoc />
    public (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol)
    {
        if (!_store.TryGetValue(symbol, out var price))
            return null;

        // Reject stale prices — if the EA tick feed stops, callers should see
        // null (data unavailable) rather than an arbitrarily old price.
        if ((DateTime.UtcNow - price.Timestamp).TotalSeconds > MaxStalenessSeconds)
            return null;

        return price;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> GetAll()
        => _store;
}
