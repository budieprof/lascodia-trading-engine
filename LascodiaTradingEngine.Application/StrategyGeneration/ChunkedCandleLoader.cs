using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Loads candle data in chunks of N symbols at a time instead of materializing
/// all symbols' candles into memory simultaneously. Reduces peak memory from
/// O(all_symbols x candles_per_symbol) to O(chunk_size x candles_per_symbol)
/// during the loading phase.
/// </summary>
internal static class ChunkedCandleLoader
{
    internal const int DefaultChunkSize = 5;

    internal static async Task LoadChunkedAsync(
        DbContext db,
        CandleLruCache candleCache,
        int screeningMonths,
        IReadOnlyList<string> symbols,
        Timeframe timeframe,
        int chunkSize,
        Action? onEviction,
        ILogger? logger,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var scaledMonths = ScaleScreeningWindowForTimeframe(screeningMonths, timeframe);
        var screeningFrom = timeProvider.GetUtcNow().UtcDateTime.AddMonths(-scaledMonths);

        for (int i = 0; i < symbols.Count; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = symbols.Skip(i).Take(chunkSize).ToList();

            var uncached = chunk
                .Where(s => !candleCache.TryGet((s, timeframe), out _))
                .ToList();

            if (uncached.Count == 0)
            {
                logger?.LogDebug(
                    "ChunkedCandleLoader: chunk {ChunkStart}-{ChunkEnd} all cached, skipping",
                    i, i + chunk.Count - 1);
                continue;
            }

            var candles = await db.Set<Candle>()
                .Where(c => uncached.Contains(c.Symbol)
                         && c.Timeframe == timeframe
                         && c.Timestamp >= screeningFrom
                         && c.IsClosed
                         && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .Select(c => new Candle
                {
                    Id = c.Id,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume,
                    Timestamp = c.Timestamp,
                    Symbol = c.Symbol,
                })
                .ToListAsync(ct);

            var grouped = candles.GroupBy(c => c.Symbol);

            foreach (var group in grouped)
            {
                while (candleCache.IsFull)
                {
                    var evicted = candleCache.EvictLru();
                    if (evicted == null) break;
                    onEviction?.Invoke();
                }

                candleCache.Put((group.Key, timeframe), group.ToList());
            }

            logger?.LogDebug(
                "ChunkedCandleLoader: loaded chunk {ChunkStart}-{ChunkEnd}, {UncachedCount} uncached symbols, {CandleCount} candles fetched",
                i, i + chunk.Count - 1, uncached.Count, candles.Count);
        }
    }

    internal static async Task LoadChunkedAsync(
        DbContext db,
        CandleLruCache candleCache,
        int screeningMonths,
        IReadOnlyList<string> symbols,
        Timeframe timeframe,
        int chunkSize,
        Action? onEviction,
        ILogger? logger,
        CancellationToken ct)
        => await LoadChunkedAsync(
            db,
            candleCache,
            screeningMonths,
            symbols,
            timeframe,
            chunkSize,
            onEviction,
        logger,
        TimeProvider.System,
        ct);

}
