namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>Classification of stress test scenarios.</summary>
public enum StressScenarioType
{
    /// <summary>Replay of a known historical event (e.g. SNB de-peg, COVID crash).</summary>
    Historical = 0,
    /// <summary>User-defined hypothetical shock (e.g. 5% EUR/USD gap).</summary>
    Hypothetical = 1,
    /// <summary>Reverse stress — finds the minimum shock that causes a specified loss level.</summary>
    ReverseStress = 2
}
