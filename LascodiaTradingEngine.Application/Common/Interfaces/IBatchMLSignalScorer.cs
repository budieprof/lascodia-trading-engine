using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Scores multiple trade signals in a single batch pass, reducing per-signal overhead
/// when multiple signals arrive simultaneously (e.g., from EA tick batch).
/// Falls back to sequential scoring when batch size is 1.
/// </summary>
public interface IBatchMLSignalScorer
{
    /// <summary>
    /// Scores a batch of candidate signals, sharing feature computation across signals
    /// that target the same symbol/timeframe.
    /// </summary>
    Task<IReadOnlyList<MLScoreResult?>> ScoreBatchAsync(
        IReadOnlyList<BatchScoringRequest> requests,
        CancellationToken ct = default);
}

/// <summary>
/// A single scoring request within a batch.
/// </summary>
public record BatchScoringRequest(
    TradeSignal Signal,
    IReadOnlyList<Candle> Candles);
