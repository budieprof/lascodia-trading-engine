using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Scoped)]
internal sealed class OptimizationApprovalCoordinator
{
    private readonly ILogger<OptimizationApprovalCoordinator> _logger;
    private readonly TradingMetrics _metrics;
    private readonly OptimizationValidator _validator;
    private readonly OptimizationFollowUpCoordinator _followUpCoordinator;
    private readonly OptimizationApprovalArtifactStore _artifactStore;
    private readonly OptimizationCrossRegimePersistenceService _crossRegimePersistenceService;
    private readonly OptimizationChronicFailureEscalator _chronicFailureEscalator;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationApprovalCoordinator(
        ILogger<OptimizationApprovalCoordinator> logger,
        TradingMetrics metrics,
        OptimizationValidator validator,
        OptimizationFollowUpCoordinator followUpCoordinator,
        OptimizationApprovalArtifactStore artifactStore,
        OptimizationCrossRegimePersistenceService crossRegimePersistenceService,
        OptimizationChronicFailureEscalator chronicFailureEscalator,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _metrics = metrics;
        _validator = validator;
        _followUpCoordinator = followUpCoordinator;
        _artifactStore = artifactStore;
        _crossRegimePersistenceService = crossRegimePersistenceService;
        _chronicFailureEscalator = chronicFailureEscalator;
        _timeProvider = timeProvider;
    }

    internal async Task ApplyAsync(
        RunContext ctx,
        ApprovalConfig config,
        CandidateValidationResult vr,
        MarketRegimeEnum? currentRegime,
        DateTime candleLookbackStart,
        BacktestOptions screeningOptions)
    {
        var nowUtc = UtcNow;
        var run = ctx.Run;
        var strategy = ctx.Strategy;
        var db = ctx.Db;
        var writeDb = ctx.WriteDb;
        var writeCtx = ctx.WriteCtx;
        var mediator = ctx.Mediator;
        var alertDispatcher = ctx.AlertDispatcher;
        var eventService = ctx.EventService;
        var ct = ctx.Ct;
        var runCt = ctx.RunCt;
        var oosResult = vr.OosResult;
        decimal improvement = vr.OosHealthScore - ctx.BaselineComparisonScore;
        string failureReason = vr.FailureReason ?? "manual review required";

        if (vr.Passed)
        {
            var liveStrategy = await writeDb.Set<Strategy>()
                .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, runCt);

            if (liveStrategy is null)
            {
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Rejected, nowUtc);
                run.FailureCategory = OptimizationFailureCategory.StrategyRemoved;
                run.ApprovalReportJson = OptimizationApprovalReportParser.MarkStrategyRemoved(run.ApprovalReportJson);
                run.ApprovalEvaluatedAt = nowUtc;

                await writeCtx.SaveChangesAsync(ct);

                _logger.LogWarning(
                    "OptimizationApprovalCoordinator: strategy {StrategyId} disappeared before approval for run {RunId} — rejecting auto-approval",
                    run.StrategyId,
                    run.Id);

                try
                {
                    await mediator.Send(new LogDecisionCommand
                    {
                        EntityType = "OptimizationRun",
                        EntityId = run.Id,
                        DecisionType = "AutoApproval",
                        Outcome = "Rejected",
                        Reason = "Strategy removed before approved parameters could be applied",
                        Source = "OptimizationWorker"
                    }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "OptimizationApprovalCoordinator: approval rejection audit log failed for run {RunId} (non-fatal)",
                        run.Id);
                }

                return;
            }

            var originalRunStatus = run.Status;
            var originalApprovedAt = run.ApprovedAt;
            var originalCompletedAt = run.CompletedAt;
            var originalErrorMessage = run.ErrorMessage;
            var originalValidationFollowUpsCreatedAt = run.ValidationFollowUpsCreatedAt;
            var originalValidationFollowUpStatus = run.ValidationFollowUpStatus;
            var originalApprovalEvaluatedAt = run.ApprovalEvaluatedAt;

            string? originalRollbackParametersJson = liveStrategy.RollbackParametersJson;
            string? originalStrategyParametersJson = liveStrategy.ParametersJson;
            int? originalRolloutPct = liveStrategy.RolloutPct;
            DateTime? originalRolloutStartedAt = liveStrategy.RolloutStartedAt;
            long? originalRolloutOptimizationRunId = liveStrategy.RolloutOptimizationRunId;
            decimal? originalEstimatedCapacityLots = liveStrategy.EstimatedCapacityLots;

