namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Represents the current lifecycle state of a machine-learning model.
/// </summary>
public enum MLModelStatus
{
    /// <summary>Model is currently being trained.</summary>
    Training = 0,

    /// <summary>Model is deployed and actively generating predictions.</summary>
    Active = 1,

    /// <summary>Model was replaced by a newer version and is no longer in use.</summary>
    Superseded = 2,

    /// <summary>Training or validation failed; model is unusable.</summary>
    Failed = 3
}
