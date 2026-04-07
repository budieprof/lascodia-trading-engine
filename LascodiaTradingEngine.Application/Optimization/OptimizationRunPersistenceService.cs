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

    public OptimizationRunPersistenceService(
        ILogger<OptimizationRunPersistenceService> logger,
        OptimizationRunMetadataService runMetadataService)
    {
        _logger = logger;
        _runMetadataService = runMetadataService;
    }

    internal async Task PersistAsync(
        OptimizationRunPersistenceContext context,
        IWriteApplicationDbContext writeCtx,
        PipelinePersistenceState persistenceState,
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
        OptimizationExecutionLeasePolicy.StampHeartbeat(run);
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

        if (vr.Passed)
            return;

        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Completed, DateTime.UtcNow);
        await writeCtx.SaveChangesAsync(ct);
        persistenceState.CompletionPersisted = true;
    }
}
