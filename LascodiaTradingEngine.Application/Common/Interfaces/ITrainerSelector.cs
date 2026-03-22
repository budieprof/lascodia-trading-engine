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
///         supplied (and not stale), the selector blends static priors with empirical
///         regime affinity computed from historical runs.</item>
///   <item><b>Historical performance:</b> the architecture with the highest UCB1 score
///         (recency-weighted composite + exploration bonus) across recent completed runs
///         for the same symbol/timeframe pair. When no history exists, cross-symbol
///         borrowing from correlated instruments is attempted.</item>
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
    /// <param name="regimeDetectedAt">UTC timestamp when the regime was detected.
    /// Regimes older than the configured staleness threshold are ignored.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LearnerArchitecture> SelectAsync(
        string            symbol,
        Timeframe         timeframe,
        int               sampleCount,
        MarketRegimeEnum? regime,
        DateTime?         regimeDetectedAt,
        CancellationToken ct);

    /// <summary>
    /// Returns 1–2 shadow architectures from a different model family than
    /// <paramref name="primary"/>, suitable for parallel shadow evaluation.
    /// Shadow candidates are ranked by historical performance, sample-gated,
    /// regime-filtered, and never duplicate the primary.
    /// </summary>
    /// <param name="primary">The primary architecture already selected for this run.</param>
    /// <param name="symbol">Currency pair symbol — used to rank shadows by historical performance.</param>
    /// <param name="timeframe">Chart timeframe — used alongside symbol for historical lookup.</param>
    /// <param name="sampleCount">Number of training samples available.</param>
    /// <param name="regime">Current market regime — used to avoid regime-penalised shadows.</param>
    /// <param name="regimeDetectedAt">UTC timestamp when the regime was detected.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<LearnerArchitecture>> SelectShadowArchitecturesAsync(
        LearnerArchitecture primary,
        string              symbol,
        Timeframe           timeframe,
        int                 sampleCount,
        MarketRegimeEnum?   regime,
        DateTime?           regimeDetectedAt,
        CancellationToken   ct);

    /// <summary>
    /// Evicts cached training-run data for the given symbol/timeframe so the next
    /// selection picks up freshly completed runs immediately instead of waiting
    /// for the cache TTL to expire. Call after a training run completes.
    /// </summary>
    void InvalidateCache(string symbol, Timeframe timeframe);
}
