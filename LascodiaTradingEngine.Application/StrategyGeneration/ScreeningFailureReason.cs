namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Structured enum for screening gate rejection reasons. Replaces ad-hoc string outcomes
/// so failure analytics can be queried without string parsing.
/// </summary>
public enum ScreeningFailureReason
{
    /// <summary>No failure — candidate passed all gates.</summary>
    None = 0,

    /// <summary>In-sample backtest produced zero trades.</summary>
    ZeroTradesIS,

    /// <summary>In-sample metrics below threshold (win rate, PF, Sharpe, drawdown, or trade count).</summary>
    IsThreshold,

    /// <summary>Out-of-sample backtest produced zero trades.</summary>
    ZeroTradesOOS,

    /// <summary>Out-of-sample metrics below relaxed thresholds.</summary>
    OosThreshold,

    /// <summary>Excessive IS-to-OOS degradation in Sharpe, PF, win rate, or drawdown.</summary>
    Degradation,

    /// <summary>Equity curve R² below minimum linearity threshold.</summary>
    EquityCurveR2,

    /// <summary>Trade entries too concentrated in specific hours of the day.</summary>
    TimeConcentration,

    /// <summary>Walk-forward mini-validation: insufficient windows passed.</summary>
    WalkForward,

    /// <summary>Monte Carlo sign-flip test: p-value too high (strategy not significantly better than random).</summary>
    MonteCarloSignFlip,

    /// <summary>Monte Carlo shuffle test: Sharpe depends on trade sequence (serial autocorrelation fragility).</summary>
    MonteCarloShuffle,

    /// <summary>IS or OOS backtest timed out.</summary>
    Timeout,

    /// <summary>Screening task threw an unexpected exception.</summary>
    TaskFault,

    /// <summary>IS Sharpe ratio is positive but too close to zero to be actionable after costs.</summary>
    MarginalSharpe,

    /// <summary>Strategy performance is too sensitive to position sizing parameter changes.</summary>
    PositionSizingSensitivity,
}