            void RestorePreApprovalState()
            {
                run.Status = originalRunStatus;
                run.ApprovedAt = originalApprovedAt;
                run.CompletedAt = originalCompletedAt;
                run.ErrorMessage = originalErrorMessage;
                run.ValidationFollowUpsCreatedAt = originalValidationFollowUpsCreatedAt;
                run.ValidationFollowUpStatus = originalValidationFollowUpStatus;
                run.ApprovalEvaluatedAt = originalApprovalEvaluatedAt;

                liveStrategy.RollbackParametersJson = originalRollbackParametersJson;
                liveStrategy.ParametersJson = originalStrategyParametersJson ?? liveStrategy.ParametersJson;
                liveStrategy.RolloutPct = originalRolloutPct;
                liveStrategy.RolloutStartedAt = originalRolloutStartedAt;
                liveStrategy.RolloutOptimizationRunId = originalRolloutOptimizationRunId;
                liveStrategy.EstimatedCapacityLots = originalEstimatedCapacityLots;
            }

            if (string.IsNullOrEmpty(run.BestParametersJson))
            {
                _logger.LogError("OptimizationApprovalCoordinator: run {RunId} has null BestParametersJson — cannot start rollout", run.Id);
                return;
            }

            GradualRolloutManager.StartRollout(liveStrategy, run.BestParametersJson, run.Id, nowUtc, initialPct: 25);
            _logger.LogInformation(
                "OptimizationApprovalCoordinator: initiated gradual rollout for strategy {StrategyId} at 25% traffic",
                liveStrategy.Id);

            if (oosResult.TotalTrades > 0 && oosResult.Trades is not null && oosResult.Trades.Count >= 2)
            {
                var oosSpanDays = (oosResult.Trades[^1].ExitTime - oosResult.Trades[0].EntryTime).TotalDays;
                if (oosSpanDays > 0)
                {
                    double tradesPerDay = oosResult.TotalTrades / oosSpanDays;
                    decimal avgLotSize = oosResult.Trades.Average(t => t.LotSize);
                    liveStrategy.EstimatedCapacityLots = avgLotSize * (decimal)Math.Max(1.0, tradesPerDay);
                }
            }

            OptimizationConfig optimizationConfig = ctx.Config;
            bool followUpsAlreadyPresent = await _followUpCoordinator.EnsureValidationFollowUpsAsync(
                writeDb,
                run,
                strategy,
                optimizationConfig,
                runCt);
            if (followUpsAlreadyPresent)
                _metrics.OptimizationDuplicateFollowUpsPrevented.Add(1);

            if (currentRegime.HasValue)
            {
                await _artifactStore.SaveRegimeParamsAsync(
                    writeDb,
                    writeCtx,
                    strategy,
                    run,
                    vr.Winner.ParamsJson,
                    vr.OosHealthScore,
                    vr.CILower,
                    currentRegime.Value,
                    runCt,
                    persistChanges: false);
            }

