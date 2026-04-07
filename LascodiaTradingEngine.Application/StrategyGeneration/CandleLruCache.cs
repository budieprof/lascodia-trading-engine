using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

internal sealed class CandleLruCache
{
    private readonly int _maxCandles;
    private readonly LinkedList<(string Symbol, Timeframe Tf)> _accessOrder = new();
    private readonly Dictionary<(string, Timeframe), (LinkedListNode<(string, Timeframe)> Node, List<Candle> Candles)> _entries = new();
    private int _totalCandles;

    public CandleLruCache(int maxCandles) => _maxCandles = maxCandles;

    public bool IsFull => _totalCandles >= _maxCandles;

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

    public int Put((string, Timeframe) key, List<Candle> candles)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            _totalCandles -= existing.Candles.Count;
            _accessOrder.Remove(existing.Node);
            _entries.Remove(key);
        }

        if (candles.Count >= _maxCandles)
            return 0;

        int evictions = 0;
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
