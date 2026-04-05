namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Classifies the current market environment detected by the regime detection algorithm.
/// </summary>
public enum MarketRegime
{
    /// <summary>Market is in a sustained directional trend.</summary>
    Trending = 0,

    /// <summary>Market is oscillating within a defined range.</summary>
    Ranging = 1,

    /// <summary>Market exhibits above-average price volatility.</summary>
    HighVolatility = 2,

    /// <summary>Market exhibits below-average price volatility.</summary>
    LowVolatility = 3,

    /// <summary>Extreme risk-off environment with correlated sell-offs.</summary>
    Crisis = 4,

    /// <summary>Price has broken out of a consolidation range with momentum.</summary>
    Breakout = 5
}
