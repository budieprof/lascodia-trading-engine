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
    /// <summary>Persists a single feature vector to the store.</summary>
    Task PersistAsync(
        StoredFeatureVector vector,
        CancellationToken cancellationToken);

    /// <summary>Persists a batch of feature vectors to the store.</summary>
    Task PersistBatchAsync(
        IReadOnlyList<StoredFeatureVector> vectors,
        CancellationToken cancellationToken);

    /// <summary>Retrieves a single feature vector by symbol, timeframe, and bar timestamp.</summary>
    Task<StoredFeatureVector?> GetAsync(
        string symbol,
        Timeframe timeframe,
        DateTime barTimestamp,
        CancellationToken cancellationToken);

    /// <summary>Retrieves all feature vectors for a symbol/timeframe within the given date range.</summary>
    Task<IReadOnlyList<StoredFeatureVector>> GetRangeAsync(
        string symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken);

    /// <summary>Current feature schema version. Incremented when feature definitions change.</summary>
    int CurrentSchemaVersion { get; }
}
