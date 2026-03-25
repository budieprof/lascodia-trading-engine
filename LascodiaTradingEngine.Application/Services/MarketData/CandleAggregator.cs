using System.Collections.Concurrent;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.MarketData;

/// <summary>A single price tick with bid/ask spread and timestamp.</summary>
public record Tick(string Symbol, decimal Bid, decimal Ask, DateTime Timestamp);

/// <summary>
/// Controls which price component of a tick is used to build OHLCV candles.
/// </summary>
public enum PriceMode { Mid, Bid, Ask }

/// <summary>
/// Accumulates live ticks into OHLCV candle bars across all configured timeframes.
/// When a tick arrives that belongs to the next time period, the previous candle is
/// closed and returned for persistence via <see cref="Application.MarketData.Commands.IngestCandle.IngestCandleCommand"/>.
/// </summary>
/// <remarks>
/// Thread-safe: each (symbol, timeframe) slot is protected by its own lock to allow
/// concurrent tick processing across different symbols without contention.
/// Designed to be registered as a Singleton in DI.
/// </remarks>
public interface ICandleAggregator
{
    /// <summary>
    /// Processes an incoming tick and returns any candles that closed as a result.
    /// Synthetic gap-fill candles (with <see cref="ClosedCandle.IsSynthetic"/> = true)
    /// are emitted for any skipped periods between the previous and current tick.
    /// </summary>
    IReadOnlyList<ClosedCandle> ProcessTick(Tick tick);

    /// <summary>
    /// Closes and returns all in-progress candles across every symbol and timeframe.
    /// Call on graceful shutdown to avoid losing the currently building bar.
    /// </summary>
    IReadOnlyList<ClosedCandle> FlushAll();

    /// <summary>
    /// Closes and returns all in-progress candles for a specific symbol.
    /// Useful when a symbol is unsubscribed at runtime.
    /// </summary>
    IReadOnlyList<ClosedCandle> FlushSymbol(string symbol);

    /// <summary>
    /// Removes candle slots that have not been updated since <paramref name="cutoff"/>.
    /// Call periodically to prevent unbounded memory growth for delisted symbols.
    /// Returns any in-progress candles that were flushed during purge so callers can persist them.
    /// </summary>
    IReadOnlyList<ClosedCandle> PurgeStaleEntries(DateTime cutoff);
}

/// <summary>
/// A completed OHLCV candle bar emitted by the aggregator.
/// </summary>
/// <param name="TickVolume">
/// Number of ticks that contributed to this candle (FX tick volume), not monetary/lot volume.
/// Synthetic gap-fill candles have a tick volume of 0.
/// </param>
public record ClosedCandle(
    string Symbol,
    Timeframe Timeframe,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long TickVolume,
    DateTime Timestamp,
    bool IsSynthetic = false);

public class CandleAggregator : ICandleAggregator
{
    /// <summary>
    /// Safety cap to prevent runaway allocation when a large time gap exists
    /// (e.g. weekend gap on M1 would be ~2880 bars — well within this limit).
    /// </summary>
    private const int MaxGapFill = 1000;

    /// <summary>
    /// Decimal places used when rounding the mid-price ((Bid + Ask) / 2).
    /// 10 digits preserves precision beyond any FX pair while avoiding
    /// repeating decimals from the division.
    /// </summary>
    private const int MidPriceDecimalPlaces = 10;

    /// <summary>
    /// Default maximum spread-to-mid ratio. Ticks where (Ask - Bid) / Mid exceeds
    /// this threshold are rejected as likely liquidity gaps. 0.01 = 1% of mid-price.
    /// </summary>
    private const decimal DefaultMaxSpreadRatio = 0.01m;

    private static readonly Timeframe[] AllTimeframes = Enum.GetValues<Timeframe>()
        .Where(tf => IsSupportedTimeframe(tf))
        .ToArray();

    private readonly ConcurrentDictionary<(string Symbol, Timeframe Tf), CandleSlot> _slots = new();
    private readonly PriceMode _priceMode;
    private readonly decimal _maxSpreadRatio;
    private readonly ILogger<CandleAggregator> _logger;
    private readonly TradingMetrics _metrics;

