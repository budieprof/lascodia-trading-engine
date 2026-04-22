namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Canonical ML model lifecycle transition types persisted as strings for audit readability.
/// </summary>
public enum MLModelLifecycleEventType
{
    DegradationRetirement,
    AbTestPromotion,
    AbTestDemotion,
    AbTestRejection,
}
