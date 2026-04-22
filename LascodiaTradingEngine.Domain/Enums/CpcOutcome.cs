namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Stable high-level outcome of a single CPC pretraining attempt. The wire-level string
/// encoding (via <see cref="CpcOutcomeExtensions.ToWire"/>) is what ends up in
/// <c>MLCpcEncoderTrainingLog.Outcome</c>, so operational dashboards keep working across
/// code changes.
/// </summary>
public enum CpcOutcome
{
    Promoted = 0,
    Rejected = 1,
    Skipped  = 2
}
