using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Evaluates whether a new trade signal arrives during a self-exciting burst episode
/// modelled by the Hawkes process, and suppresses it if intensity is elevated (Rec #32).
/// </summary>
public interface IHawkesSignalFilter
{
    /// <summary>
    /// Returns <c>true</c> when the current Hawkes intensity λ(t) for the given
    /// symbol/timeframe exceeds the suppression threshold, indicating a burst episode.
    /// When <c>true</c>, the caller should suppress the signal to avoid over-trading.
    /// </summary>
    /// <param name="symbol">Currency pair (e.g. "EURUSD").</param>
    /// <param name="timeframe">Chart timeframe.</param>
    /// <param name="recentSignalTimestamps">UTC timestamps of the most recent signals.</param>
    Task<bool> IsBurstEpisodeAsync(
        string            symbol,
        Timeframe         timeframe,
        IReadOnlyList<DateTime> recentSignalTimestamps,
        CancellationToken ct = default);
}
