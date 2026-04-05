namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Defines the role of an ML model in the champion-challenger evaluation framework.
/// </summary>
public enum ModelRole
{
    /// <summary>Currently active production model serving live predictions.</summary>
    Champion = 0,

    /// <summary>Candidate model running in shadow mode to be evaluated against the champion.</summary>
    Challenger = 1
}
