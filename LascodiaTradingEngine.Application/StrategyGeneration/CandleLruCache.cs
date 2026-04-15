using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Lightweight LRU cache for candle series keyed by symbol and timeframe.
/// </summary>
/// <remarks>
/// The cache enforces a global candle-count budget rather than a fixed entry count so large
/// symbols cannot silently blow up memory usage during a screening cycle.
/// </remarks>
internal sealed class CandleLruCache
{
    private readonly int _maxCandles;
    private readonly LinkedList<(string Symbol, Timeframe Tf)> _accessOrder = new();
    private readonly Dictionary<(string, Timeframe), (LinkedListNode<(string, Timeframe)> Node, List<Candle> Candles)> _entries = new();
    private int _totalCandles;

    public CandleLruCache(int maxCandles) => _maxCandles = maxCandles;

    public bool IsFull => _totalCandles >= _maxCandles;

    /// <summary>
    /// Attempts to fetch a cached candle series and refreshes its recency position when found.
    /// </summary>
    public bool TryGet((string, Timeframe) key, out List<Candle> candles)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            _accessOrder.Remove(entry.Node);
            _accessOrder.AddFirst(entry.Node);
            candles = entry.Candles;
            return true;
        }

        candles = [];
        return false;
    }

    /// <summary>
    /// Inserts or replaces a candle series, evicting least-recently-used entries until the
    /// global candle budget can accommodate the new payload.
    /// </summary>
    public int Put((string, Timeframe) key, List<Candle> candles)
    {
        // Replacement should first free the previous payload so the budget check reflects the
        // true post-update size instead of double-counting the old and new series.
        if (_entries.TryGetValue(key, out var existing))
        {
            _totalCandles -= existing.Candles.Count;
            _accessOrder.Remove(existing.Node);
            _entries.Remove(key);
        }

        if (candles.Count >= _maxCandles)
            return 0;

        int evictions = 0;
        // Evict oldest entries until the incoming series fits inside the global cache budget.
        while (_entries.Count > 0 && _totalCandles + candles.Count > _maxCandles)
        {
            if (EvictLru() == null)
                break;
            evictions++;
        }

        var node = _accessOrder.AddFirst(key);
        _entries[key] = (node, candles);
        _totalCandles += candles.Count;
        return evictions;
    }

    /// <summary>
    /// Evicts the least-recently-used candle series and returns its key.
    /// </summary>
    public (string Symbol, Timeframe Tf)? EvictLru()
    {
        if (_accessOrder.Last is null)
            return null;

        var lruKey = _accessOrder.Last.Value;
        if (_entries.TryGetValue(lruKey, out var entry))
        {
            _totalCandles -= entry.Candles.Count;
            _accessOrder.Remove(entry.Node);
            _entries.Remove(lruKey);
            return lruKey;
        }

        return null;
    }
}
