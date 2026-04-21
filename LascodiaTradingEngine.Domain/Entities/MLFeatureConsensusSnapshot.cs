using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores a point-in-time consensus of feature importance across all active models
/// for a given symbol and timeframe. Produced by <c>MLFeatureConsensusWorker</c>.
/// </summary>
/// <remarks>
/// Cross-architecture feature importance consensus identifies features that are
/// universally important (high mean importance, low std across architectures)
/// vs architecture-specific artifacts (high std). This information feeds into:
/// <list type="bullet">
///   <item><c>TrainerSelector</c>: boost architectures whose top features align with consensus.</item>
///   <item><c>MLCovariateShiftWorker</c>: skip retraining if drift is in low-consensus features.</item>
///   <item><c>MLTrainingWorker</c>: optionally initialise feature masks from consensus.</item>
/// </list>
/// </remarks>
public class MLFeatureConsensusSnapshot : Entity<long>
{
    /// <summary>The currency pair this consensus covers (e.g. "EURUSD").</summary>
    public string   Symbol              { get; set; } = string.Empty;

    /// <summary>The chart timeframe for which the consensus was computed.</summary>
    public Timeframe Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>
    /// JSON array of per-feature consensus data. Each element contains:
    /// <c>{"Feature":"Rsi","MeanImportance":0.12,"StdImportance":0.03,"AgreementScore":0.89}</c>.
    /// AgreementScore = 1 − (StdImportance / MeanImportance), clamped to [0, 1].
    /// </summary>
    public string   FeatureConsensusJson { get; set; } = "[]";

    /// <summary>
    /// Compatibility key for the feature schema represented by this snapshot. Includes the model
    /// feature-schema fingerprint when available and the actual importance feature-name set.
    /// </summary>
    public string   SchemaKey            { get; set; } = string.Empty;

    /// <summary>Number of distinct named features represented in <see cref="FeatureConsensusJson"/>.</summary>
    public int      FeatureCount         { get; set; }

    /// <summary>
    /// JSON object summarising how many contributors used each importance source
    /// (for example, TCN channel scores, calibrated permutation scores, raw importance, or weights).
    /// </summary>
    public string   ImportanceSourceSummaryJson { get; set; } = "{}";

    /// <summary>JSON array of MLModel IDs that contributed to this exact consensus snapshot.</summary>
    public string   ContributorModelIdsJson { get; set; } = "[]";

    /// <summary>Number of active models that contributed to this consensus.</summary>
    public int      ContributingModelCount { get; set; }

    /// <summary>
    /// Average pairwise Kendall's tau rank correlation across all contributing models'
    /// feature importance rankings. Range [−1, 1], higher means stronger agreement.
    /// </summary>
    public double   MeanKendallTau      { get; set; }

    /// <summary>UTC timestamp when this consensus snapshot was computed.</summary>
    public DateTime DetectedAt          { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool     IsDeleted           { get; set; }
}
