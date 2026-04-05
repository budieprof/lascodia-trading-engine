namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Indicates whether a scale order adds to or reduces an existing position.
/// </summary>
public enum ScaleType
{
    /// <summary>Increase position size by adding lots at a new level.</summary>
    ScaleIn = 0,

    /// <summary>Reduce position size by partially closing lots.</summary>
    ScaleOut = 1
}
