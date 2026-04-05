using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Validates incoming market data (ticks, candles) for anomalies: price spikes,
/// stale quotes, inverted spreads, volume anomalies, and timestamp regression.
/// Anomalous data is quarantined and last-known-good prices are used.
/// </summary>
public record AnomalyCheckResult(
    bool IsAnomalous,
    MarketDataAnomalyType? AnomalyType,
    string? Description,
    decimal? LastGoodBid,
    decimal? LastGoodAsk);

public record CandleQualityResult(
    bool IsValid,
    MarketDataAnomalyType? AnomalyType,
    string? Description);

public interface IMarketDataAnomalyDetector
{
    /// <summary>Validates an incoming tick for anomalies (price spike, stale quote, inverted spread).</summary>
    Task<AnomalyCheckResult> ValidateTickAsync(
        string symbol,
        decimal bid,
        decimal ask,
        DateTime timestamp,
        string instanceId,
        CancellationToken cancellationToken);

    /// <summary>Validates candle OHLCV data for structural anomalies (e.g. high &lt; low, zero volume).</summary>
    CandleQualityResult ValidateCandle(
        decimal open, decimal high, decimal low, decimal close,
        long volume, DateTime timestamp, DateTime? previousClose);
}
