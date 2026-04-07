namespace LascodiaTradingEngine.Application.StrategyGeneration;

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
