namespace LascodiaTradingEngine.Application.StrategyGeneration;

public sealed record StrategyGenerationPendingArtifactRecord(
    long StrategyId,
    string CandidateId,
    string? CycleId,
    GenerationCheckpointStore.PendingCandidateState Candidate,
    bool NeedsCreationAudit,
    bool NeedsCreatedEvent,
    bool NeedsAutoPromoteEvent,
    int AttemptCount = 0,
    DateTime? LastAttemptAtUtc = null,
    string? LastErrorMessage = null,
    DateTime? CreationAuditLoggedAtUtc = null,
    Guid? CandidateCreatedEventId = null,
    DateTime? CandidateCreatedEventDispatchedAtUtc = null,
    Guid? AutoPromotedEventId = null,
    DateTime? AutoPromotedEventDispatchedAtUtc = null,
    DateTime? QuarantinedAtUtc = null,
    string? TerminalFailureReason = null);

public sealed record StrategyGenerationPendingArtifactLoadResult(
    IReadOnlyList<StrategyGenerationPendingArtifactRecord> PendingArtifacts,
    IReadOnlyList<long> CorruptArtifactIds);

public sealed record StrategyGenerationFailureRecord(
    string CandidateId,
    string? CycleId,
    string CandidateHash,
    LascodiaTradingEngine.Domain.Enums.StrategyType StrategyType,
    string Symbol,
    LascodiaTradingEngine.Domain.Enums.Timeframe Timeframe,
    string ParametersJson,
    string FailureStage,
    string FailureReason,
    string? DetailsJson = null);
