namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Reason a <see cref="Entities.PaperExecution"/> row closed. Kept separate from
/// <see cref="TradeExitReason"/> (which covers backtest-only states) because paper
/// execution has live-specific exits like Timeout and StrategyReversal.
/// </summary>
public enum PaperExitReason
{
    StopLoss         = 0,
    TakeProfit       = 1,
    /// <summary>Forced close after the signal-timeout window elapsed without a bracket hit.</summary>
    Timeout          = 2,
    /// <summary>Opposite-direction signal from the same strategy closed the paper position.</summary>
    StrategyReversal = 3,
    /// <summary>Simulation ended (e.g. strategy retired) while the row was still open.</summary>
    EndOfEvaluation  = 4,
}
