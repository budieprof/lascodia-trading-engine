using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetOptimizationWorkerHealth;

public sealed class OptimizationWorkerHealthSnapshotDto
{
    public string WorkerName { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public DateTime? LastErrorAt { get; init; }
    public string? LastErrorMessage { get; init; }
    public long LastCycleDurationMs { get; init; }
    public long CycleDurationP50Ms { get; init; }
    public long CycleDurationP95Ms { get; init; }
    public long CycleDurationP99Ms { get; init; }
    public int ConsecutiveFailures { get; init; }
    public int ErrorsLastHour { get; init; }
    public int SuccessesLastHour { get; init; }
    public int BacklogDepth { get; init; }
    public int ConfiguredIntervalSeconds { get; init; }
}

public sealed class OptimizationWorkerHealthDto
{
    public OptimizationWorkerHealthSnapshotDto? OptimizationWorker { get; init; }
    public OptimizationWorkerHealthSnapshotDto? CompletionReplayWorker { get; init; }
    public int QueuedRuns { get; init; }
    public int RunningRuns { get; init; }
    public int RetryableFailedRuns { get; init; }
    public int AbandonedRuns { get; init; }
    public int PendingFollowUps { get; init; }
    public int PendingCompletionPublications { get; init; }
    public int ConfigCacheAgeSeconds { get; init; }
    public DateTime? ConfigRefreshDueAtUtc { get; init; }
    public int ConfigRefreshIntervalSeconds { get; init; }
    public long? OldestRunningRunId { get; init; }
    public OptimizationExecutionStage? OldestRunningStage { get; init; }
    public string? OldestRunningStageMessage { get; init; }
    public DateTime? OldestRunningStageUpdatedAt { get; init; }
}

public class GetOptimizationWorkerHealthQuery : IRequest<ResponseData<OptimizationWorkerHealthDto>>
{
}

public class GetOptimizationWorkerHealthQueryHandler
    : IRequestHandler<GetOptimizationWorkerHealthQuery, ResponseData<OptimizationWorkerHealthDto>>
{
    private readonly IWorkerHealthMonitor _healthMonitor;
    private readonly IOptimizationWorkerHealthStore _optimizationHealthStore;

    public GetOptimizationWorkerHealthQueryHandler(
        IWorkerHealthMonitor healthMonitor,
        IOptimizationWorkerHealthStore optimizationHealthStore)
    {
        _healthMonitor = healthMonitor;
        _optimizationHealthStore = optimizationHealthStore;
    }

    public Task<ResponseData<OptimizationWorkerHealthDto>> Handle(
        GetOptimizationWorkerHealthQuery request,
        CancellationToken cancellationToken)
    {
        var snapshots = _healthMonitor.GetCurrentSnapshots();
        var optimizationWorker = snapshots.FirstOrDefault(s => s.WorkerName == "OptimizationWorker");
        var replayWorker = snapshots.FirstOrDefault(s => s.WorkerName == "OptimizationCompletionReplayWorker");
        var typedState = _optimizationHealthStore.GetMainWorkerState();

        var dto = new OptimizationWorkerHealthDto
        {
            OptimizationWorker = MapSnapshot(optimizationWorker),
            CompletionReplayWorker = MapSnapshot(replayWorker),
            QueuedRuns = typedState.QueuedRuns,
            RunningRuns = typedState.RunningRuns,
            RetryableFailedRuns = typedState.RetryableFailedRuns,
            AbandonedRuns = typedState.AbandonedRuns,
            PendingFollowUps = typedState.PendingFollowUps,
            PendingCompletionPublications = typedState.PendingCompletionPublications,
            ConfigCacheAgeSeconds = typedState.ConfigCacheAgeSeconds,
            ConfigRefreshDueAtUtc = typedState.ConfigRefreshDueAtUtc,
            ConfigRefreshIntervalSeconds = typedState.ConfigRefreshIntervalSeconds,
            OldestRunningRunId = typedState.OldestRunningRunId,
            OldestRunningStage = typedState.OldestRunningStage,
            OldestRunningStageMessage = typedState.OldestRunningStageMessage,
            OldestRunningStageUpdatedAt = typedState.OldestRunningStageUpdatedAt,
        };

        return Task.FromResult(ResponseData<OptimizationWorkerHealthDto>.Init(dto, true, "Successful", "00"));
    }

    private static OptimizationWorkerHealthSnapshotDto? MapSnapshot(WorkerHealthSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;

        return new OptimizationWorkerHealthSnapshotDto
        {
            WorkerName = snapshot.WorkerName,
            IsRunning = snapshot.IsRunning,
            LastSuccessAt = snapshot.LastSuccessAt,
            LastErrorAt = snapshot.LastErrorAt,
            LastErrorMessage = snapshot.LastErrorMessage,
            LastCycleDurationMs = snapshot.LastCycleDurationMs,
            CycleDurationP50Ms = snapshot.CycleDurationP50Ms,
            CycleDurationP95Ms = snapshot.CycleDurationP95Ms,
            CycleDurationP99Ms = snapshot.CycleDurationP99Ms,
            ConsecutiveFailures = snapshot.ConsecutiveFailures,
            ErrorsLastHour = snapshot.ErrorsLastHour,
            SuccessesLastHour = snapshot.SuccessesLastHour,
            BacklogDepth = snapshot.BacklogDepth,
            ConfiguredIntervalSeconds = snapshot.ConfiguredIntervalSeconds,
        };
    }
}
