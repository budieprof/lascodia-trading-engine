using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records the top-K pairwise feature interaction scores detected by
/// <c>MLFeatureInteractionWorker</c> (Rec #34).
/// </summary>
/// <remarks>
/// The production worker scores each pair with a partial F-test: it compares a reduced
/// model using each feature's individual contribution against a full model that also
/// includes their product term. Pairs that pass the configured evidence thresholds can
/// be appended as replayable product features in subsequent training runs.
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

    /// <summary>Feature schema version of the model snapshot that produced this audit.</summary>
    public int       FeatureSchemaVersion { get; set; }

    /// <summary>Base feature count before any interaction features are appended.</summary>
    public int       BaseFeatureCount     { get; set; }

    /// <summary>Number of finite, schema-compatible prediction rows used for the test.</summary>
    public int       SampleCount          { get; set; }

    /// <summary>Statistical method used to compute the score.</summary>
    public string    Method               { get; set; } = string.Empty;

    /// <summary>
    /// Partial F-ratio measuring the incremental value of the product term after
    /// controlling for the two individual feature contributions.
    /// </summary>
    public double    InteractionScore     { get; set; }

    /// <summary>Incremental R² gained by adding the product term.</summary>
    public double    EffectSize           { get; set; }

    /// <summary>Approximate p-value for the interaction term.</summary>
    public double    PValue               { get; set; } = 1.0;

    /// <summary>Benjamini-Hochberg false-discovery-adjusted p-value across tested pairs.</summary>
    public double    QValue               { get; set; } = 1.0;

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
