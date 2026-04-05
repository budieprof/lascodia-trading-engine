namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Tracks the lifecycle of a trade signal from generation through execution or expiry.
/// </summary>
public enum TradeSignalStatus
{
    /// <summary>Signal generated but awaiting validation and approval.</summary>
    Pending = 0,

    /// <summary>Signal passed Tier 1 validation and is available for order creation.</summary>
    Approved = 1,

    /// <summary>An order was successfully created and filled from this signal.</summary>
    Executed = 2,

    /// <summary>Signal was rejected by validation or risk checks.</summary>
    Rejected = 3,

    /// <summary>Signal expired before it could be executed.</summary>
    Expired = 4
}