    public CandleAggregator(
        ILogger<CandleAggregator> logger,
        TradingMetrics metrics,
        PriceMode priceMode = PriceMode.Mid,
        decimal maxSpreadRatio = DefaultMaxSpreadRatio)
    {
        _logger = logger;
        _metrics = metrics;
        _priceMode = priceMode;
        _maxSpreadRatio = maxSpreadRatio;
    }

    public IReadOnlyList<ClosedCandle> ProcessTick(Tick tick)
    {
        ValidateTick(tick);

        // Reject ticks with abnormally wide spreads (liquidity gaps, stale quotes).
        var mid = (tick.Bid + tick.Ask) / 2m;
        if (mid > 0 && (tick.Ask - tick.Bid) / mid > _maxSpreadRatio)
        {
            _logger.LogDebug(
                "Rejected tick for {Symbol}: spread {Spread} exceeds max ratio {MaxRatio} (Bid={Bid}, Ask={Ask})",
                tick.Symbol, tick.Ask - tick.Bid, _maxSpreadRatio, tick.Bid, tick.Ask);
            _metrics.CandleTicksSpreadRejected.Add(1);
            return Array.Empty<ClosedCandle>();
        }

        var price = _priceMode switch
        {
            PriceMode.Bid => tick.Bid,
            PriceMode.Ask => tick.Ask,
            _ => Math.Round((tick.Bid + tick.Ask) / 2m, MidPriceDecimalPlaces, MidpointRounding.AwayFromZero)
        };

        List<ClosedCandle>? closed = null;

        foreach (var tf in AllTimeframes)
        {
            var periodStart = GetPeriodStart(tick.Timestamp, tf);
            var key = (tick.Symbol, tf);

            var slot = _slots.GetOrAdd(key, _ => new CandleSlot());

            var results = slot.Apply(tick.Symbol, tf, periodStart, price, tick.Timestamp, _logger, _metrics);
            if (results is not null)
            {
                closed ??= [];
                closed.AddRange(results);
            }
        }

        if (closed is not null)
        {
            foreach (var c in closed)
            {
                if (c.IsSynthetic)
                    _metrics.CandlesSynthetic.Add(1);
                else
                    _metrics.CandlesClosed.Add(1);
            }
        }

        return closed ?? (IReadOnlyList<ClosedCandle>)Array.Empty<ClosedCandle>();
    }

    public IReadOnlyList<ClosedCandle> FlushAll()
    {
        List<ClosedCandle>? flushed = null;

        foreach (var kvp in _slots)
        {
            var candle = kvp.Value.Flush(kvp.Key.Symbol, kvp.Key.Tf);
            if (candle is not null)
            {
                flushed ??= [];
                flushed.Add(candle);
            }
        }

        _logger.LogInformation("Flushed {Count} open candles on shutdown", flushed?.Count ?? 0);
        return flushed ?? (IReadOnlyList<ClosedCandle>)Array.Empty<ClosedCandle>();
    }

    public IReadOnlyList<ClosedCandle> FlushSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        List<ClosedCandle>? flushed = null;

        foreach (var tf in AllTimeframes)
        {
            var key = (symbol, tf);
            if (_slots.TryRemove(key, out var slot))
            {
                var candle = slot.Flush(symbol, tf);
                if (candle is not null)
                {
                    flushed ??= [];
                    flushed.Add(candle);
                }
            }
        }

