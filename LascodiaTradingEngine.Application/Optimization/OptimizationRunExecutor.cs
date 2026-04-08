using System.Diagnostics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Scoped, typeof(IOptimizationRunExecutor))]
internal sealed class OptimizationRunExecutor : IOptimizationRunExecutor
{
    private static readonly ActivitySource s_activitySource = new("LascodiaTradingEngine.Optimization");
    private static readonly TimeSpan CompletionPublicationTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<OptimizationRunExecutor> _logger;
    private readonly TradingMetrics _metrics;
    private readonly OptimizationDataLoader _dataLoader;
    private readonly OptimizationCandidateRefinementService _candidateRefinementService;
    private readonly OptimizationSearchCoordinator _searchCoordinator;
    private readonly OptimizationValidationCoordinator _validationCoordinator;
    private readonly OptimizationApprovalCoordinator _approvalCoordinator;
    private readonly OptimizationCompletionPublisher _completionPublisher;
    private readonly OptimizationRunMetadataService _runMetadataService;
    private readonly OptimizationRunPersistenceService _runPersistenceService;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationRunExecutor(
        ILogger<OptimizationRunExecutor> logger,
        TradingMetrics metrics,
        OptimizationDataLoader dataLoader,
        OptimizationCandidateRefinementService candidateRefinementService,
        OptimizationSearchCoordinator searchCoordinator,
        OptimizationValidationCoordinator validationCoordinator,
        OptimizationApprovalCoordinator approvalCoordinator,
        OptimizationCompletionPublisher completionPublisher,
        OptimizationRunMetadataService runMetadataService,
        OptimizationRunPersistenceService runPersistenceService,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _metrics = metrics;
        _dataLoader = dataLoader;
        _candidateRefinementService = candidateRefinementService;
        _searchCoordinator = searchCoordinator;
        _validationCoordinator = validationCoordinator;
        _approvalCoordinator = approvalCoordinator;
        _completionPublisher = completionPublisher;
        _runMetadataService = runMetadataService;
        _runPersistenceService = runPersistenceService;
        _timeProvider = timeProvider;
    }

