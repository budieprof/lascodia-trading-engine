namespace LascodiaTradingEngine.Domain.Enums;
public enum StrategyType
{
    MovingAverageCrossover = 0,
    RSIReversion           = 1,
    BreakoutScalper        = 2,
    Custom                 = 3,
    BollingerBandReversion = 4,
    MACDDivergence         = 5,
    SessionBreakout        = 6,
    MomentumTrend          = 7
}
