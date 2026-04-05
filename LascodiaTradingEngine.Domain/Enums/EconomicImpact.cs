namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Classifies the expected market impact of an economic calendar event.
/// </summary>
public enum EconomicImpact
{
    /// <summary>Minor event with minimal expected price movement.</summary>
    Low = 0,

    /// <summary>Moderate event that may cause noticeable volatility.</summary>
    Medium = 1,

    /// <summary>Major event likely to trigger significant price swings.</summary>
    High = 2,

    /// <summary>Market holiday with reduced liquidity or closure.</summary>
    Holiday = 3
}
