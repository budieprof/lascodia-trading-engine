using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

internal sealed record OptimizationRunPersistenceContext(
    OptimizationRun Run,
    Strategy Strategy,
    IReadOnlyList<Candle> Candles,
    IReadOnlyList<Candle> TrainCandles,
    IReadOnlyList<Candle> TestCandles,
    int EmbargoSize,
    MarketRegimeEnum? OptimizationRegime,
    MarketRegimeEnum? PersistenceRegime,
    bool BaselineRegimeParamsUsed,
    decimal BaselineComparisonScore,
    SearchResult SearchResult,
    CandidateValidationResult ValidationResult,
    int TotalIterations);

[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationRunPersistenceService
{
    private readonly ILogger<OptimizationRunPersistenceService> _logger;
    private readonly OptimizationRunMetadataService _runMetadataService;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationRunPersistenceService(
        ILogger<OptimizationRunPersistenceService> logger,
        OptimizationRunMetadataService runMetadataService,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _runMetadataService = runMetadataService;
        _timeProvider = timeProvider;
    }

    internal async Task PersistAsync(
        OptimizationRunPersistenceContext context,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct)
    {
        var run = context.Run;
        var vr = context.ValidationResult;
        var searchResult = context.SearchResult;

        run.Iterations = context.TotalIterations;
        run.IntermediateResultsJson = null;
        run.CheckpointVersion = 0;
        run.BestParametersJson = CanonicalParameterJson.Normalize(vr.Winner.ParamsJson);
        run.BestHealthScore = vr.HasOosValidation ? vr.OosHealthScore : null;
        run.BestSharpeRatio = vr.HasOosValidation ? vr.OosResult.SharpeRatio : null;
        run.BestMaxDrawdownPct = vr.HasOosValidation ? vr.OosResult.MaxDrawdownPct : null;
        run.BestWinRate = vr.HasOosValidation ? vr.OosResult.WinRate : null;
        run.ApprovalReportJson = OptimizationCheckpointStore.LimitJsonPayload(
            vr.ApprovalReportJson,
            OptimizationCheckpointStore.MaxApprovalReportChars,
            "approval report",
            _logger);
        OptimizationExecutionLeasePolicy.StampHeartbeat(run, UtcNow);
        run.RunMetadataJson = _runMetadataService.SerializeRunMetadata(
            run,
            context.Strategy,
            context.Candles,
            context.TrainCandles,
            context.TestCandles,
            context.EmbargoSize,
            searchResult.SurrogateKind,
            searchResult.ResumedFromCheckpoint,
            context.OptimizationRegime,
            context.PersistenceRegime,
            context.BaselineRegimeParamsUsed,
            searchResult.WarmStartedObservations,
            context.TotalIterations,
            context.BaselineComparisonScore,
            searchResult.Diagnostics,
            vr.HasOosValidation ? vr.OosHealthScore : null,
            vr.Passed);

        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Completed, UtcNow);
        run.ResultsPersistedAt = UtcNow;
        run.ApprovalEvaluatedAt = null;
        run.DeferralReason = null;
        run.DeferredAtUtc = null;
        run.ValidationFollowUpsCreatedAt = null;
        run.ValidationFollowUpStatus = null;
        run.FollowUpLastCheckedAt = null;
        run.NextFollowUpCheckAt = null;
        run.FollowUpRepairAttempts = 0;
        run.FollowUpLastStatusCode = null;
        run.FollowUpLastStatusMessage = null;
        run.FollowUpStatusUpdatedAt = null;
        run.CompletionPublicationPayloadJson = null;
        run.CompletionPublicationStatus = null;
        run.CompletionPublicationAttempts = 0;
        run.CompletionPublicationLastAttemptAt = null;
        run.CompletionPublicationPreparedAt = null;
        run.CompletionPublicationCompletedAt = null;
        run.CompletionPublicationErrorMessage = null;
        run.LifecycleReconciledAt = null;
        await writeCtx.SaveChangesAsync(ct);
    }
}
