using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Builds CPC training sequences from closed candles while preserving market-time continuity.
/// </summary>
public interface ICpcSequencePreparationService
{
    /// <summary>
    /// Converts ordered candles into CPC windows, splitting at timestamp gaps so no sequence
    /// crosses a missing bar or a separate regime episode.
    /// </summary>
    IReadOnlyList<float[][]> BuildSequences(
        IReadOnlyList<Candle> candles,
        int sequenceLength,
        int sequenceStride,
        int maxSequences);
}
