using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Maps a detected market regime to the strategy types that are historically effective
/// in that environment. Used by <c>StrategyGenerationWorker</c> to generate
/// regime-appropriate candidates rather than exhaustive combinations.
///
/// The mapping starts from a static baseline and can be augmented by performance feedback
/// via <see cref="RefreshFromFeedback"/>. Types that survive well in a regime they weren't
/// statically mapped to can be promoted into that regime's candidate pool.
/// </summary>
public interface IRegimeStrategyMapper
{
    /// <summary>
    /// Returns the strategy types suitable for the given market regime.
    /// Includes both statically-mapped types and any feedback-promoted types.
    /// Returns an empty list for regimes where generation should be suppressed (e.g. Crisis).
    /// </summary>
    IReadOnlyList<StrategyType> GetStrategyTypes(MarketRegimeEnum regime);

    /// <summary>
    /// Refreshes the mapping with performance feedback data. Strategy types with survival
    /// rates above <paramref name="promotionThreshold"/> in a regime they aren't statically
    /// mapped to will be added to that regime's candidate pool.
    /// </summary>
    void RefreshFromFeedback(
        IReadOnlyDictionary<(StrategyType, MarketRegimeEnum), double> feedbackRates,
        double promotionThreshold = 0.65);
}
