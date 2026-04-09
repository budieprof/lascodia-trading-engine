namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Captures why a queued optimization run is intentionally deferred instead of being
/// immediately claimable by the worker.
/// </summary>
public enum OptimizationDeferralReason
{
    Unknown = 0,
    SeasonalBlackout = 1,
    DrawdownRecovery = 2,
    RegimeTransition = 3,
    EADataUnavailable = 4,
    DataQuality = 5,
}
