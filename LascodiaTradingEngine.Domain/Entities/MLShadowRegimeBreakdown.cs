using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persists the per-market-regime accuracy breakdown captured during a shadow evaluation.
///
/// <b>Motivation:</b> Aggregate shadow evaluation metrics (overall champion vs challenger accuracy)
/// can mask regime-specific weaknesses. A challenger may beat the champion globally while
/// significantly underperforming in trending markets, or the opposite. By persisting one row
/// per (ShadowEvaluationId, Regime) when the arbiter reaches a decision, analysts and downstream
/// workers can inspect whether a newly promoted model degrades in specific market conditions.
/// </summary>
public class MLShadowRegimeBreakdown : Entity<long>
{
    /// <summary>Foreign key to the completed <see cref="MLShadowEvaluation"/>.</summary>
    public long   ShadowEvaluationId              { get; set; }

    /// <summary>The market regime this row describes (e.g. Trending, Ranging, Volatile).</summary>
    public MarketRegime Regime                    { get; set; }

    /// <summary>Number of predictions evaluated in this regime during the shadow evaluation.</summary>
    public int    TotalPredictions                { get; set; }

    /// <summary>Champion model direction accuracy within this regime (0.0–1.0).</summary>
    public decimal ChampionAccuracy               { get; set; }

    /// <summary>Challenger model direction accuracy within this regime (0.0–1.0).</summary>
    public decimal ChallengerAccuracy             { get; set; }

    /// <summary>
    /// Difference: <c>ChallengerAccuracy − ChampionAccuracy</c>.
    /// Positive means challenger wins this regime; negative means champion holds.
    /// </summary>
    public decimal AccuracyDelta                  { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool   IsDeleted                       { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>The shadow evaluation this breakdown belongs to.</summary>
    public virtual MLShadowEvaluation ShadowEvaluation { get; set; } = null!;
}