    public async Task ExecuteAsync(
        OptimizationRun run,
        Strategy strategy,
        OptimizationConfig config,
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IAlertDispatcher alertDispatcher,
        IIntegrationEventService eventService,
        Stopwatch sw,
        CancellationToken ct,
        CancellationToken runCt)
    {
        using var dataLoadActivity = s_activitySource.StartActivity("optimization.data_load");
        var phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "DataLoad");
        SetRunStage(run, OptimizationExecutionStage.DataLoad, "Loading candles, split windows, and baseline evaluation context.");
        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct);

        var dataLoad = await _dataLoader.LoadAsync(db, run, strategy, config.ToDataLoadingConfig(), runCt);

        phase.Dispose();
        dataLoadActivity?.SetTag("candle.count", dataLoad.AllCandles.Count);
        dataLoadActivity?.SetTag("train.count", dataLoad.TrainCandles.Count);
        dataLoadActivity?.SetTag("test.count", dataLoad.TestCandles.Count);

        var candles = dataLoad.AllCandles;
        var trainCandles = dataLoad.TrainCandles;
        var testCandles = dataLoad.TestCandles;
        int embargoSize = dataLoad.EmbargoSize;
        var screeningOptions = dataLoad.ScreeningOptions;
        var protocol = dataLoad.Protocol;
        var candleLookbackStart = dataLoad.CandleLookbackStart;
        var optimizationRegime = dataLoad.CurrentRegimeForBaseline;
        var baselineComparisonScore = dataLoad.BaselineComparisonScore;
        var baselineParamsJson = dataLoad.BaselineParametersJson;
        var pairInfo = dataLoad.PairInfo;
        bool baselineRegimeParamsUsed = dataLoad.BaselineRegimeParamsUsed;

        using var searchActivity = s_activitySource.StartActivity("optimization.search");
        phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Search");
        SetRunStage(run, OptimizationExecutionStage.Search, "Exploring parameter candidates with the surrogate-guided search loop.");
        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct);
        var searchResult = await _searchCoordinator.RunAsync(
            db,
            run,
            strategy,
            config.ToSearchConfig(),
            trainCandles,
            candles,
            screeningOptions,
            protocol,
            embargoSize,
            optimizationRegime,
            writeCtx,
            ct,
            runCt);

        var allEvaluated = searchResult.EvaluatedCandidates;
        int totalIters = searchResult.TotalIterations;
        var searchDiagnostics = searchResult.Diagnostics;

        var persistenceRegime = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == strategy.Symbol
                     && s.Timeframe == strategy.Timeframe
                     && !s.IsDeleted)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => (MarketRegimeEnum?)s.Regime)
            .FirstOrDefaultAsync(runCt);

        if (allEvaluated.Count == 0)
        {
            run.Iterations = totalIters;
            OptimizationExecutionLeasePolicy.StampHeartbeat(run, UtcNow);
            run.RunMetadataJson = _runMetadataService.SerializeRunMetadata(
                run,
                strategy,
                candles,
                trainCandles,
                testCandles,
                embargoSize,
                searchResult.SurrogateKind,
                searchResult.ResumedFromCheckpoint,
                optimizationRegime,
                persistenceRegime,
                baselineRegimeParamsUsed,
                searchResult.WarmStartedObservations,
                totalIters,
                baselineComparisonScore,
                searchDiagnostics,
                oosHealthScore: null,
                autoApproved: null);
            throw new OptimizationSearchExhaustedException();
        }

        phase.Dispose();
        searchActivity?.SetTag("surrogate", searchResult.SurrogateKind);
        searchActivity?.SetTag("iterations", totalIters);
        searchActivity?.SetTag("candidates.evaluated", allEvaluated.Count);

        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct);

        var refinement = await _candidateRefinementService.RefineAsync(
            allEvaluated,
            strategy,
            trainCandles,
            screeningOptions,
            config,
            totalIters,
            runCt);
        totalIters = refinement.TotalIterations;

        using var validationActivity = s_activitySource.StartActivity("optimization.validation");
        phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Validation");
        SetRunStage(run, OptimizationExecutionStage.Validation, "Re-scoring Pareto candidates and applying approval validation gates.");
        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct);

        var vr = await _validationCoordinator.ValidateAsync(
            refinement.RankedCandidates,
            strategy,
            run,
            trainCandles,
            testCandles,
            screeningOptions,
            protocol,
            config.ToValidationConfig(),
            db,
            totalIters,
            baselineComparisonScore,
            baselineParamsJson,
            writeCtx,
            pairInfo,
            ct,
            runCt);
        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct);

        phase.Dispose();
        validationActivity?.SetTag("approval.passed", vr.Passed);
        validationActivity?.SetTag("oos.validated", vr.HasOosValidation);
        if (vr.HasOosValidation)
            validationActivity?.SetTag("oos.score", (double)vr.OosHealthScore);

        using var persistActivity = s_activitySource.StartActivity("optimization.persist");
        phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Persist");
        SetRunStage(run, OptimizationExecutionStage.Persist, "Persisting best candidate metrics, reports, and run metadata.");
        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct);
        await _runPersistenceService.PersistAsync(
            new OptimizationRunPersistenceContext(
                run,
                strategy,
                candles,
                trainCandles,
                testCandles,
                embargoSize,
                optimizationRegime,
                persistenceRegime,
                baselineRegimeParamsUsed,
                baselineComparisonScore,
                searchResult,
                vr,
                totalIters),
            writeCtx,
            ct);

        phase.Dispose();

        sw.Stop();
        double elapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
        double elapsedSeconds = sw.Elapsed.TotalSeconds;
        double candidatesPerSec = totalIters / Math.Max(1.0, elapsedSeconds);

        using var approvalActivity = s_activitySource.StartActivity("optimization.approval");
        phase = PhaseTimer.Start(_logger, _metrics, run.Id, run.StrategyId, "Approval");
        SetRunStage(run, OptimizationExecutionStage.Approval, "Applying auto-approval or manual-review decision.");
        await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct);

        try
        {
            var ctx = new RunContext(
                run,
                strategy,
                config,
                baselineComparisonScore,
                db,
                writeDb,
                writeCtx,
                mediator,
                alertDispatcher,
                eventService,
                ct,
                runCt);
            await _approvalCoordinator.ApplyAsync(
                ctx,
                ctx.Config.ToApprovalConfig(),
                vr,
                optimizationRegime,
                candleLookbackStart,
                screeningOptions);
        }
        finally
        {
            phase.Dispose();
            approvalActivity?.SetTag("approval.decision", run.Status.ToString());

            if (OptimizationRunLifecycle.ShouldPreservePersistedResult(run)
                && !OptimizationRunLifecycle.HasPublishedCompletionArtifacts(run))
            {
                using var completionPublishCts = new CancellationTokenSource(CompletionPublicationTimeout);
                await PublishCompletionArtifactsAsync(
                    run,
                    strategy,
                    writeCtx,
                    mediator,
                    eventService,
                    vr,
                    totalIters,
                    candidatesPerSec,
                    elapsedMilliseconds,
                    completionPublishCts.Token);
            }
        }

        if (OptimizationRunLifecycle.HasPersistedTerminalResult(run))
        {
            _metrics.OptimizationRunsProcessed.Add(1);
            _metrics.OptimizationCycleDurationMs.Record(elapsedMilliseconds);
            _metrics.OptimizationComputeSeconds.Record(
                elapsedSeconds,
                new KeyValuePair<string, object?>("strategy_type", strategy.StrategyType.ToString()));
        }
    }

    internal async Task PublishCompletionArtifactsAsync(
        OptimizationRun run,
        Strategy strategy,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IIntegrationEventService eventService,
        CandidateValidationResult vr,
        int totalIters,
        double candidatesPerSec,
        double elapsedMilliseconds,
        CancellationToken ct)
    {
        using var fallbackCompletionCts = ct.IsCancellationRequested
            ? new CancellationTokenSource(CompletionPublicationTimeout)
            : null;
        var durableCt = fallbackCompletionCts?.Token ?? ct;
        string completedOosLabel = vr.HasOosValidation ? $"{vr.OosHealthScore:F2}" : "n/a";
        decimal? completedOosScore = vr.HasOosValidation ? vr.OosHealthScore : null;

        if (vr.HasOosValidation)
        {
            _logger.LogInformation(
                "OptimizationRunExecutor: run {RunId} completed — Iter={Iter} ({CPS:F1}/s), IS={IS:F2}, OOS={OOS}, CI=[{CIL:F2},{CIU:F2}], WF={WF:F2}, Sens={Sens}, CostOk={Cost}, Baseline={Base:F2} in {Ms:F0}ms",
                run.Id,
                run.Iterations,
                candidatesPerSec,
                vr.Winner.HealthScore,
                completedOosLabel,
                vr.CILower,
                vr.CIUpper,
                vr.WfAvgScore,
                vr.SensitivityOk,
                vr.CostSensitiveOk,
                run.BaselineHealthScore,
                elapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation(
                "OptimizationRunExecutor: run {RunId} completed without approval-grade OOS validation — Iter={Iter} ({CPS:F1}/s), IS={IS:F2}, OOS=n/a, Reason={Reason}, Baseline={Base:F2} in {Ms:F0}ms",
                run.Id,
                run.Iterations,
                candidatesPerSec,
                vr.Winner.HealthScore,
                vr.FailureReason,
                run.BaselineHealthScore,
                elapsedMilliseconds);
        }

        await TryLogDecisionAsync(
            mediator,
            new LogDecisionCommand
            {
                EntityType = "OptimizationRun",
                EntityId = run.Id,
                DecisionType = "OptimizationCompleted",
                Outcome = "Completed",
                Reason = vr.HasOosValidation
                    ? $"Iter={run.Iterations}, IS={vr.Winner.HealthScore:F2}, OOS={completedOosLabel}, CI95=[{vr.CILower:F2},{vr.CIUpper:F2}], WF_Avg={vr.WfAvgScore:F2}, WF_Stable={vr.WfStable}, MTF={vr.MtfCompatible}, ParamCorr={!vr.CorrelationSafe}, TemporalCorr={vr.TemporalMaxOverlap:P0}, PortfolioCorr={vr.PortfolioMaxCorrelation:P0}, Sensitivity={vr.SensitivityOk}, CostSensitive={vr.CostSensitiveOk} (pess={vr.PessimisticScore:F2}), PermTest p={vr.PermPValue:F3} α_corrected={vr.PermCorrectedAlpha:F4} sig={vr.PermSignificant} (N={totalIters}), Baseline={run.BaselineHealthScore:F2}, Throughput={candidatesPerSec:F1}/s"
                    : $"Iter={run.Iterations}, IS={vr.Winner.HealthScore:F2}, OOS=n/a, ApprovalGradeValidationSkipped=true, FailureReason={vr.FailureReason}, Baseline={run.BaselineHealthScore:F2}, Throughput={candidatesPerSec:F1}/s",
                Source = "OptimizationWorker"
            },
            durableCt,
            "OptimizationRunExecutor: completion audit log failed for run {RunId} (non-fatal)",
            run.Id);

        try
        {
            SetRunStage(run, OptimizationExecutionStage.CompletionPublication, "Persisting and publishing terminal completion side effects.");
            var completedEvent = new OptimizationCompletedIntegrationEvent
            {
                OptimizationRunId = run.Id,
                StrategyId = run.StrategyId,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                Iterations = run.Iterations,
                BaselineScore = run.BaselineHealthScore ?? 0m,
                BestOosScore = completedOosScore ?? 0m,
                CompletedAt = run.CompletedAt ?? UtcNow,
            };

            if (run.CompletionPublicationStatus != OptimizationCompletionPublicationStatus.Published)
            {
                await _completionPublisher.PrepareAsync(run, writeCtx, completedEvent, durableCt);
                await _completionPublisher.PublishWithFallbackAsync(run.Id, completedEvent, durableCt);
            }
        }
        catch (Exception ex)
        {
            OptimizationRunProgressTracker.RecordOperationalIssue(
                run,
                "CompletionPublicationFailed",
                $"Completion artifact publication degraded: {ex.Message}",
                UtcNow);
            run.CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Failed;
            run.CompletionPublicationErrorMessage = TruncateForPersistence(ex.Message, 500);
            await writeCtx.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(ex,
                "OptimizationRunExecutor: completion artifact publication failed for run {RunId}",
                run.Id);
        }
    }

    private async Task TryLogDecisionAsync(
        IMediator mediator,
        LogDecisionCommand command,
        CancellationToken ct,
        string failureMessage,
        params object?[] args)
    {
        try
        {
            await mediator.Send(command, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureMessage, args);
        }
    }

    private void SetRunStage(
        OptimizationRun run,
        OptimizationExecutionStage stage,
        string? message)
        => OptimizationRunProgressTracker.SetStage(run, stage, message, UtcNow);

    private static string? TruncateForPersistence(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
