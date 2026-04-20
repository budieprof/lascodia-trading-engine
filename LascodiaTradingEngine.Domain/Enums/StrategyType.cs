namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Identifies the algorithmic strategy type used for signal generation.
/// </summary>
public enum StrategyType
{
    /// <summary>Generates signals on moving average crossover events.</summary>
    MovingAverageCrossover = 0,

    /// <summary>Mean-reversion strategy based on RSI overbought/oversold levels.</summary>
    RSIReversion           = 1,

    /// <summary>Scalping strategy that trades breakouts from consolidation zones.</summary>
    BreakoutScalper        = 2,

    /// <summary>User-defined custom strategy with external evaluation logic.</summary>
    Custom                 = 3,

    /// <summary>Mean-reversion strategy using Bollinger Band extremes.</summary>
    BollingerBandReversion = 4,

    /// <summary>Divergence strategy based on MACD histogram and price action.</summary>
    MACDDivergence         = 5,

    /// <summary>Breakout strategy targeting the open of a major trading session.</summary>
    SessionBreakout        = 6,

    /// <summary>Trend-following strategy using momentum indicators.</summary>
    MomentumTrend          = 7,

    /// <summary>Composite ML evaluator using trained models with Platt-calibrated probabilities.</summary>
    CompositeML            = 8,

    /// <summary>Statistical arbitrage via cointegrated pair spread z-score mean-reversion.</summary>
    StatisticalArbitrage   = 9,

    /// <summary>Mean-reversion to session VWAP when price deviates beyond ATR threshold.</summary>
    VwapReversion          = 10,

    /// <summary>
    /// Captures institutional calendar flows: month-end rebalancing (last N business days
    /// fade short-horizon momentum) and London-NY session overlap (continuation during the
    /// 13:00-16:00 UTC liquidity peak).
    /// </summary>
    CalendarEffect         = 11
}
