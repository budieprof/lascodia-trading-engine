namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Stable machine-readable reason codes emitted alongside <see cref="CpcOutcome"/> in
/// <c>MLCpcEncoderTrainingLog.Reason</c>. Every decision point in the CPC pretraining
/// pipeline maps to exactly one value here; add new cases rather than overloading strings.
/// The wire encoding (<see cref="CpcOutcomeExtensions.ToWire(CpcReason)"/>) is stable even
/// as enum member names evolve.
/// </summary>
public enum CpcReason
{
    // Terminal success.
    Accepted = 0,

    // Data-availability skips / rejections.
    InsufficientCandles            = 10,
    NoSequences                    = 11,
    InsufficientValidationSequences = 12,

    // Registration / config mismatches.
    PretrainerMissing       = 20,
    EmbeddingDimMismatch    = 21,

    // Trainer-level failures.
    TrainerException    = 30,
    TrainerReturnedNull = 31,
    EmptyWeights        = 32,
    LossOutOfBounds     = 33,

    // Projection / contrastive gates.
    ProjectionInvalid          = 40,
    ValidationLossOutOfBounds  = 41,
    EmbeddingCollapsed         = 42,
    NoImprovement              = 43,
    GateEvaluationException    = 44,

    // Downstream probe gates.
    DownstreamProbeInsufficientSamples       = 50,
    DownstreamProbeInsufficientLabels        = 51,
    DownstreamProbeBelowFloor                = 52,
    DownstreamProbePassed                    = 53,
    DownstreamProbePassedPriorUnavailable    = 54,
    DownstreamProbePassedPriorUnevaluable    = 55,
    DownstreamProbeNoLift                    = 56,
    DownstreamProbeDisabled                  = 57,

    // Representation-drift / novelty gates (added to reach gate comprehensiveness).
    RepresentationDriftInsufficient = 60,
    RepresentationDriftExcessive    = 61,

    // Anti-forgetting gate (cross-architecture switch).
    ArchitectureSwitchRegression = 70,

    // Adversarial-validation gate.
    AdversarialValidationFailed = 80,

    // Lifecycle / race outcomes.
    LockBusy                 = 90,
    PromotionConflict        = 91,
    WorkerException          = 92,
    SupersededByBetterActive = 93
}

/// <summary>
/// Extension methods mapping <see cref="CpcOutcome"/> / <see cref="CpcReason"/> to their
/// persisted string form and back. Kept explicit so future enum reorderings don't break
/// the wire schema.
/// </summary>
public static class CpcOutcomeExtensions
{
    public static string ToWire(this CpcOutcome outcome) => outcome switch
    {
        CpcOutcome.Promoted => "promoted",
        CpcOutcome.Rejected => "rejected",
        CpcOutcome.Skipped  => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    public static string ToWire(this CpcReason reason) => reason switch
    {
        CpcReason.Accepted                                 => "accepted",
        CpcReason.InsufficientCandles                      => "insufficient_candles",
        CpcReason.NoSequences                              => "no_sequences",
        CpcReason.InsufficientValidationSequences          => "insufficient_validation_sequences",
        CpcReason.PretrainerMissing                        => "pretrainer_missing",
        CpcReason.EmbeddingDimMismatch                     => "embedding_dim_mismatch",
        CpcReason.TrainerException                         => "trainer_exception",
        CpcReason.TrainerReturnedNull                      => "trainer_returned_null",
        CpcReason.EmptyWeights                             => "empty_weights",
        CpcReason.LossOutOfBounds                          => "loss_out_of_bounds",
        CpcReason.ProjectionInvalid                        => "projection_invalid",
        CpcReason.ValidationLossOutOfBounds                => "validation_loss_out_of_bounds",
        CpcReason.EmbeddingCollapsed                       => "embedding_collapsed",
        CpcReason.NoImprovement                            => "no_improvement",
        CpcReason.GateEvaluationException                  => "gate_evaluation_exception",
        CpcReason.DownstreamProbeInsufficientSamples       => "downstream_probe_insufficient_samples",
        CpcReason.DownstreamProbeInsufficientLabels        => "downstream_probe_insufficient_labels",
        CpcReason.DownstreamProbeBelowFloor                => "downstream_probe_below_floor",
        CpcReason.DownstreamProbePassed                    => "downstream_probe_passed",
        CpcReason.DownstreamProbePassedPriorUnavailable    => "downstream_probe_passed_prior_unavailable",
        CpcReason.DownstreamProbePassedPriorUnevaluable    => "downstream_probe_passed_prior_unevaluable",
        CpcReason.DownstreamProbeNoLift                    => "downstream_probe_no_lift",
        CpcReason.DownstreamProbeDisabled                  => "downstream_probe_disabled",
        CpcReason.RepresentationDriftInsufficient          => "representation_drift_insufficient",
        CpcReason.RepresentationDriftExcessive             => "representation_drift_excessive",
        CpcReason.ArchitectureSwitchRegression             => "architecture_switch_regression",
        CpcReason.AdversarialValidationFailed              => "adversarial_validation_failed",
        CpcReason.LockBusy                                 => "lock_busy",
        CpcReason.PromotionConflict                        => "promotion_conflict",
        CpcReason.WorkerException                          => "worker_exception",
        CpcReason.SupersededByBetterActive                 => "superseded_by_better_active",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}
