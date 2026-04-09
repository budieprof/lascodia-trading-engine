namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IStrategyGenerationHealthStore
{
    void UpdateState(StrategyGenerationHealthStateSnapshot snapshot);
    void UpdateState(Func<StrategyGenerationHealthStateSnapshot, StrategyGenerationHealthStateSnapshot> updater);
    void RecordPhaseSuccess(string phaseName, long durationMs, DateTime utcNow);
    void RecordPhaseFailure(string phaseName, string errorMessage, DateTime utcNow);
    StrategyGenerationHealthStateSnapshot GetState();
    IReadOnlyList<StrategyGenerationPhaseStateSnapshot> GetPhaseStates();
}

public sealed record StrategyGenerationPhaseStateSnapshot
{
    public string PhaseName { get; init; } = string.Empty;
    public DateTime? LastSuccessAtUtc { get; init; }
    public DateTime? LastFailureAtUtc { get; init; }
    public string? LastFailureMessage { get; init; }
    public int ConsecutiveFailures { get; init; }
    public long LastDurationMs { get; init; }
}

public sealed record StrategyGenerationHealthStateSnapshot
{
    public int PendingArtifacts { get; init; }
    public DateTime? OldestPendingArtifactAttemptAtUtc { get; init; }
    public int UnresolvedFailures { get; init; }
    public DateTime? LastReplayCompletedAtUtc { get; init; }
    public DateTime? LastReplayFailureAtUtc { get; init; }
    public string? LastReplayFailureMessage { get; init; }
    public int LastReplayArtifactCount { get; init; }
    public int LastReplayCorruptArtifactCount { get; init; }
    public string? LastSkipReason { get; init; }
    public DateTime? LastSkippedAtUtc { get; init; }
    public DateTime? LastCheckpointSavedAtUtc { get; init; }
    public string? LastCheckpointLabel { get; init; }
    public bool IsCheckpointPersistenceDegraded { get; init; }
    public int ConsecutiveCheckpointSaveFailures { get; init; }
    public DateTime? LastCheckpointSaveFailureAtUtc { get; init; }
    public string? LastCheckpointSaveFailureMessage { get; init; }
    public int CheckpointClearFailures { get; init; }
    public DateTime? LastCheckpointClearedAtUtc { get; init; }
    public DateTime? LastCheckpointClearFailureAtUtc { get; init; }
    public string? LastCheckpointClearFailureMessage { get; init; }
    public int SummaryPublishFailures { get; init; }
    public DateTime? LastSummaryPublishedAtUtc { get; init; }
    public DateTime? LastSummaryPublishFailureAtUtc { get; init; }
    public string? LastSummaryPublishFailureMessage { get; init; }
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}
