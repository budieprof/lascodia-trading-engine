using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Chooses the most appropriate <see cref="LearnerArchitecture"/> for a training run
/// without requiring manual intervention from the caller.
/// </summary>
/// <remarks>
/// Selection pipeline (first match wins):
/// <list type="number">
///   <item><b>Regime-conditional bias:</b> if a current <see cref="MarketRegimeEnum"/> is
///         supplied, the selector boosts composite scores for architectures that
///         historically perform well in that regime.</item>
///   <item><b>Historical performance:</b> the architecture with the highest recency-weighted
///         composite score across recent completed runs for the same symbol/timeframe pair.</item>
///   <item><b>Operator default:</b> the <c>MLTraining:DefaultArchitecture</c>
///         <see cref="Domain.Entities.EngineConfig"/> key if no useful history exists.</item>
///   <item><b>Regime default:</b> a built-in default architecture for the current regime
///         when no history or operator config exists.</item>
///   <item><b>Fallback:</b> <see cref="LearnerArchitecture.BaggedLogistic"/> when nothing
///         else applies.</item>
/// </list>
/// After selection, a three-tier sample-count gate may downgrade the choice.
/// </remarks>
public interface ITrainerSelector
{
    /// <summary>
    /// Returns the best <see cref="LearnerArchitecture"/> for the given context.
    /// </summary>
    /// <param name="symbol">Currency pair symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">Chart timeframe.</param>
    /// <param name="sampleCount">Number of training samples available for this run.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LearnerArchitecture> SelectAsync(
        string            symbol,
        Timeframe         timeframe,
        int               sampleCount,
        CancellationToken ct);

    /// <summary>
    /// Returns the best <see cref="LearnerArchitecture"/> for the given context,
    /// biased by the current <paramref name="regime"/>.
    /// </summary>
    /// <param name="symbol">Currency pair symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">Chart timeframe.</param>
    /// <param name="sampleCount">Number of training samples available for this run.</param>
    /// <param name="regime">Current market regime — used to bias architecture selection.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LearnerArchitecture> SelectAsync(
        string            symbol,
        Timeframe         timeframe,
        int               sampleCount,
        MarketRegimeEnum? regime,
        CancellationToken ct);

    /// <summary>
    /// Returns 1–2 shadow architectures from a different model family than
    /// <paramref name="primary"/>, suitable for parallel shadow evaluation.
    /// Shadow candidates are sample-gated and never duplicate the primary.
    /// </summary>
    /// <param name="primary">The primary architecture already selected for this run.</param>
    /// <param name="sampleCount">Number of training samples available.</param>
    /// <param name="timeframe">Chart timeframe — influences whether TCN (deep-tier) is eligible.</param>
    IReadOnlyList<LearnerArchitecture> SelectShadowArchitectures(
        LearnerArchitecture primary,
        int                 sampleCount,
        Timeframe           timeframe);
}
