using System.Collections.Concurrent;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services.Cache;

public class InMemoryLivePriceCache : ILivePriceCache
{
    private readonly ConcurrentDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> _store = new();

    public void Update(string symbol, decimal bid, decimal ask, DateTime timestamp)
        => _store[symbol] = (bid, ask, timestamp);

    public (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol)
        => _store.TryGetValue(symbol, out var price) ? price : null;

    public IReadOnlyDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> GetAll()
        => _store;
}
