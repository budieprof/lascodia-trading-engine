namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Records the outcome of an ML shadow evaluation promotion decision.
/// </summary>
public enum PromotionDecision
{
    /// <summary>Model met all criteria and was automatically promoted to production.</summary>
    AutoPromoted = 0,

    /// <summary>Model showed borderline results and requires manual review.</summary>
    FlaggedForReview = 1,

    /// <summary>Model failed evaluation criteria and was rejected.</summary>
    Rejected = 2
}