            if (run.Status == OptimizationRunStatus.Running)
                OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Completed, nowUtc);
            else if (run.Status != OptimizationRunStatus.Completed)
                throw new InvalidOperationException(
                    $"Cannot approve optimization run {run.Id} from status {run.Status}; expected Completed.");

            OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Approved, nowUtc);
            run.ApprovalEvaluatedAt = nowUtc;
            var approvedEvent = new OptimizationApprovedIntegrationEvent
            {
                OptimizationRunId = run.Id,
                StrategyId = run.StrategyId,
                Symbol = strategy.Symbol,
                Timeframe = strategy.Timeframe,
                Improvement = improvement,
                OosScore = vr.OosHealthScore,
                ApprovedAt = run.ApprovedAt ?? nowUtc,
            };

            try
            {
                await eventService.SaveAndPublish(writeCtx, approvedEvent);
            }
            catch (DbUpdateException ex) when (OptimizationFollowUpCoordinator.IsDuplicateFollowUpConstraintViolation(ex))
            {
                OptimizationFollowUpCoordinator.DetachPendingValidationFollowUps(writeDb, run.Id);
                run.ValidationFollowUpsCreatedAt ??= nowUtc;
                run.ValidationFollowUpStatus ??= ValidationFollowUpStatus.Pending;
                _metrics.OptimizationDuplicateFollowUpsPrevented.Add(1);

                try
                {
                    await eventService.SaveAndPublish(writeCtx, approvedEvent);
                }
                catch (Exception retryEx)
                {
                    RestorePreApprovalState();
                    _artifactStore.RollbackTrackedArtifacts(writeDb, run.Id, strategy.Id);
                    OptimizationRunLifecycle.FailForRetry(
                        run,
                        $"Failed to persist approved optimization changes after duplicate follow-up retry: {retryEx.Message}",
                        OptimizationFailureCategory.Transient,
                        nowUtc);
                    await writeCtx.SaveChangesAsync(ct);
                    _logger.LogError(retryEx,
                        "OptimizationApprovalCoordinator: failed to persist approval for run {RunId} after duplicate follow-up retry — marked Failed for retry",
                        run.Id);
                    return;
                }
            }
            catch (Exception ex)
            {
                RestorePreApprovalState();
                _artifactStore.RollbackTrackedArtifacts(writeDb, run.Id, strategy.Id);
                OptimizationRunLifecycle.FailForRetry(
                    run,
                    $"Failed to persist approved optimization changes: {ex.Message}",
                    OptimizationFailureCategory.Transient,
                    nowUtc);
                await writeCtx.SaveChangesAsync(ct);
                _logger.LogError(ex,
                    "OptimizationApprovalCoordinator: failed to persist approval for run {RunId} — marked Failed for retry",
                    run.Id);
                return;
            }

            _metrics.OptimizationAutoApproved.Add(1);

            _logger.LogInformation(
                "OptimizationApprovalCoordinator: run {RunId} AUTO-APPROVED — improvement={Improvement:+0.00;-0.00}, OOS={Score:F2}, CI_lower={CIL:F2}, WF_Avg={WfAvg:F2}",
                run.Id,
                improvement,
                vr.OosHealthScore,
                vr.CILower,
                vr.WfAvgScore);

            try
            {
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType = "OptimizationRun",
                    EntityId = run.Id,
                    DecisionType = "AutoApproval",
                    Outcome = "Approved",
                    Reason = $"OOS={vr.OosHealthScore:F2}, CI95=[{vr.CILower:F2},{vr.CIUpper:F2}], WF={vr.WfAvgScore:F2}, Sens=pass, CostPess={vr.PessimisticScore:F2}; params applied",
                    Source = "OptimizationWorker"
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OptimizationApprovalCoordinator: approval audit log failed for run {RunId} (non-fatal)",
                    run.Id);
            }

            await _crossRegimePersistenceService.PersistAsync(
                run,
                strategy,
                config,
                currentRegime,
                candleLookbackStart,
                screeningOptions,
                vr.Winner.ParamsJson,
                db,
                writeDb,
                writeCtx,
                ct,
                runCt);
        }
        else
        {
            _metrics.OptimizationAutoRejected.Add(1);

            try
            {
                var failedCandidates = (vr.FailedCandidates ?? [])
                    .Select(f => new OptimizationApprovalReportParser.FailedCandidateDiagnostic(
                        f.Rank,
                        f.Params,
                        f.Reason,
                        f.Score))
                    .ToArray();

                if (failedCandidates.Length == 0)
                {
                    failedCandidates =
                    [
                        new OptimizationApprovalReportParser.FailedCandidateDiagnostic(
                            1,
                            vr.Winner.ParamsJson,
                            vr.FailureReason ?? "unknown",
                            vr.HasOosValidation ? vr.OosHealthScore : vr.Winner.HealthScore)
                    ];
                }

                run.ApprovalReportJson = OptimizationApprovalReportParser.SetManualReviewDiagnostics(
                    run.ApprovalReportJson,
                    failedCandidates,
                    failureReason,
                    vr.HasOosValidation ? vr.OosHealthScore : vr.Winner.HealthScore,
                    vr.HasOosValidation);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "OptimizationApprovalCoordinator: failed to persist rejection diagnostics for run {RunId}",
                    run.Id);
            }

            run.ApprovalEvaluatedAt = nowUtc;
            await writeCtx.SaveChangesAsync(ct);

            _logger.LogInformation(
                "OptimizationApprovalCoordinator: run {RunId} requires manual review — {Reason}",
                run.Id,
                failureReason);

            try
            {
                await mediator.Send(new LogDecisionCommand
                {
                    EntityType = "OptimizationRun",
                    EntityId = run.Id,
                    DecisionType = "AutoApproval",
                    Outcome = "ManualReviewRequired",
                    Reason = failureReason,
                    Source = "OptimizationWorker"
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OptimizationApprovalCoordinator: manual-review audit log failed for run {RunId} (non-fatal)",
                    run.Id);
            }

            try
            {
                await EscalateChronicFailuresAsync(
                    db,
                    writeDb,
                    writeCtx,
                    mediator,
                    alertDispatcher,
                    run.StrategyId,
                    strategy.Name,
                    config.MaxConsecutiveFailuresBeforeEscalation,
                    config.CooldownDays,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OptimizationApprovalCoordinator: chronic-failure escalation failed for run {RunId} (non-fatal)",
                    run.Id);
            }
        }
    }

    private async Task EscalateChronicFailuresAsync(
        DbContext db,
        DbContext writeDb,
        IWriteApplicationDbContext writeCtx,
        IMediator mediator,
        IAlertDispatcher alertDispatcher,
        long strategyId,
        string strategyName,
        int maxConsecutiveFailures,
        int baseCooldownDays,
        CancellationToken ct)
        => await _chronicFailureEscalator.EscalateAsync(
            db,
            writeDb,
            writeCtx,
            mediator,
            alertDispatcher,
            strategyId,
            strategyName,
            maxConsecutiveFailures,
            baseCooldownDays,
            ct);
}
