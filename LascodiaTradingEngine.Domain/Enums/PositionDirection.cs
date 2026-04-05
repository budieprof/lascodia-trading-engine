namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Indicates the directional bias of an open position.
/// </summary>
public enum PositionDirection
{
    /// <summary>Long position expecting price appreciation.</summary>
    Long = 0,

    /// <summary>Short position expecting price depreciation.</summary>
    Short = 1
}
