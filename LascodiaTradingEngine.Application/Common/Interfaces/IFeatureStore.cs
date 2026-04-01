using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Pre-computes and persists feature vectors per (symbol, timeframe, bar) after each candle close.
/// Both training and scoring read from the store to guarantee training/serving parity.
/// Stores feature schema version to detect schema drift.
/// </summary>
public record StoredFeatureVector(
    long CandleId,
    string Symbol,
    Timeframe Timeframe,
    DateTime BarTimestamp,
    double[] Features,
    int SchemaVersion,
    string[] FeatureNames);

public interface IFeatureStore
{
    Task PersistAsync(
        StoredFeatureVector vector,
        CancellationToken cancellationToken);

    Task PersistBatchAsync(
        IReadOnlyList<StoredFeatureVector> vectors,
        CancellationToken cancellationToken);

    Task<StoredFeatureVector?> GetAsync(
        string symbol,
        Timeframe timeframe,
        DateTime barTimestamp,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredFeatureVector>> GetRangeAsync(
        string symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken);

    int CurrentSchemaVersion { get; }
}
