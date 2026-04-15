namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Durable representation of deferred post-persist work for one generated strategy.
/// </summary>
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

/// <summary>
/// Pending-artifact load result split into valid replay items and corrupt rows.
/// </summary>
public sealed record StrategyGenerationPendingArtifactLoadResult(
    IReadOnlyList<StrategyGenerationPendingArtifactRecord> PendingArtifacts,
    IReadOnlyList<StrategyGenerationCorruptArtifactRecord> CorruptArtifacts);

/// <summary>
/// Corrupt pending-artifact row that should be quarantined from future replay attempts.
/// </summary>
public sealed record StrategyGenerationCorruptArtifactRecord(
    long ArtifactId,
    string Reason);

/// <summary>
/// Durable summary-event dispatch state used when reconciling cycle-summary publication.
/// </summary>
public sealed record StrategyGenerationSummaryDispatchRecord(
    string CycleId,
    Guid EventId,
    DateTime? FailedAtUtc,
    DateTime? DispatchedAtUtc,
    string? FailureMessage,
    string? PayloadJson);

/// <summary>
/// Durable failure payload recorded when candidate persistence or replay cannot be repaired
/// inline during a generation cycle.
/// </summary>
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
