using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores the trained stacking ensemble meta-learner that combines out-of-fold
/// probability outputs from multiple base models (Rec #25).
/// </summary>
/// <remarks>
/// <c>MLStackingMetaLearnerWorker</c> collects out-of-fold Buy probabilities from
/// all active models for a symbol/timeframe (e.g. TCN + BaggedLogistic), trains a
/// logistic regression meta-learner on those predictions, and persists the result here.
/// At inference time <c>MLSignalScorer</c> may use the meta-learner to blend the
/// individual model outputs rather than a simple average.
/// </remarks>
public class MLStackingMetaModel : Entity<long>
{
    /// <summary>The currency pair (e.g. "EURUSD").</summary>
    public string   Symbol              { get; set; } = string.Empty;

    /// <summary>The chart timeframe.</summary>
    public Timeframe Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>
    /// JSON-serialised long[] of the base <see cref="MLModel"/> IDs whose out-of-fold
    /// predictions were used to train this meta-learner.
    /// </summary>
    public string   BaseModelIdsJson    { get; set; } = "[]";

    /// <summary>
    /// Number of base models (inputs to the meta-learner).
    /// Matches the length of <see cref="BaseModelIdsJson"/>.
    /// </summary>
    public int      BaseModelCount      { get; set; }

    /// <summary>
    /// JSON-serialised double[] of logistic regression weights — one per base model.
    /// The meta-learner predicts P(Buy) = sigmoid(w · p_base + b).
    /// </summary>
    public string   MetaWeightsJson     { get; set; } = "[]";

    /// <summary>Intercept (bias) term of the logistic regression meta-learner.</summary>
    public double   MetaBias            { get; set; }

    /// <summary>Direction accuracy of the meta-learner on the held-out evaluation set.</summary>
    public decimal? DirectionAccuracy   { get; set; }

    /// <summary>Brier score of the meta-learner probability output.</summary>
    public decimal? BrierScore          { get; set; }

    /// <summary>
    /// <c>true</c> when this meta-learner is the current active stacker for the symbol/timeframe.
    /// Only one row per symbol/timeframe should be active at a time.
    /// </summary>
    public bool     IsActive            { get; set; }

    /// <summary>Number of out-of-fold samples used to train the meta-learner.</summary>
    public int      TrainingSamples     { get; set; }

    /// <summary>UTC timestamp when this meta-learner was trained.</summary>
    public DateTime TrainedAt           { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag.</summary>
    public bool     IsDeleted           { get; set; }
}
