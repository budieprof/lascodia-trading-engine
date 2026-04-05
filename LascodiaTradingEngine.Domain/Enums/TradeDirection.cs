namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Indicates the directional bias of a trade signal or strategy evaluation.
/// </summary>
public enum TradeDirection
{
    /// <summary>Signal recommends a long (buy) entry.</summary>
    Buy = 0,

    /// <summary>Signal recommends a short (sell) entry.</summary>
    Sell = 1
}
