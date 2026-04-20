namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Reason the conformal coverage circuit breaker suppressed an ML model.</summary>
public enum MLConformalBreakerTripReason
{
    /// <summary>Breaker has not recorded a trip reason.</summary>
    Unknown = 0,

    /// <summary>A long contiguous run of uncovered outcomes tripped the breaker.</summary>
    ConsecutiveUncovered = 1,

    /// <summary>Empirical coverage was statistically below the target coverage floor.</summary>
    SustainedLowCoverage = 2,

    /// <summary>Both consecutive-uncovered and sustained-low-coverage rules tripped.</summary>
    Both = 3
}
