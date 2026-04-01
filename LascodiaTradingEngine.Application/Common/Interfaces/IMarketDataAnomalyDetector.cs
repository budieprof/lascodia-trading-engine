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
    Task<AnomalyCheckResult> ValidateTickAsync(
        string symbol,
        decimal bid,
        decimal ask,
        DateTime timestamp,
        string instanceId,
        CancellationToken cancellationToken);

    CandleQualityResult ValidateCandle(
        decimal open, decimal high, decimal low, decimal close,
        long volume, DateTime timestamp, DateTime? previousClose);
}
