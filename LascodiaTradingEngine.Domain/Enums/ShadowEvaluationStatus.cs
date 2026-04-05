namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Tracks the lifecycle of an ML model shadow evaluation against live data.
/// </summary>
public enum ShadowEvaluationStatus
{
    /// <summary>Evaluation is actively collecting live predictions.</summary>
    Running = 0,

    /// <summary>Evaluation period has ended and metrics are finalised.</summary>
    Completed = 1,

    /// <summary>Model passed evaluation and was promoted to production.</summary>
    Promoted = 2,

    /// <summary>Model failed evaluation criteria and was rejected.</summary>
    Rejected = 3,

    /// <summary>Evaluation results are being processed by the arbiter.</summary>
    Processing = 4
}
