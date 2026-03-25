namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// The margin accounting mode used by the broker for this account.
/// </summary>
public enum MarginMode
{
    /// <summary>Allows simultaneous long and short positions on the same symbol.</summary>
    Hedging,

    /// <summary>Opposite-direction orders reduce existing position volume.</summary>
    Netting
}
