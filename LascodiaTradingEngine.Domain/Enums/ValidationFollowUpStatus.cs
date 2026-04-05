namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Tracks the aggregate outcome of post-approval validation follow-ups
/// (backtest + walk-forward) for an optimization run.
/// </summary>
public enum ValidationFollowUpStatus
{
    /// <summary>Validation follow-ups have been queued but not yet completed.</summary>
    Pending = 0,

    /// <summary>All validation follow-ups completed successfully.</summary>
    Passed = 1,

    /// <summary>One or more validation follow-ups failed or produced poor results.</summary>
    Failed = 2,
}
