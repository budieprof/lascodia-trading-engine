namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Defines configuration keys that should not influence the checkpoint compatibility fingerprint.
/// </summary>
/// <remarks>
/// These keys represent runtime bookkeeping or replay state, not the logical screening inputs
/// that determine whether a saved checkpoint can be safely resumed.
/// </remarks>
internal static class StrategyGenerationCheckpointFingerprintPolicy
{
    internal static IReadOnlySet<string> ExcludedConfigKeys { get; } = new HashSet<string>(
        [
            GenerationCheckpointStore.ConfigKey,
            "StrategyGeneration:PendingPostPersistArtifacts",
            "StrategyGeneration:FailedCandidateKeys",
            "StrategyGeneration:PreviousCycleStats",
            "StrategyGeneration:FeedbackSummary",
            "StrategyGeneration:LastRunDateUtc",
            "StrategyGeneration:CircuitBreakerUntilUtc",
            "StrategyGeneration:ConsecutiveFailures",
            "StrategyGeneration:RetriesThisWindow",
            "StrategyGeneration:RetryWindowDateUtc",
        ],
        StringComparer.OrdinalIgnoreCase);
}
