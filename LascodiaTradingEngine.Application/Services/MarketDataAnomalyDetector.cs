using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Validates incoming market data for anomalies. Maintains per-symbol state to detect
/// stale quotes, price spikes, and timestamp regressions. Quarantines anomalous data
/// and returns last-known-good prices.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class MarketDataAnomalyDetector : IMarketDataAnomalyDetector
{
    private readonly AnomalyDetectionOptions _options;
    private readonly ILogger<MarketDataAnomalyDetector> _logger;

    /// <summary>Per-symbol tracking state for anomaly detection.</summary>
    private readonly ConcurrentDictionary<string, SymbolState> _symbolState = new();

    private class SymbolState
    {
        public readonly object SyncRoot = new();
        public decimal LastGoodBid { get; set; }
        public decimal LastGoodAsk { get; set; }
        public DateTime LastTickTimestamp { get; set; }
        public decimal RunningAtr { get; set; }
        public decimal RecentAvgVolume { get; set; }
    }

    public MarketDataAnomalyDetector(
        AnomalyDetectionOptions options,
        ILogger<MarketDataAnomalyDetector> logger)
    {
        _options = options;
        _logger  = logger;
    }

    public Task<AnomalyCheckResult> ValidateTickAsync(
        string symbol,
        decimal bid,
        decimal ask,
        DateTime timestamp,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var state = _symbolState.GetOrAdd(symbol, _ => new SymbolState
        {
            LastGoodBid = bid,
            LastGoodAsk = ask,
            LastTickTimestamp = timestamp,
            RunningAtr = 0
        });

        // Lock per-symbol to prevent torn reads/writes on state fields.
        // Tick processing is fast (~µs) so contention is negligible.
        lock (state.SyncRoot)
        {

        // Check 1: Inverted spread
        if (bid > ask)
        {
            _logger.LogWarning(
                "Anomaly: inverted spread for {Symbol} from {Instance} — bid={Bid} > ask={Ask}",
                symbol, instanceId, bid, ask);

            return Task.FromResult(new AnomalyCheckResult(
                true, MarketDataAnomalyType.InvertedSpread,
                $"Bid {bid} > Ask {ask}",
                state.LastGoodBid, state.LastGoodAsk));
        }

        // Check 2: Timestamp regression
        if (timestamp < state.LastTickTimestamp)
        {
            _logger.LogWarning(
                "Anomaly: timestamp regression for {Symbol} from {Instance} — {New} < {Old}",
                symbol, instanceId, timestamp, state.LastTickTimestamp);

            return Task.FromResult(new AnomalyCheckResult(
                true, MarketDataAnomalyType.TimestampRegression,
                $"Timestamp {timestamp:O} < previous {state.LastTickTimestamp:O}",
                state.LastGoodBid, state.LastGoodAsk));
        }

        // Check 3: Stale quote
        if (bid == state.LastGoodBid && ask == state.LastGoodAsk &&
            (timestamp - state.LastTickTimestamp).TotalSeconds > _options.StaleQuoteMaxSeconds)
        {
            _logger.LogWarning(
                "Anomaly: stale quote for {Symbol} from {Instance} — unchanged for {Seconds:F0}s",
                symbol, instanceId, (timestamp - state.LastTickTimestamp).TotalSeconds);

            return Task.FromResult(new AnomalyCheckResult(
                true, MarketDataAnomalyType.StaleQuote,
                $"Quote unchanged for {(timestamp - state.LastTickTimestamp).TotalSeconds:F0}s",
                state.LastGoodBid, state.LastGoodAsk));
        }

        // Check 4: Price spike (relative to running ATR)
        if (state.RunningAtr > 0)
        {
            var midDelta = Math.Abs((bid + ask) / 2m - (state.LastGoodBid + state.LastGoodAsk) / 2m);
            if (midDelta > state.RunningAtr * _options.PriceSpikeAtrMultiple)
            {
                _logger.LogWarning(
                    "Anomaly: price spike for {Symbol} from {Instance} — delta={Delta:F5}, ATR={ATR:F5}, threshold={Thresh}x",
                    symbol, instanceId, midDelta, state.RunningAtr, _options.PriceSpikeAtrMultiple);

                return Task.FromResult(new AnomalyCheckResult(
                    true, MarketDataAnomalyType.PriceSpike,
                    $"Mid-price delta {midDelta:F5} exceeds {_options.PriceSpikeAtrMultiple}x ATR ({state.RunningAtr:F5})",
                    state.LastGoodBid, state.LastGoodAsk));
            }
        }

        // Update state — this tick is valid
        var atrAlpha = 2.0m / 15.0m; // EMA smoothing factor ~14 period
        var tickRange = Math.Abs(bid - state.LastGoodBid);
        state.RunningAtr = state.RunningAtr == 0
            ? tickRange
            : atrAlpha * tickRange + (1m - atrAlpha) * state.RunningAtr;

        state.LastGoodBid      = bid;
        state.LastGoodAsk      = ask;
        state.LastTickTimestamp = timestamp;

        return Task.FromResult(new AnomalyCheckResult(false, null, null, null, null));

        } // end lock(state.SyncRoot)
    }

    public CandleQualityResult ValidateCandle(
        decimal open, decimal high, decimal low, decimal close,
        long volume, DateTime timestamp, DateTime? previousClose)
    {
        // OHLC consistency: High must be >= max(Open, Close) and Low must be <= min(Open, Close)
        if (high < Math.Max(open, close))
        {
            return new CandleQualityResult(false, MarketDataAnomalyType.InvalidOhlc,
                $"High ({high}) < max(Open={open}, Close={close})");
        }

        if (low > Math.Min(open, close))
        {
            return new CandleQualityResult(false, MarketDataAnomalyType.InvalidOhlc,
                $"Low ({low}) > min(Open={open}, Close={close})");
        }

        // High must be >= Low
        if (high < low)
        {
            return new CandleQualityResult(false, MarketDataAnomalyType.InvalidOhlc,
                $"High ({high}) < Low ({low})");
        }

        // Volume must be non-negative
        if (volume < 0)
        {
            return new CandleQualityResult(false, MarketDataAnomalyType.VolumeAnomaly,
                $"Negative volume ({volume})");
        }

        // Prices must be positive
        if (open <= 0 || high <= 0 || low <= 0 || close <= 0)
        {
            return new CandleQualityResult(false, MarketDataAnomalyType.InvalidOhlc,
                $"Non-positive price: O={open}, H={high}, L={low}, C={close}");
        }

        // Magnitude guard: the Candle price columns are numeric(18,8), which caps the
        // integer portion at ~10 billion. A bogus EA payload with a very large price
        // (broker connectivity error, EA sending a float-infinity cast, instrument
        // mis-scale) passes all the consistency checks above but overflows the column
        // at INSERT time with PostgreSQL 22003. Quarantine such candles here so the
        // audit trail captures the root cause instead of a generic DbUpdateException.
        const decimal MaxPriceMagnitude = 1_000_000_000m; // 1e9, well above any real instrument
        if (open  > MaxPriceMagnitude || high > MaxPriceMagnitude ||
            low   > MaxPriceMagnitude || close > MaxPriceMagnitude)
        {
            return new CandleQualityResult(false, MarketDataAnomalyType.InvalidOhlc,
                $"Price magnitude exceeds column precision: O={open}, H={high}, L={low}, C={close} (max {MaxPriceMagnitude:F0})");
        }

        return new CandleQualityResult(true, null, null);
    }
}
