namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Algorithmic execution strategy for order placement.</summary>
public enum ExecutionAlgorithmType
{
    /// <summary>Single market or limit order — no slicing.</summary>
    Direct = 0,
    /// <summary>Time-Weighted Average Price — slices order into equal child orders over a time window.</summary>
    TWAP = 1,
    /// <summary>Volume-Weighted Average Price — slices order proportional to historical volume profile.</summary>
    VWAP = 2
}
