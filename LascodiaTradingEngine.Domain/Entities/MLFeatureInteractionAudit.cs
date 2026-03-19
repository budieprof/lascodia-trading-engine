using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records the top-K pairwise feature interaction scores detected by
/// <c>MLFeatureInteractionWorker</c> using random-forest H-statistic or ANOVA (Rec #34).
/// </summary>
/// <remarks>
/// For each pair of features (i, j), the H-statistic measures what fraction of the
/// variance in the model's predictions is attributable to the interaction term x_i × x_j
/// (vs the individual main effects).  Pairs with high H-statistics are added as product
/// features in subsequent training runs.
/// </remarks>
public class MLFeatureInteractionAudit : Entity<long>
{
    /// <summary>FK to the <see cref="MLModel"/> whose predictions were analysed.</summary>
    public long      MLModelId            { get; set; }

    /// <summary>The currency pair (e.g. "EURUSD").</summary>
    public string    Symbol               { get; set; } = string.Empty;

    /// <summary>The chart timeframe.</summary>
    public Timeframe Timeframe            { get; set; } = Timeframe.H1;

    /// <summary>Zero-based index of the first feature in the interaction.</summary>
    public int       FeatureIndexA        { get; set; }

    /// <summary>Human-readable name of the first feature.</summary>
    public string    FeatureNameA         { get; set; } = string.Empty;

    /// <summary>Zero-based index of the second feature.</summary>
    public int       FeatureIndexB        { get; set; }

    /// <summary>Human-readable name of the second feature.</summary>
    public string    FeatureNameB         { get; set; } = string.Empty;

    /// <summary>
    /// H-statistic (Friedman) or ANOVA F-ratio measuring the interaction strength.
    /// Higher values indicate a stronger interaction requiring a product feature.
    /// </summary>
    public double    InteractionScore     { get; set; }

    /// <summary>Rank of this pair among all pairs tested (1 = strongest).</summary>
    public int       Rank                 { get; set; }

    /// <summary>
    /// <c>true</c> when the product feature x_i × x_j has been added to the feature
    /// vector and included in subsequent training runs.
    /// </summary>
    public bool      IsIncludedAsFeature  { get; set; }

    /// <summary>UTC timestamp when this audit was run.</summary>
    public DateTime  ComputedAt           { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag.</summary>
    public bool      IsDeleted            { get; set; }

    public virtual MLModel MLModel        { get; set; } = null!;
}
