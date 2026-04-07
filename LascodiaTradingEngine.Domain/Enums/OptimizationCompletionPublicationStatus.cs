namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Tracks delivery of terminal optimization completion side effects so publication
/// can be retried independently from the optimization run itself.
/// </summary>
public enum OptimizationCompletionPublicationStatus
{
    /// <summary>Completion payload has been prepared but not confirmed published.</summary>
    Pending = 0,

    /// <summary>Completion payload was successfully persisted/published.</summary>
    Published = 1,

    /// <summary>Completion payload failed to publish and requires replay.</summary>
    Failed = 2,
}
