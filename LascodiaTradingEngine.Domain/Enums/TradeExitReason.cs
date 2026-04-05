namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Records the reason a trade was closed during backtesting or live execution.
/// </summary>
public enum TradeExitReason
{
    /// <summary>Position was closed by hitting the stop-loss level.</summary>
    StopLoss   = 0,

    /// <summary>Position was closed by hitting the take-profit level.</summary>
    TakeProfit = 1,

    /// <summary>Position was closed because the backtest data window ended.</summary>
    EndOfData  = 2
}
