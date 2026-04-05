namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Categorises why an optimization run failed, enabling targeted retry strategies.
/// Timeout failures get extended timeouts on retry; data quality issues wait longer
/// for new candles; config errors are not retried at all.
/// </summary>
public enum OptimizationFailureCategory
{
    /// <summary>Uncategorised or legacy failure.</summary>
    Unknown = 0,

    /// <summary>Run exceeded the aggregate timeout. Retry with extended timeout.</summary>
    Timeout = 1,

    /// <summary>Candle data quality issue (gaps, insufficient bars). Wait for new data before retry.</summary>
    DataQuality = 2,

    /// <summary>Invalid configuration. Do not retry — requires operator fix.</summary>
    ConfigError = 3,

    /// <summary>Transient infrastructure error (DB timeout, OOM). Retry with standard backoff.</summary>
    Transient = 4,

    /// <summary>Strategy was deleted or deactivated during the run.</summary>
    StrategyRemoved = 5,

    /// <summary>All parameter candidates failed during search (evaluator bug, systematic issue).</summary>
    SearchExhausted = 6,
}