        return flushed ?? (IReadOnlyList<ClosedCandle>)Array.Empty<ClosedCandle>();
    }

    public IReadOnlyList<ClosedCandle> PurgeStaleEntries(DateTime cutoff)
    {
        List<ClosedCandle>? flushed = null;

        foreach (var kvp in _slots)
        {
            var (isStale, candle) = kvp.Value.TryPurgeIfStale(kvp.Key.Symbol, kvp.Key.Tf, cutoff);
            if (isStale)
            {
                if (_slots.TryRemove(kvp.Key, out _))
                {
                    if (candle is not null)
                    {
                        flushed ??= [];
                        flushed.Add(candle);
                    }
                }
            }
        }

        var purgedCount = flushed?.Count ?? 0;
        if (purgedCount > 0)
        {
            _metrics.CandleSlotsPurged.Add(purgedCount);
            _logger.LogInformation("Purged {Count} stale candle slots (cutoff: {Cutoff:O})", purgedCount, cutoff);
        }

        return flushed ?? (IReadOnlyList<ClosedCandle>)Array.Empty<ClosedCandle>();
    }

    private static bool IsSupportedTimeframe(Timeframe tf) => tf is
        Timeframe.M1 or Timeframe.M5 or Timeframe.M15 or
        Timeframe.H1 or Timeframe.H4 or Timeframe.D1;

    internal static DateTime GetPeriodStart(DateTime timestamp, Timeframe tf)
    {
        if (timestamp.Kind == DateTimeKind.Unspecified)
            throw new ArgumentException(
                "Tick timestamp must have an explicit DateTimeKind (Utc or Local), not Unspecified.",
                nameof(timestamp));

        var utc = timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.ToUniversalTime();

        return tf switch
        {
            Timeframe.M1  => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc),
            Timeframe.M5  => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 5 * 5, 0, DateTimeKind.Utc),
            Timeframe.M15 => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 15 * 15, 0, DateTimeKind.Utc),
            Timeframe.H1  => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
            Timeframe.H4  => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour / 4 * 4, 0, 0, DateTimeKind.Utc),
            Timeframe.D1  => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
            _             => throw new ArgumentOutOfRangeException(nameof(tf), tf, "Unsupported timeframe")
        };
    }

    internal static DateTime GetNextPeriodStart(DateTime current, Timeframe tf) => tf switch
    {
        Timeframe.M1  => current.AddMinutes(1),
        Timeframe.M5  => current.AddMinutes(5),
        Timeframe.M15 => current.AddMinutes(15),
        Timeframe.H1  => current.AddHours(1),
        Timeframe.H4  => current.AddHours(4),
        Timeframe.D1  => current.AddDays(1),
        _             => throw new ArgumentOutOfRangeException(nameof(tf), tf, "Unsupported timeframe")
    };

    private static void ValidateTick(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        if (string.IsNullOrWhiteSpace(tick.Symbol))
            throw new ArgumentException("Tick symbol must not be null or empty.", nameof(tick));
        if (tick.Bid <= 0)
            throw new ArgumentException($"Tick Bid must be positive, got {tick.Bid}.", nameof(tick));
        if (tick.Ask <= 0)
            throw new ArgumentException($"Tick Ask must be positive, got {tick.Ask}.", nameof(tick));
        if (tick.Ask < tick.Bid)
            throw new ArgumentException($"Tick Ask ({tick.Ask}) must be >= Bid ({tick.Bid}).", nameof(tick));
    }

    /// <summary>
    /// Thread-safe wrapper around a single (symbol, timeframe) candle being built.
    /// All mutations are serialised via <c>lock</c> so concurrent ticks for the same
    /// symbol cannot corrupt OHLC state or produce duplicate closes.
    /// </summary>
    private sealed class CandleSlot
    {
        private readonly object _lock = new();
        private BuildingCandle? _current;
        private DateTime _lastUpdatedUtc;

        /// <summary>
        /// Atomically checks staleness and clears the slot under a single lock acquisition.
        /// Returns the in-progress candle (if any) so the caller can persist it before removal.
        /// This prevents the race where a tick updates the slot between a staleness check and
        /// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove"/>.
        /// </summary>
        public (bool IsStale, ClosedCandle? Flushed) TryPurgeIfStale(
            string symbol, Timeframe tf, DateTime cutoff)
        {
            lock (_lock)
            {
                if (_lastUpdatedUtc >= cutoff)
                    return (false, null);

                ClosedCandle? flushed = null;
                if (_current is not null)
                {
                    flushed = new ClosedCandle(
                        symbol, tf,
                        _current.Open, _current.High, _current.Low, _current.Close,
                        _current.TickCount, _current.PeriodStart);
                    _current = null;
                }

                return (true, flushed);
            }
        }

        /// <summary>
        /// Closes and returns the in-progress candle without starting a new one.
        /// Returns <c>null</c> if no candle is currently being built.
        /// </summary>
        public ClosedCandle? Flush(string symbol, Timeframe tf)
        {
            lock (_lock)
            {
                if (_current is null)
                    return null;

                var closed = new ClosedCandle(
                    symbol, tf,
                    _current.Open, _current.High, _current.Low, _current.Close,
                    _current.TickCount, _current.PeriodStart);

                _current = null;
                return closed;
            }
        }

        /// <summary>
        /// Applies a tick price. Returns closed candles (including synthetic gap-fills)
        /// if this tick crossed a period boundary, otherwise <c>null</c>.
        /// </summary>
        public IReadOnlyList<ClosedCandle>? Apply(
            string symbol, Timeframe tf, DateTime periodStart, decimal price,
            DateTime tickTimestamp, ILogger logger, TradingMetrics metrics)
        {
            lock (_lock)
            {
                var tickUtc = tickTimestamp.Kind == DateTimeKind.Utc
                    ? tickTimestamp
                    : tickTimestamp.ToUniversalTime();
                _lastUpdatedUtc = tickUtc;

                if (_current is null)
                {
                    _current = new BuildingCandle(periodStart, price);
                    logger.LogDebug(
                        "Initialised new candle slot for {Symbol}/{Tf} at period {Period:O} (first tick)",
                        symbol, tf, periodStart);
                    return null;
                }

                // Out-of-order tick from a past period — drop it
                if (periodStart < _current.PeriodStart)
                {
                    logger.LogDebug(
                        "Dropped out-of-order tick for {Symbol}/{Tf}: tick period {TickPeriod:O} < current {CurrentPeriod:O}",
                        symbol, tf, periodStart, _current.PeriodStart);
                    metrics.CandleTicksDropped.Add(1);
                    return null;
                }

                // Same period — update OHLCV
                if (periodStart == _current.PeriodStart)
                {
                    _current.Update(price);
                    return null;
                }

                // New period — close previous candle, fill gaps, and start fresh
                var results = new List<ClosedCandle>();

                // 1. Close the real candle
                results.Add(new ClosedCandle(
                    symbol, tf,
                    _current.Open, _current.High, _current.Low, _current.Close,
                    _current.TickCount, _current.PeriodStart));

                // 2. Emit synthetic candles for any skipped periods
                var lastClose = _current.Close;
                var gapStart = GetNextPeriodStart(_current.PeriodStart, tf);
                var gapCount = 0;

                while (gapStart < periodStart && gapCount < MaxGapFill)
                {
                    results.Add(new ClosedCandle(
                        symbol, tf,
                        lastClose, lastClose, lastClose, lastClose,
                        0, gapStart, IsSynthetic: true));
                    gapStart = GetNextPeriodStart(gapStart, tf);
                    gapCount++;
                }

                if (gapCount > 0)
                    logger.LogDebug(
                        "Emitted {GapCount} synthetic candles for {Symbol}/{Tf}", gapCount, symbol, tf);

                if (gapCount >= MaxGapFill)
                    logger.LogWarning(
                        "Gap fill capped at {Max} for {Symbol}/{Tf} — some periods may be missing",
                        MaxGapFill, symbol, tf);

                // 3. Start the new candle
                _current = new BuildingCandle(periodStart, price);
                return results;
            }
        }
    }

    private sealed class BuildingCandle
    {
        public DateTime PeriodStart { get; }
        public decimal Open  { get; }
        public decimal High  { get; private set; }
        public decimal Low   { get; private set; }
        public decimal Close { get; private set; }
        public long TickCount { get; private set; }

        public BuildingCandle(DateTime periodStart, decimal firstPrice)
        {
            PeriodStart = periodStart;
            Open = firstPrice;
            High = firstPrice;
            Low  = firstPrice;
            Close = firstPrice;
            TickCount = 1;
        }

        public void Update(decimal price)
        {
            if (price > High) High = price;
            if (price < Low) Low = price;
            Close = price;
            TickCount++;
        }
    }
}
