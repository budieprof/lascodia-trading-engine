using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetStrategyGenerationWorkerHealth;

public sealed class StrategyGenerationWorkerPhaseHealthDto
{
    public string PhaseName { get; init; } = string.Empty;
    public DateTime? LastSuccessAtUtc { get; init; }
    public DateTime? LastFailureAtUtc { get; init; }
    public string? LastFailureMessage { get; init; }
    public int ConsecutiveFailures { get; init; }
    public long LastDurationMs { get; init; }
}

public sealed class StrategyGenerationWorkerHealthDto
{
    public int PendingArtifacts { get; init; }
    public int QuarantinedArtifacts { get; init; }
    public int OldestPendingArtifactAgeSeconds { get; init; }
    public DateTime? LastArtifactQuarantinedAtUtc { get; init; }
    public string? LastArtifactQuarantineReason { get; init; }
    public int UnresolvedFailures { get; init; }
    public DateTime? LastReplayCompletedAtUtc { get; init; }
    public DateTime? LastReplayFailureAtUtc { get; init; }
    public string? LastReplayFailureMessage { get; init; }
    public int LastReplayArtifactCount { get; init; }
    public int LastReplayCorruptArtifactCount { get; init; }
    public int PendingSummaryDispatches { get; init; }
    public string? LastSkipReason { get; init; }
    public DateTime? LastSkippedAtUtc { get; init; }
    public DateTime? LastCheckpointSavedAtUtc { get; init; }
    public int CheckpointAgeSeconds { get; init; }
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
    public IReadOnlyList<StrategyGenerationWorkerPhaseHealthDto> PhaseStates { get; init; } = [];
}

public class GetStrategyGenerationWorkerHealthQuery : IRequest<ResponseData<StrategyGenerationWorkerHealthDto>>
{
}

public class GetStrategyGenerationWorkerHealthQueryHandler
    : IRequestHandler<GetStrategyGenerationWorkerHealthQuery, ResponseData<StrategyGenerationWorkerHealthDto>>
{
    private readonly IStrategyGenerationHealthStore _healthStore;
    private readonly TimeProvider _timeProvider;

    public GetStrategyGenerationWorkerHealthQueryHandler(
        IStrategyGenerationHealthStore healthStore,
        TimeProvider timeProvider)
    {
        _healthStore = healthStore;
        _timeProvider = timeProvider;
    }

    public Task<ResponseData<StrategyGenerationWorkerHealthDto>> Handle(
        GetStrategyGenerationWorkerHealthQuery request,
        CancellationToken cancellationToken)
    {
        var state = _healthStore.GetState();
        var phaseStates = _healthStore.GetPhaseStates();
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var dto = new StrategyGenerationWorkerHealthDto
        {
            PendingArtifacts = state.PendingArtifacts,
            QuarantinedArtifacts = state.QuarantinedArtifacts,
            OldestPendingArtifactAgeSeconds = ComputeAgeSeconds(nowUtc, state.OldestPendingArtifactAttemptAtUtc),
            LastArtifactQuarantinedAtUtc = state.LastArtifactQuarantinedAtUtc,
            LastArtifactQuarantineReason = state.LastArtifactQuarantineReason,
            UnresolvedFailures = state.UnresolvedFailures,
            LastReplayCompletedAtUtc = state.LastReplayCompletedAtUtc,
            LastReplayFailureAtUtc = state.LastReplayFailureAtUtc,
            LastReplayFailureMessage = state.LastReplayFailureMessage,
            LastReplayArtifactCount = state.LastReplayArtifactCount,
            LastReplayCorruptArtifactCount = state.LastReplayCorruptArtifactCount,
            PendingSummaryDispatches = state.PendingSummaryDispatches,
            LastSkipReason = state.LastSkipReason,
            LastSkippedAtUtc = state.LastSkippedAtUtc,
            LastCheckpointSavedAtUtc = state.LastCheckpointSavedAtUtc,
            CheckpointAgeSeconds = ComputeAgeSeconds(nowUtc, state.LastCheckpointSavedAtUtc),
            LastCheckpointLabel = state.LastCheckpointLabel,
            IsCheckpointPersistenceDegraded = state.IsCheckpointPersistenceDegraded,
            ConsecutiveCheckpointSaveFailures = state.ConsecutiveCheckpointSaveFailures,
            LastCheckpointSaveFailureAtUtc = state.LastCheckpointSaveFailureAtUtc,
            LastCheckpointSaveFailureMessage = state.LastCheckpointSaveFailureMessage,
            CheckpointClearFailures = state.CheckpointClearFailures,
            LastCheckpointClearedAtUtc = state.LastCheckpointClearedAtUtc,
            LastCheckpointClearFailureAtUtc = state.LastCheckpointClearFailureAtUtc,
            LastCheckpointClearFailureMessage = state.LastCheckpointClearFailureMessage,
            SummaryPublishFailures = state.SummaryPublishFailures,
            LastSummaryPublishedAtUtc = state.LastSummaryPublishedAtUtc,
            LastSummaryPublishFailureAtUtc = state.LastSummaryPublishFailureAtUtc,
            LastSummaryPublishFailureMessage = state.LastSummaryPublishFailureMessage,
            PhaseStates = phaseStates
                .OrderBy(x => x.PhaseName, StringComparer.Ordinal)
                .Select(MapPhaseState)
                .ToArray(),
        };

        return Task.FromResult(ResponseData<StrategyGenerationWorkerHealthDto>.Init(dto, true, "Successful", "00"));
    }

    private static int ComputeAgeSeconds(DateTime nowUtc, DateTime? anchorUtc)
    {
        if (anchorUtc == null)
            return 0;

        return Math.Max(0, (int)Math.Round((nowUtc - anchorUtc.Value).TotalSeconds));
    }

    private static StrategyGenerationWorkerPhaseHealthDto MapPhaseState(StrategyGenerationPhaseStateSnapshot snapshot)
    {
        return new StrategyGenerationWorkerPhaseHealthDto
        {
            PhaseName = snapshot.PhaseName,
            LastSuccessAtUtc = snapshot.LastSuccessAtUtc,
            LastFailureAtUtc = snapshot.LastFailureAtUtc,
            LastFailureMessage = snapshot.LastFailureMessage,
            ConsecutiveFailures = snapshot.ConsecutiveFailures,
            LastDurationMs = snapshot.LastDurationMs,
        };
    }
}
