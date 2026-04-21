using System.Diagnostics;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Application.Common.WorkerGroups;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Slim orchestrator that processes queued <see cref="MLTrainingRun"/> records and drives
/// the first two stages of the ML lifecycle: <b>Train → Shadow evaluation</b>.
///
/// <para>
/// <b>ML lifecycle position:</b>
/// <code>
///   [MLTrainingWorker] → Train + quality gates → (pass) → promote + create shadow eval
///                                                         → [MLShadowArbiterWorker] decides promotion
///        ↑                                       (fail) → Failed run, alert if consecutive
///        └── [MLDriftMonitorWorker / MLCovariateShiftWorker] queue new runs when deployed model degrades
/// </code>
/// </para>
///
/// <para>
/// <b>Processing pipeline per run:</b>
/// <list type="number">
///   <item>Atomically claim a <see cref="MLTrainingRun"/> with status <see cref="RunStatus.Queued"/>
///         via a TOCTOU-safe <c>ExecuteUpdateAsync</c> claim — concurrent workers cannot claim the same run.</item>
///   <item>Load all hyperparameters from <see cref="EngineConfig"/>; apply per-run overrides from
///         <c>HyperparamConfigJson</c> (used by hyperparameter search workers).</item>
///   <item>Load candle OHLCV data and COT (Commitment of Traders) reports for the training window.</item>
///   <item>Apply a <b>freshness gate</b>: reject stale data if the newest candle is older than
///         <c>MLTraining:MaxCandleAgeMinutes</c>. Re-queues the run with a 30-minute delay.</item>
///   <item>Build training samples via <see cref="MLFeatureHelper"/> — either standard next-bar labels
///         or triple-barrier labels (configurable via <c>MLTraining:UseTripleBarrier</c>).</item>
///   <item>Apply a <b>label imbalance guard</b>: reject runs where buy/sell ratio exceeds
///         <c>MLTraining:MaxLabelImbalance</c> to avoid biased models.</item>
///   <item>Auto-select trainer architecture via <see cref="ITrainerSelector"/> unless the run
///         already specifies a non-default <see cref="LearnerArchitecture"/>.</item>
///   <item>Optionally warm-start from the previous champion's serialised weights when the trigger
///         is <see cref="TriggerType.AutoDegrading"/> (speeds re-convergence by ~30%).</item>
///   <item>Train with a configurable timeout (<c>MLTraining:TrainingTimeoutMinutes</c>).</item>
///   <item>Apply <b>multi-gate quality checks</b>: accuracy, expected value, Brier score, Sharpe
///         ratio, walk-forward std dev, ECE (calibration), Brier skill score, and OOB regression guard.</item>
///   <item>On pass: demote previous champion, persist new <see cref="MLModel"/>, create a
///         <see cref="MLShadowEvaluation"/> (challenger vs champion), publish
///         <see cref="MLModelActivatedIntegrationEvent"/>, write audit log.</item>
///   <item>Optionally train regime-specific sub-models when <c>MLTraining:EnableRegimeSpecificModels</c>
///         is true.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Concurrency guarantee:</b> the worker uses a per-process <see cref="_instanceId"/> Guid
/// embedded in the atomic claim UPDATE so multiple worker instances running in parallel cannot
/// process the same run. A <see cref="IDistributedLock"/> further prevents concurrent model
/// promotions from this worker and <see cref="MLShadowArbiterWorker"/> racing on the same symbol/timeframe.
/// </para>
///
/// <para>
/// <b>Retry policy:</b> transient failures re-queue the run with exponential back-off
/// (2<sup>n</sup> × 60 s, capped at 1 h). Cancellations (timeout or shutdown) re-queue
/// immediately without incrementing the attempt counter. After <c>MaxAttempts</c> failures,
/// an <see cref="Alert"/> of type <see cref="AlertType.MLModelDegraded"/> is raised.
/// </para>
///
/// <para>
/// <b>Polling interval:</b> configurable via <c>MLTraining:PollIntervalSeconds</c> (default 30 s).
/// </para>
/// </summary>
public sealed class MLTrainingWorker : BackgroundService
{
    // ── Per-process identity (TOCTOU-safe atomic claim) ───────────────────────
    private static readonly Guid _instanceId = Guid.NewGuid();

    /// <summary>
    /// Minutes after which a <see cref="RunStatus.Running"/> claim from another worker
    /// instance is considered orphaned (e.g. due to crash/OOM) and eligible for recovery.
    /// Set to 2× the default training timeout to avoid prematurely reclaiming slow runs.
    /// </summary>
    private const int StaleClaimMinutes = 60;

    /// <summary>
    /// Default starvation deadline in minutes. Any <see cref="RunStatus.Queued"/> run whose
    /// <see cref="MLTrainingRun.StartedAt"/> is older than this horizon is claimed ahead of
    /// the normal priority order, guaranteeing no run waits longer than the deadline
    /// regardless of how many higher-priority runs keep arriving. Prevents the starvation
    /// failure mode observed on 2026-04-15 where continuous Priority=1 AutoDegrading runs
    /// for USDJPY/M5 starved Priority=5 runs for GBPUSD/M15 and EURUSD/H1 for 5+ hours.
    /// Configurable via EngineConfig key <c>MLTraining:StarvationDeadlineMinutes</c>;
    /// set the config value to 0 to disable starvation rescue entirely and revert to
    /// strict priority ordering.
    /// </summary>
    private const int StarvationDeadlineMinutesDefault = 240;

    // ── EngineConfig keys ─────────────────────────────────────────────────────
    private const string CK_PollSecs                    = "MLTraining:PollIntervalSeconds";
    private const string CK_K                     = "MLTraining:K";
    private const string CK_LR                    = "MLTraining:LearningRate";
    private const string CK_L2                    = "MLTraining:L2Lambda";
    private const string CK_MaxEpochs             = "MLTraining:MaxEpochs";
    private const string CK_ESPatience            = "MLTraining:EarlyStoppingPatience";
    private const string CK_MinAccuracy           = "MLTraining:MinAccuracyToPromote";
    private const string CK_MinEV                 = "MLTraining:MinExpectedValue";
    private const string CK_MaxBrier              = "MLTraining:MaxBrierScore";
    private const string CK_MinSharpe             = "MLTraining:MinSharpeRatio";
    private const string CK_MinSamples            = "MLTraining:MinSamples";
    private const string CK_ShadowTrades          = "MLTraining:ShadowRequiredTrades";
    private const string CK_ShadowExpiry          = "MLTraining:ShadowExpiryDays";
    private const string CK_WFolds                = "MLTraining:WalkForwardFolds";
    private const string CK_Embargo               = "MLTraining:EmbargoBarCount";
    private const string CK_Timeout               = "MLTraining:TrainingTimeoutMinutes";
    private const string CK_DecayLambda           = "MLTraining:TemporalDecayLambda";
    private const string CK_DriftWindowDays       = "MLTraining:DriftWindowDays";
    private const string CK_DriftMinPredictions   = "MLTraining:DriftMinPredictions";
    private const string CK_DriftAccThreshold     = "MLTraining:DriftAccuracyThreshold";
    private const string CK_ConsecFailThreshold   = "MLTraining:ConsecutiveFailureAlertThreshold";
    private const string CK_AlertDestination      = "MLTraining:AlertDestination";
    private const string CK_MaxWfStdDev           = "MLTraining:MaxWalkForwardStdDev";
    private const string CK_LabelSmoothing        = "MLTraining:LabelSmoothing";
    private const string CK_MinFeatureImportance  = "MLTraining:MinFeatureImportance";
    private const string CK_EnableRegimeModels         = "MLTraining:EnableRegimeSpecificModels";
    private const string CK_FeatureSampleRatio         = "MLTraining:FeatureSampleRatio";
    private const string CK_MaxEce                     = "MLTraining:MaxEce";
    private const string CK_UseTripleBarrier           = "MLTraining:UseTripleBarrier";
    private const string CK_UseExtendedFeatureVector   = "MLTraining:UseExtendedFeatureVector";
    private const string CK_UseEventFeatureVector      = "MLTraining:UseEventFeatureVector";
    private const string CK_StarvationDeadline         = "MLTraining:StarvationDeadlineMinutes";
    private const string CK_TripleBarrierProfitAtrMult = "MLTraining:TripleBarrierProfitAtrMult";
    private const string CK_TripleBarrierStopAtrMult   = "MLTraining:TripleBarrierStopAtrMult";
    private const string CK_TripleBarrierHorizonBars   = "MLTraining:TripleBarrierHorizonBars";
    private const string CK_NoiseSigma                 = "MLTraining:NoiseSigma";
    private const string CK_FpCostWeight               = "MLTraining:FpCostWeight";
    private const string CK_NclLambda                  = "MLTraining:NclLambda";
    private const string CK_SwaStartEpoch              = "MLTraining:SwaStartEpoch";
    private const string CK_SwaFrequency               = "MLTraining:SwaFrequency";
    private const string CK_MixupAlpha                 = "MLTraining:MixupAlpha";
    private const string CK_EnableGes                  = "MLTraining:EnableGreedyEnsembleSelection";
    private const string CK_MaxGradNorm                = "MLTraining:MaxGradNorm";
    private const string CK_AtrLabelSensitivity        = "MLTraining:AtrLabelSensitivity";
    private const string CK_ShadowMinZScore            = "MLTraining:ShadowMinZScore";
    private const string CK_L1Lambda                   = "MLTraining:L1Lambda";
    private const string CK_MagnitudeQuantileTau       = "MLTraining:MagnitudeQuantileTau";
    private const string CK_MagLossWeight              = "MLTraining:MagLossWeight";
    private const string CK_DensityRatioWindowDays     = "MLTraining:DensityRatioWindowDays";
    private const string CK_DurbinWatsonThreshold      = "MLTraining:DurbinWatsonThreshold";
    private const string CK_AdaptiveLrDecayFactor      = "MLTraining:AdaptiveLrDecayFactor";
    private const string CK_OobPruningEnabled          = "MLTraining:OobPruningEnabled";
    private const string CK_MutualInfoRedundancyThr    = "MLTraining:MutualInfoRedundancyThreshold";
    private const string CK_MinSharpeTrendSlope        = "MLTraining:MinSharpeTrendSlope";
    private const string CK_MinF1                      = "MLTraining:MinF1Score";
    private const string CK_UseClassWeights            = "MLTraining:UseClassWeights";
    private const string CK_TrendingMinAccuracy        = "MLTraining:TrendingMinAccuracy";
    private const string CK_TrendingMinEV              = "MLTraining:TrendingMinEV";
    private const string CK_SelfTuningEnabled          = "MLTraining:SelfTuningEnabled";
    private const string CK_MaxSelfTuningRetries       = "MLTraining:MaxSelfTuningRetries";
    private const string CK_BlockedArchitectures       = "MLTraining:BlockedArchitectures";
    private const string CK_FitTemperatureScale        = "MLTraining:FitTemperatureScale";
    private const string CK_MinBrierSkillScore         = "MLTraining:MinBrierSkillScore";
    private const string CK_RecalibrationDecayLambda   = "MLTraining:RecalibrationDecayLambda";
    private const string CK_MaxEnsembleDiversity       = "MLTraining:MaxEnsembleDiversity";
    private const string CK_UseSymmetricCE             = "MLTraining:UseSymmetricCE";
    private const string CK_SymmetricCeAlpha           = "MLTraining:SymmetricCeAlpha";
    private const string CK_DiversityLambda            = "MLTraining:DiversityLambda";
    private const string CK_UseAdaptiveLabelSmoothing  = "MLTraining:UseAdaptiveLabelSmoothing";
    private const string CK_AgeDecayLambda             = "MLTraining:AgeDecayLambda";
    private const string CK_UseCovariateShiftWeights   = "MLTraining:UseCovariateShiftWeights";
    private const string CK_MaxBadFoldFraction         = "MLTraining:MaxBadFoldFraction";
    private const string CK_MinQualityRetentionRatio   = "MLTraining:MinQualityRetentionRatio";
    private const string CK_MaxCandleAgeMinutes        = "MLTraining:MaxCandleAgeMinutes";
    private const string CK_MaxLabelImbalance          = "MLTraining:MaxLabelImbalance";

    // ── DI ────────────────────────────────────────────────────────────────────
    private readonly IServiceScopeFactory     _scopeFactory;
    private readonly ILogger<MLTrainingWorker> _logger;
    private readonly IDistributedLock          _distributedLock;

    public MLTrainingWorker(
        IServiceScopeFactory       scopeFactory,
        ILogger<MLTrainingWorker>  logger,
        IDistributedLock           distributedLock)
    {
        _scopeFactory    = scopeFactory;
        _logger          = logger;
        _distributedLock = distributedLock;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield immediately so the host can continue starting other hosted services.
        // Without this, ExecuteAsync blocks IHost.StartAsync until the first real await.
        await Task.Yield();

        _logger.LogInformation("MLTrainingWorker started. InstanceId={Id}", _instanceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 30;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx = db.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 30, stoppingToken);

                // ── Concurrency guard — prevent deadlock from too many concurrent runs ──
                int maxConcurrent = await GetConfigAsync<int>(ctx, "MLTraining:MaxConcurrentRuns", 10, stoppingToken);
                int currentRunning = await ctx.Set<MLTrainingRun>()
                    .CountAsync(r => r.Status == RunStatus.Running && !r.IsDeleted, stoppingToken);
                if (currentRunning >= maxConcurrent)
                {
                    _logger.LogDebug(
                        "Concurrency limit reached ({Running}/{Max}). Waiting for slots.",
                        currentRunning, maxConcurrent);
                    await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
                    continue;
                }

                // ── Claim one queued run atomically ──────────────────────────
                var run = await ClaimNextRunAsync(ctx, stoppingToken);
                if (run is null)
                {
                    _logger.LogDebug("No queued ML runs. Sleeping {Secs}s.", pollSecs);
                    await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "Claimed run Id={RunId} Symbol={Symbol} Timeframe={Tf} Trigger={Trigger}",
                    run.Id, run.Symbol, run.Timeframe, run.TriggerType);

                // Bulkhead: limit concurrent CPU-bound training to prevent thread pool starvation
                await WorkerBulkhead.MLTraining.WaitAsync(stoppingToken);
                try
                {
                    // Per-job timeout: prevent individual training runs from running forever.
                    // FtTransformer/TCN can get stuck in infinite loops with certain hyperparams.
                    int timeoutMinutes = await GetConfigAsync<int>(ctx, "MLTraining:MaxRunTimeMinutes", 30, stoppingToken);
                    using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    jobCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));

                    try
                    {
                        await ProcessRunAsync(run, db, ctx, scope.ServiceProvider, jobCts.Token);
                    }
                    catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "Run {RunId} ({Symbol}/{Tf}) timed out after {Timeout} minutes — marking as failed",
                            run.Id, run.Symbol, run.Timeframe, timeoutMinutes);

                        run.Status = RunStatus.Failed;
                        run.ErrorMessage = $"Timed out after {timeoutMinutes} minutes";
                        run.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                }
                finally
                {
                    WorkerBulkhead.MLTraining.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLTrainingWorker loop error");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(pollSecs, 60)), stoppingToken);
            }
        }

        _logger.LogInformation("MLTrainingWorker stopping. InstanceId={Id}", _instanceId);
    }

    // ── Run claiming ──────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically claims the next queued <see cref="MLTrainingRun"/> for this worker instance.
    ///
    /// <para>
    /// Uses a single <c>ExecuteUpdateAsync</c> UPDATE that stamps the row with
    /// <see cref="_instanceId"/> and flips the status to <see cref="RunStatus.Running"/>
    /// in one round-trip. Because only one UPDATE can win the race for a given row,
    /// no two worker instances will claim the same run even when running concurrently
    /// (TOCTOU-safe — avoids the check-then-act race that would occur with a
    /// SELECT + separate UPDATE pattern).
    /// </para>
    ///
    /// <para>
    /// The subsequent SELECT reads back the row by matching both <see cref="_instanceId"/>
    /// and a 10-second recency window on <c>PickedUpAt</c> so we get the exact run object
    /// that was claimed, even if several runs were in the queue.
    /// </para>
    ///
    /// Runs with a future <c>NextRetryAt</c> (back-off delay after failure) are skipped
    /// until the back-off period has elapsed.
    /// </summary>
    /// <returns>The claimed <see cref="MLTrainingRun"/>, or <c>null</c> if the queue is empty.</returns>
    /// <summary>
    /// Produces a deterministic <see cref="Guid"/> for tournament grouping so that all
    /// shadow evaluations created for the same champion within the same UTC hour share
    /// one group. Uses the champion model ID and the truncated-to-hour UTC timestamp.
    /// </summary>
    private static Guid DeterministicTournamentGroup(long championModelId)
    {
        var now = DateTime.UtcNow;
        // Truncate to hour so all shadows created within the same hour share a group
        var hourKey = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), championModelId);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), hourKey.Ticks);
        return new Guid(bytes);
    }

    private async Task<MLTrainingRun?> ClaimNextRunAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        var runSet = ctx.Set<MLTrainingRun>();

        // ── Recover orphaned runs ─────────────────────────────────────────
        // If a worker process crashes (kill -9, OOM, etc.), its claimed run stays
        // in Running with a WorkerInstanceId that no longer exists. Reclaim any
        // run that has been Running for longer than StaleClaimThreshold by resetting
        // it to Queued so it can be picked up on the next cycle.
        var staleThreshold = DateTime.UtcNow.AddMinutes(-StaleClaimMinutes);
        await runSet
            .Where(r => r.Status           == RunStatus.Running &&
                        r.WorkerInstanceId != null              &&
                        r.WorkerInstanceId != _instanceId       &&
                        r.PickedUpAt       != null              &&
                        r.PickedUpAt       < staleThreshold)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status,           RunStatus.Queued)
                .SetProperty(r => r.WorkerInstanceId, (Guid?)null)
                .SetProperty(r => r.ErrorMessage,     "Re-queued: previous worker instance did not complete (stale claim recovery)."),
                ct);

        // ── Improvement #11: Systemic pause check ────────────────────────
        // When correlated failure is detected, only process emergency/drift runs
        var systemicPause = await ctx.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:SystemicPauseActive" && !c.IsDeleted)
            .Select(c => c.Value).FirstOrDefaultAsync(ct);
        bool isPaused = systemicPause == "true" || systemicPause == "1";

        // ── Starvation-aware first pass ──────────────────────────────────
        // Runs with lower priorities can be starved indefinitely when a steady
        // stream of high-priority runs keeps arriving (observed with USDJPY/M5
        // AutoDegrading flooding the queue and locking out GBPUSD/M15 Scheduled
        // runs for 5+ hours). Before the normal priority-ordered claim, do a
        // separate pass that picks up any run whose StartedAt is older than the
        // configured deadline, regardless of its priority. Guarantees no run
        // waits longer than StarvationDeadlineMinutes no matter how busy the
        // higher-priority lanes are.
        //
        // Setting the config value to 0 disables this rescue path entirely and
        // reverts to strict priority ordering — useful if you want to
        // deliberately pause low-priority lanes during an emergency.
        var starvationMinutesStr = await ctx.Set<EngineConfig>()
            .Where(c => c.Key == CK_StarvationDeadline && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        int starvationMinutes = StarvationDeadlineMinutesDefault;
        if (int.TryParse(starvationMinutesStr, out var parsedStarvation) && parsedStarvation >= 0)
            starvationMinutes = parsedStarvation;

        if (starvationMinutes > 0)
        {
            var starvationCutoff = DateTime.UtcNow.AddMinutes(-starvationMinutes);

            var starvedRescued = await runSet
                .Where(r => r.Status == RunStatus.Queued && r.WorkerInstanceId == null &&
                            r.StartedAt < starvationCutoff &&
                            (r.NextRetryAt == null || r.NextRetryAt <= DateTime.UtcNow) &&
                            (!isPaused || r.Priority <= 1 || r.IsEmergencyRetrain))
                .OrderBy(r => r.StartedAt)
                .Take(1)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status,           RunStatus.Running)
                    .SetProperty(r => r.PickedUpAt,       DateTime.UtcNow)
                    .SetProperty(r => r.WorkerInstanceId, _instanceId),
                    ct);

            if (starvedRescued > 0)
            {
                var rescued = await runSet.FirstOrDefaultAsync(
                    r => r.WorkerInstanceId == _instanceId &&
                         r.Status           == RunStatus.Running,
                    ct);

                if (rescued is not null)
                {
                    var waitedMinutes = (DateTime.UtcNow - rescued.StartedAt).TotalMinutes;
                    _logger.LogInformation(
                        "ClaimNextRunAsync: starvation rescue — claiming run {RunId} ({Symbol}/{Tf}, priority={Priority}) " +
                        "after waiting {WaitedMin:F0}m (deadline {Deadline}m)",
                        rescued.Id, rescued.Symbol, rescued.Timeframe, rescued.Priority,
                        waitedMinutes, starvationMinutes);
                }

                return rescued;
            }
        }

        // ── Normal priority-ordered claim ────────────────────────────────
        // Improvement #9: Order by Priority first (lower = higher priority),
        // then by StartedAt for FIFO within the same priority level.
        // During systemic pause, only claim fast-lane runs (Priority <= 1).
        var rowsUpdated = await runSet
            .Where(r => r.Status == RunStatus.Queued && r.WorkerInstanceId == null &&
                        (r.NextRetryAt == null || r.NextRetryAt <= DateTime.UtcNow) &&
                        (!isPaused || r.Priority <= 1 || r.IsEmergencyRetrain))
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.StartedAt)
            .Take(1)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status,           RunStatus.Running)
                .SetProperty(r => r.PickedUpAt,       DateTime.UtcNow)
                .SetProperty(r => r.WorkerInstanceId, _instanceId),
                ct);

        if (rowsUpdated == 0)
            return null;

        // Use WorkerInstanceId alone to find the claimed run — the 10-second recency
        // window was too fragile (GC pauses, slow DB could cause false misses).
        // The combination of WorkerInstanceId + Running status is unique enough.
        return await runSet.FirstOrDefaultAsync(
            r => r.WorkerInstanceId == _instanceId &&
                 r.Status           == RunStatus.Running,
            ct);
    }

    // ── Main per-run processing ───────────────────────────────────────────────

    /// <summary>
    /// Executes all steps for a single claimed <see cref="MLTrainingRun"/>: data loading,
    /// feature engineering, training, quality gate evaluation, model promotion, shadow
    /// evaluation creation, audit logging, and event publishing.
    ///
    /// <para>
    /// On cancellation (timeout or host shutdown) the run is re-queued immediately
    /// without incrementing <c>AttemptCount</c> — cancellations are not ML failures.
    /// </para>
    ///
    /// On transient exception the run is re-queued with exponential back-off
    /// (2<sup>n</sup> × 60 s, max 1 h). After <c>MaxAttempts</c> exceptions the run
    /// is permanently marked <see cref="RunStatus.Failed"/> and an alert is created.
    /// </summary>
    /// <param name="run">The claimed training run to process.</param>
    /// <param name="db">Write DB context for persisting the run, new model, and shadow eval.</param>
    /// <param name="ctx">The underlying EF <see cref="DbContext"/> for direct set access.</param>
    /// <param name="sp">Scoped service provider — used to resolve <see cref="IMLModelTrainer"/>,
    ///   <see cref="IMediator"/>, and <see cref="IIntegrationEventService"/>.</param>
    /// <param name="stoppingToken">Cancellation token from <see cref="BackgroundService"/>.</param>
    private async Task ProcessRunAsync(
        MLTrainingRun                           run,
        IWriteApplicationDbContext              db,
        Microsoft.EntityFrameworkCore.DbContext ctx,
        IServiceProvider                        sp,
        CancellationToken                       stoppingToken)
    {
        var sw = Stopwatch.StartNew();

        var mediator = sp.GetRequiredService<IMediator>();
        IIntegrationEventService? eventService = null;
        try { eventService = sp.GetService<IIntegrationEventService>(); } catch { /* optional */ }

        try
        {
            // ── Load hyperparams from EngineConfig ───────────────────────────
            var hp = await LoadHyperparamsAsync(ctx, stoppingToken);

            // ── Apply per-run hyperparameter overrides (hyperparameter search) ─
            if (!string.IsNullOrWhiteSpace(run.HyperparamConfigJson))
            {
                try
                {
                    var overrides = JsonSerializer.Deserialize<HyperparamOverrides>(run.HyperparamConfigJson);
                    if (overrides is not null)
                    {
                        hp = hp with
                        {
                            K                          = overrides.K                          ?? hp.K,
                            LearningRate               = overrides.LearningRate               ?? hp.LearningRate,
                            L2Lambda                   = overrides.L2Lambda                   ?? hp.L2Lambda,
                            TemporalDecayLambda        = overrides.TemporalDecayLambda        ?? hp.TemporalDecayLambda,
                            MaxEpochs                  = overrides.MaxEpochs                  ?? hp.MaxEpochs,
                            EmbargoBarCount            = overrides.EmbargoBarCount            ?? hp.EmbargoBarCount,
                            FpCostWeight               = overrides.FpCostWeight               ?? hp.FpCostWeight,
                            UseClassWeights            = overrides.UseClassWeights            ?? hp.UseClassWeights,
                            UseTripleBarrier           = overrides.UseTripleBarrier           ?? hp.UseTripleBarrier,
                            TripleBarrierProfitAtrMult = overrides.TripleBarrierProfitAtrMult ?? hp.TripleBarrierProfitAtrMult,
                            TripleBarrierStopAtrMult   = overrides.TripleBarrierStopAtrMult   ?? hp.TripleBarrierStopAtrMult,
                            LabelSmoothing             = overrides.LabelSmoothing             ?? hp.LabelSmoothing,
                            NoiseSigma                 = overrides.NoiseSigma                 ?? hp.NoiseSigma,
                            FtTransformerHeads         = overrides.FtTransformerHeads         ?? hp.FtTransformerHeads,
                            FtTransformerArchitectureNumLayers =
                                overrides.FtTransformerNumLayers ?? hp.FtTransformerArchitectureNumLayers,
                        };
                        _logger.LogInformation(
                            "Run {RunId}: applied hyperparameter overrides from HyperparamConfigJson " +
                            "(K={K} lr={LR} l2={L2} decay={Dec} epochs={Ep} embargo={Emb})",
                            run.Id, hp.K, hp.LearningRate, hp.L2Lambda,
                            hp.TemporalDecayLambda, hp.MaxEpochs, hp.EmbargoBarCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Run {RunId}: failed to parse HyperparamConfigJson — using defaults", run.Id);
                }
            }

            // ── Cold-start quality gate relaxation ───────────────────────────
            // When NO active MLModel exists for this (Symbol, Timeframe), the
            // normal quality gates — tuned for beating an existing champion —
            // are too strict to ever admit the first model. The strict gates
            // (MinAccuracy 0.55, MinSharpe 0.50, MaxBrier 0.25, MinF1 0.10)
            // reject weak baselines that are still useful as starting points
            // the shadow-arbiter can subsequently challenge and improve.
            // Relax to "barely above random" thresholds for the first model
            // only — downstream SignalValidator + RiskChecker layers still
            // catch any bad trades the weak baseline generates.
            bool coldStartModeEnabled = await GetConfigAsync<bool>(
                ctx, "MLTraining:ColdStart:Enabled", true, stoppingToken);

            bool hasActiveModel = await ctx.Set<MLModel>()
                .AsNoTracking()
                .AnyAsync(m => m.Symbol    == run.Symbol
                            && m.Timeframe == run.Timeframe
                            && m.IsActive
                            && !m.IsDeleted, stoppingToken);

            bool coldStart = coldStartModeEnabled && !hasActiveModel;
            if (coldStart)
            {
                double coldMinAccuracy    = await GetConfigAsync<double>(ctx, "MLTraining:ColdStart:MinAccuracy",    0.52, stoppingToken);
                // MinSharpe lowered from 0.30 to 0.05: the first model per combo only
                // needs to avoid losing money. "Slightly positive expectation" is the
                // right floor for a baseline the shadow-arbiter will later challenge
                // with better candidates. 0.30 was empirically too strict — today's
                // GBPUSD/M15 run came in at 0.10, a real but marginal edge, and the
                // 0.30 floor rejected it.
                double coldMinSharpe      = await GetConfigAsync<double>(ctx, "MLTraining:ColdStart:MinSharpe",      0.05, stoppingToken);
                double coldMaxBrier       = await GetConfigAsync<double>(ctx, "MLTraining:ColdStart:MaxBrier",       0.30, stoppingToken);
                double coldMinEv          = await GetConfigAsync<double>(ctx, "MLTraining:ColdStart:MinExpectedValue", -0.005, stoppingToken);
                double coldMinF1          = await GetConfigAsync<double>(ctx, "MLTraining:ColdStart:MinF1",          0.00, stoppingToken);
                double coldMaxWfStdDev    = await GetConfigAsync<double>(ctx, "MLTraining:ColdStart:MaxWfStdDev",    0.25, stoppingToken);

                var originalHp = hp;
                hp = hp with
                {
                    MinAccuracyToPromote = Math.Min(originalHp.MinAccuracyToPromote, coldMinAccuracy),
                    MinSharpeRatio       = Math.Min(originalHp.MinSharpeRatio,       coldMinSharpe),
                    MaxBrierScore        = Math.Max(originalHp.MaxBrierScore,        coldMaxBrier),
                    MinExpectedValue     = Math.Min(originalHp.MinExpectedValue,     coldMinEv),
                    MinF1Score           = Math.Min(originalHp.MinF1Score,           coldMinF1),
                    MaxWalkForwardStdDev = Math.Max(originalHp.MaxWalkForwardStdDev, coldMaxWfStdDev),
                };

                _logger.LogInformation(
                    "Run {RunId} ({Symbol}/{Tf}): COLD START mode — no active model exists. " +
                    "Gates relaxed: acc {OrigAcc:P1}→{NewAcc:P1}, sharpe {OrigSh:F2}→{NewSh:F2}, " +
                    "brier {OrigBr:F2}→{NewBr:F2}, ev {OrigEv:F4}→{NewEv:F4}, f1 {OrigF1:F2}→{NewF1:F2}, " +
                    "wfStd {OrigWf:P0}→{NewWf:P0}",
                    run.Id, run.Symbol, run.Timeframe,
                    originalHp.MinAccuracyToPromote, hp.MinAccuracyToPromote,
                    originalHp.MinSharpeRatio, hp.MinSharpeRatio,
                    originalHp.MaxBrierScore, hp.MaxBrierScore,
                    originalHp.MinExpectedValue, hp.MinExpectedValue,
                    originalHp.MinF1Score, hp.MinF1Score,
                    originalHp.MaxWalkForwardStdDev, hp.MaxWalkForwardStdDev);
            }

            // ── Guard: skip meta-learner runs with Symbol="ALL" ──────────────
            // These are queued by drift/PSI/calibration workers that blindly mirror
            // the MLModel.Symbol of every model they monitor, including MAML cross-
            // symbol initialisers whose Symbol="ALL" is a sentinel rather than a real
            // instrument. Mark as Completed (not Failed) so they don't pollute the
            // MLTrainingRunHealthWorker 100%-failure-rate alerts, and log at Debug
            // since the skip is expected and non-actionable.
            if (string.Equals(run.Symbol, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                run.Status       = RunStatus.Completed;
                run.CompletedAt  = DateTime.UtcNow;
                run.ErrorMessage = "Skipped: Symbol 'ALL' is a meta-learner sentinel; MAML models are rebuilt by MLAverageWeightInitWorker.";
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogDebug("Run {RunId}: skipping meta-learner run with Symbol='ALL'.", run.Id);
                return;
            }

            // ── Load candle data ─────────────────────────────────────────────
            var candles = await ctx.Set<Candle>()
                .Where(c => c.Symbol    == run.Symbol &&
                            c.Timeframe == run.Timeframe &&
                            c.Timestamp >= run.FromDate &&
                            c.Timestamp <= run.ToDate)
                .OrderBy(c => c.Timestamp)
                .AsNoTracking()
                .ToListAsync(stoppingToken);

            _logger.LogInformation(
                "Loaded {Count} candles for {Symbol}/{Tf}", candles.Count, run.Symbol, run.Timeframe);

            int minRequired = hp.MinSamples + MLFeatureHelper.LookbackWindow;
            if (candles.Count < minRequired)
                throw new InvalidOperationException(
                    $"Insufficient candles: {candles.Count} (need {minRequired})");

            // ── Training data quality validation ────────────────────────────
            var dataWarnings = ValidateCandleData(candles);
            if (dataWarnings.Count > 0)
            {
                double failPct = (double)dataWarnings.Count / candles.Count;
                var reasonSummary = string.Join("; ", dataWarnings
                    .GroupBy(w => w.Reason)
                    .Select(g => $"{g.Key}={g.Count()}"));

                if (failPct > 0.05)
                {
                    // >5% flagged — abort training
                    run.Status      = RunStatus.Failed;
                    run.CompletedAt = DateTime.UtcNow;
                    run.ErrorMessage =
                        $"Training data quality check failed: {dataWarnings.Count}/{candles.Count} candles flagged — {reasonSummary}";
                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogError(
                        "Run {RunId}: data quality check FAILED — {Flagged}/{Total} ({Pct:P1}) candles flagged: {Reasons}",
                        run.Id, dataWarnings.Count, candles.Count, failPct, reasonSummary);
                    return;
                }

                // 1-5% flagged — warn but continue
                _logger.LogWarning(
                    "Run {RunId}: data quality warnings — {Flagged}/{Total} ({Pct:P1}) candles flagged: {Reasons}",
                    run.Id, dataWarnings.Count, candles.Count, failPct, reasonSummary);
            }

            // ── Freshness gate: reject stale data before training ────────────
            int maxCandleAgeMinutes = await GetConfigAsync<int>(ctx, CK_MaxCandleAgeMinutes, 0, stoppingToken);
            if (maxCandleAgeMinutes > 0)
            {
                var latestCandleTs = await ctx.Set<Candle>()
                    .Where(c => c.Symbol == run.Symbol && c.Timeframe == run.Timeframe)
                    .MaxAsync(c => (DateTime?)c.Timestamp, stoppingToken);

                if (latestCandleTs.HasValue)
                {
                    double ageMinutes = (DateTime.UtcNow - latestCandleTs.Value).TotalMinutes;
                    if (ageMinutes > maxCandleAgeMinutes)
                    {
                        // Freshness bounces should NOT consume the regular AttemptCount
                        // budget — slow data feeds shouldn't prevent training once data
                        // arrives. Instead, use a calendar-time cutoff: if the run has been
                        // bouncing for more than 24 hours since creation, fail permanently.
                        double hoursSinceCreation = (DateTime.UtcNow - run.StartedAt).TotalHours;
                        if (hoursSinceCreation > 24)
                        {
                            run.Status      = RunStatus.Failed;
                            run.CompletedAt = DateTime.UtcNow;
                            run.ErrorMessage =
                                $"Freshness gate: latest {run.Symbol}/{run.Timeframe} candle is " +
                                $"{ageMinutes:F0} min old (threshold: {maxCandleAgeMinutes} min). " +
                                $"Permanently failed — run has been waiting for fresh data for {hoursSinceCreation:F1} hours.";
                            await db.SaveChangesAsync(stoppingToken);
                            _logger.LogError(
                                "Run {RunId}: freshness gate permanently failed after {Hours:F1}h of waiting.",
                                run.Id, hoursSinceCreation);
                            await MaybeCreateTrainingFailureAlertAsync(ctx, run, stoppingToken);
                            return;
                        }

                        run.Status           = RunStatus.Queued;
                        run.WorkerInstanceId = null;
                        run.NextRetryAt      = DateTime.UtcNow.AddMinutes(30);
                        run.ErrorMessage     =
                            $"Freshness gate: latest {run.Symbol}/{run.Timeframe} candle is " +
                            $"{ageMinutes:F0} min old (threshold: {maxCandleAgeMinutes} min). " +
                            $"Re-queued (waiting {hoursSinceCreation:F1}h / 24h max).";
                        await db.SaveChangesAsync(stoppingToken);
                        _logger.LogWarning(
                            "Run {RunId}: stale data gate — latest {Symbol}/{Tf} candle is {Age:F0} min old " +
                            "(threshold: {Max} min). Re-queuing with 30 min delay (waiting {Hours:F1}h / 24h max).",
                            run.Id, run.Symbol, run.Timeframe, ageMinutes, maxCandleAgeMinutes,
                            hoursSinceCreation);
                        return;
                    }
                }
            }

            // ── Load COT data for base currency ─────────────────────────────
            // COT (Commitment of Traders) reports capture non-commercial (speculative)
            // positioning in currency futures, published weekly by the CFTC. The base
            // currency is extracted from the first 3 characters of the symbol (e.g. "EUR"
            // from "EURUSD") and used to look up the relevant currency series.
            //
            // COT features are incorporated as two normalised inputs in the feature vector:
            //   - Net non-commercial positioning (normalised to [-3, +3] z-range)
            //   - Weekly change in net positioning (momentum signal, same range)
            //
            // The min/max bounds computed here over the training window are stored in the
            // ModelSnapshot (CotNetNormMin/Max, CotMomNormMin/Max) so that the same
            // normalisation is applied consistently at inference time by MLSignalScorer.
            var baseCurrency = run.Symbol.Length >= 3 ? run.Symbol[..3] : run.Symbol;
            var cotReports = await ctx.Set<COTReport>()
                .Where(c => c.Currency == baseCurrency)
                .OrderBy(c => c.ReportDate)
                .AsNoTracking()
                .ToListAsync(stoppingToken);

            // Compute training-window COT min/max bounds for consistent inference normalisation.
            // Defaults cover typical speculative positioning ranges; guard against degenerate
            // (flat) series where all values are identical to avoid division-by-zero.
            float cotNetMin = -300_000f, cotNetMax = 300_000f;
            float cotMomMin = -30_000f,  cotMomMax = 30_000f;
            if (cotReports.Count > 0)
            {
                cotNetMin = (float)cotReports.Min(c => (double)c.NetNonCommercialPositioning);
                cotNetMax = (float)cotReports.Max(c => (double)c.NetNonCommercialPositioning);
                cotMomMin = (float)cotReports.Min(c => (double)c.NetPositioningChangeWeekly);
                cotMomMax = (float)cotReports.Max(c => (double)c.NetPositioningChangeWeekly);
                // Guard against degenerate (flat) series
                if (cotNetMax - cotNetMin < 1f) { cotNetMin = -300_000f; cotNetMax = 300_000f; }
                if (cotMomMax - cotMomMin < 1f) { cotMomMin = -30_000f;  cotMomMax = 30_000f;  }
            }

            CotFeatureEntry CotLookup(DateTime ts)
            {
                if (cotReports.Count == 0) return CotFeatureEntry.Zero;

                var report = cotReports.LastOrDefault(c => c.ReportDate <= ts);
                if (report is null) return CotFeatureEntry.Zero;

                float netRange = cotNetMax - cotNetMin;
                float momRange = cotMomMax - cotMomMin;
                float netNorm  = MLFeatureHelper.Clamp(
                    ((float)(double)report.NetNonCommercialPositioning - cotNetMin) / netRange * 6f - 3f, -3f, 3f);
                float momentum = MLFeatureHelper.Clamp(
                    ((float)(double)report.NetPositioningChangeWeekly  - cotMomMin) / momRange * 6f - 3f, -3f, 3f);
                return new CotFeatureEntry(netNorm, momentum);
            }

            // ── Build training samples ───────────────────────────────────────
            // Option 1 (vector versioning): when MLTraining:UseExtendedFeatureVector
            // is true, pre-load H1 closes for the G10 USD basket covering the
            // training window so each sample can compute point-in-time macro
            // features (carry, safe-haven, DXY, correlation stress) without
            // per-sample DB round trips. The trainer then sees a 37-element
            // vector instead of 33, and CompositeMLEvaluator dispatches on the
            // model's stored feature count at inference time.
            // Default flipped to true: V2 (37 features with cross-pair macro: PairCarryProxy,
            // SafeHavenIndex, DollarStrengthComposite, CrossPairCorrelationStress) strictly
            // enriches V1 without semantic change. Operators wanting V1-only behaviour can
            // opt out via MLTraining:UseExtendedFeatureVector=false.
            bool useExtendedVector = await GetConfigAsync<bool>(
                ctx, CK_UseExtendedFeatureVector, true, stoppingToken);
            // V3 adds cross-asset (DXY/US10Y/VIX) + event-proximity features on top of V2.
            // Off by default because V3 expects DXY/US10Y/VIX candles to be ingested; zero-fill
            // when missing still produces a valid vector but sacrifices the signal quality
            // that motivated the extension.
            bool useEventFeatureVector = await GetConfigAsync<bool>(
                ctx, CK_UseEventFeatureVector, false, stoppingToken);

            // V4 layers minute-level news proximity + tick-microstructure features on top
            // of V3. Requires persistent TickRecord (ReceiveTickBatch now writes them) —
            // without ticks, the 3 microstructure slots zero-fill and V4 degrades to V3
            // plus 2 calendar features. Only enable once ticks have accumulated (~days).
            bool useTickMicrostructureFeatureVector = await GetConfigAsync<bool>(
                ctx, "MLTraining:UseTickMicrostructureFeatureVector", false, stoppingToken);

            // V5 adds 4 synthetic-microstructure proxies on top of V4 — EffectiveSpread,
            // AmihudIlliquidity, RollSpreadEstimate, VarianceRatio. Computed from the same
            // tick stream V4 uses (no extra data source). Implies V4 + event-vector + ticks.
            bool useV5SyntheticMicrostructure = await GetConfigAsync<bool>(
                ctx, "MLTraining:UseV5SyntheticMicrostructure", false, stoppingToken);

            // V6 adds 5 real-DOM features on top of V5 — BookImbalance{Top1,Top5},
            // TotalLiquidityNorm, BookSlopeBid/Ask. Requires the EA to stream depth
            // via MarketBookAdd → ReceiveOrderBookSnapshot. Symbols where the broker
            // doesn't expose DOM still get a vector; the DOM slots zero-fill.
            bool useV6OrderBook = await GetConfigAsync<bool>(
                ctx, "MLTraining:UseV6OrderBookFeatures", false, stoppingToken);

            Dictionary<string, (DateTime[] Times, double[] Closes)>? basket = null;
            if (useExtendedVector && candles.Count > 0)
            {
                var basketSymbols = new[] { "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "USDCAD", "AUDUSD", "NZDUSD" };
                var windowStart = candles[0].Timestamp.AddDays(-7); // room for 120-bar warmup
                var windowEnd   = candles[^1].Timestamp.AddHours(1);

                var rawBasket = await ctx.Set<Candle>()
                    .AsNoTracking()
                    .Where(c => basketSymbols.Contains(c.Symbol)
                             && c.Timeframe == Timeframe.H1
                             && c.IsClosed
                             && !c.IsDeleted
                             && c.Timestamp >= windowStart
                             && c.Timestamp <= windowEnd)
                    .OrderBy(c => c.Timestamp)
                    .Select(c => new { c.Symbol, c.Timestamp, c.Close })
                    .ToListAsync(stoppingToken);

                basket = rawBasket
                    .GroupBy(c => c.Symbol)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            var ordered = g.OrderBy(x => x.Timestamp).ToArray();
                            return (
                                Times:  ordered.Select(x => x.Timestamp).ToArray(),
                                Closes: ordered.Select(x => (double)x.Close).ToArray()
                            );
                        },
                        StringComparer.OrdinalIgnoreCase);

                _logger.LogInformation(
                    "Run {RunId}: V2 feature vector enabled — loaded {Pairs} basket pairs " +
                    "({TotalBars} H1 bars) covering {Start:u}..{End:u}",
                    run.Id, basket.Count, rawBasket.Count, windowStart, windowEnd);
            }

            // Cost-baked labelling: derive a precise per-pair cost buffer from the symbol's
            // actual spread and screening cost config rather than a flat 3-pip default.
            //   buffer = spread_price_units + slippage_price_units + commission_per_pip_equiv
            // Each component:
            //   spread   = pair.SpreadPoints × pointSize  (configured pair spread in price)
            //   slippage = 2 × pointSize                   (fixed small friction)
            //   commission = $7/lot / ($10/pip) × pipSize  (rough pip equivalent for standard FX)
            // This makes labels represent net-profit-after-actual-costs rather than a
            // conservative constant. Zero-buffer fallback preserves legacy behaviour.
            float costBufferPriceUnits = 0f;
            try
            {
                var pairInfo = await ctx.Set<Domain.Entities.CurrencyPair>()
                    .AsNoTracking()
                    .Where(p => p.Symbol == run.Symbol && !p.IsDeleted)
                    .FirstOrDefaultAsync(stoppingToken);
                if (pairInfo is not null && pairInfo.DecimalPlaces > 0)
                {
                    double pointSize = 1.0 / Math.Pow(10, pairInfo.DecimalPlaces);
                    double pipSize   = pointSize * 10.0;
                    double spreadPriceUnits     = (pairInfo.SpreadPoints > 0 ? pairInfo.SpreadPoints : 20.0) * pointSize;
                    double slippagePriceUnits   = 2.0 * pointSize;
                    double commissionPipEquiv   = 0.7 * pipSize; // $7/lot ≈ 0.7 pips on standard FX
                    costBufferPriceUnits = (float)(spreadPriceUnits + slippagePriceUnits + commissionPipEquiv);
                }
            }
            catch { /* best-effort; legacy zero-cost label if metadata missing */ }

            List<TrainingSample> samples;
            if (useTickMicrostructureFeatureVector && useEventFeatureVector && useExtendedVector
                && basket is not null && basket.Count >= 5
                && hp.UseTripleBarrier && candles.Count > 0)
            {
                // ── V4 path: V3 + minute-level news + tick microstructure ──────────────
                // Preload event + cross-asset + per-bar tick snapshots so the per-bar
                // inner loop does O(log N) lookups rather than DB round-trips.
                var crossAssetProvider = sp.GetRequiredService<global::LascodiaTradingEngine.Application.Services.ML.CrossAssetFeatureProvider>();
                var eventProvider      = sp.GetRequiredService<global::LascodiaTradingEngine.Application.Services.ML.EconomicEventFeatureProvider>();
                var tickFlowProvider   = sp.GetRequiredService<global::LascodiaTradingEngine.Application.Services.ITickFlowProvider>();

                var eventLookup = await eventProvider.LoadForSymbolAsync(
                    run.Symbol, candles[0].Timestamp, candles[^1].Timestamp, stoppingToken);

                var crossAssetCache = new Dictionary<DateTime, global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot>();
                async Task<global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot> CrossAsOfV4(DateTime ts)
                {
                    var day = new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc);
                    if (!crossAssetCache.TryGetValue(day, out var snap))
                    {
                        snap = await crossAssetProvider.GetAsync(day, stoppingToken);
                        crossAssetCache[day] = snap;
                    }
                    return snap;
                }
                foreach (var c in candles) _ = await CrossAsOfV4(c.Timestamp);

                // Tick-flow cache: per-bar snapshot. TickFlowProvider already caches internally
                // with 1-min TTL — fine for the hot loop. Null tolerance built into V4 builder.
                var tickFlowCache = new Dictionary<DateTime, global::LascodiaTradingEngine.Application.Services.TickFlowSnapshot?>();
                foreach (var c in candles)
                {
                    if (tickFlowCache.ContainsKey(c.Timestamp)) continue;
                    tickFlowCache[c.Timestamp] = await tickFlowProvider.GetSnapshotAsync(
                        run.Symbol, c.Timestamp, stoppingToken);
                }

                if (useV5SyntheticMicrostructure || useV6OrderBook)
                {
                    if (useV6OrderBook)
                    {
                        var orderBookProvider = sp.GetRequiredService<global::LascodiaTradingEngine.Application.Services.IOrderBookFeatureProvider>();
                        var orderBookCache = new Dictionary<DateTime, global::LascodiaTradingEngine.Application.Services.OrderBookFeatureSnapshot?>();
                        foreach (var c in candles)
                        {
                            if (orderBookCache.ContainsKey(c.Timestamp)) continue;
                            orderBookCache[c.Timestamp] = await orderBookProvider.GetSnapshotAsync(
                                run.Symbol, c.Timestamp, stoppingToken);
                        }

                        samples = MLFeatureHelper.BuildTrainingSamplesWithTripleBarrierV6(
                            candles, run.Symbol, basket,
                            ts => crossAssetCache.TryGetValue(
                                new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
                                out var s) ? s : default,
                            eventLookup.SnapshotAt,
                            eventLookup.SnapshotMinuteLevel,
                            ts => tickFlowCache.TryGetValue(ts, out var tf) ? tf : null,
                            ts => orderBookCache.TryGetValue(ts, out var ob) ? ob : null,
                            CotLookup,
                            (float)hp.TripleBarrierProfitAtrMult,
                            (float)hp.TripleBarrierStopAtrMult,
                            hp.TripleBarrierHorizonBars,
                            costBufferPriceUnits);
                        int v6BookHits  = orderBookCache.Values.Count(v => v is not null);
                        int v6TickHits  = tickFlowCache.Values.Count(v => v is not null);
                        _logger.LogInformation(
                            "Built {Samples} V6 training samples (features={Feat}, tickFlowCoverage={Th}/{Total}, orderBookCoverage={Bh}/{Total}, costBuffer={Buf:F5})",
                            samples.Count, MLFeatureHelper.FeatureCountV6, v6TickHits, tickFlowCache.Count, v6BookHits, costBufferPriceUnits);
                        goto trainingSamplesBuilt;
                    }
                    samples = MLFeatureHelper.BuildTrainingSamplesWithTripleBarrierV5(
                        candles, run.Symbol, basket,
                        ts => crossAssetCache.TryGetValue(
                            new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
                            out var s) ? s : default,
                        eventLookup.SnapshotAt,
                        eventLookup.SnapshotMinuteLevel,
                        ts => tickFlowCache.TryGetValue(ts, out var tf) ? tf : null,
                        CotLookup,
                        (float)hp.TripleBarrierProfitAtrMult,
                        (float)hp.TripleBarrierStopAtrMult,
                        hp.TripleBarrierHorizonBars,
                        costBufferPriceUnits);
                    int v5TickFlowHits = tickFlowCache.Values.Count(v => v is not null);
                    _logger.LogInformation(
                        "Built {Samples} V5 training samples (features={Feat}, tickFlowCoverage={Hit}/{Total}, costBuffer={Buf:F5})",
                        samples.Count, MLFeatureHelper.FeatureCountV5, v5TickFlowHits, tickFlowCache.Count, costBufferPriceUnits);
                    goto trainingSamplesBuilt;
                }
                samples = MLFeatureHelper.BuildTrainingSamplesWithTripleBarrierV4(
                    candles, run.Symbol, basket,
                    ts => crossAssetCache.TryGetValue(
                        new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
                        out var s) ? s : default,
                    eventLookup.SnapshotAt,
                    eventLookup.SnapshotMinuteLevel,
                    ts => tickFlowCache.TryGetValue(ts, out var tf) ? tf : null,
                    CotLookup,
                    (float)hp.TripleBarrierProfitAtrMult,
                    (float)hp.TripleBarrierStopAtrMult,
                    hp.TripleBarrierHorizonBars,
                    costBufferPriceUnits);
trainingSamplesBuilt:;

                int tickFlowHits = tickFlowCache.Values.Count(v => v is not null);
                _logger.LogInformation(
                    "Built {Samples} V4 training samples (features={Feat}, tickFlowCoverage={Hit}/{Total}, costBuffer={Buf:F5})",
                    samples.Count, MLFeatureHelper.FeatureCountV4, tickFlowHits, tickFlowCache.Count, costBufferPriceUnits);
            }
            else if (useEventFeatureVector && useExtendedVector && basket is not null && basket.Count >= 5
                && hp.UseTripleBarrier && candles.Count > 0)
            {
                // Pre-load cross-asset + event series for the training window so the
                // per-bar lookups are in-memory O(log N) rather than N DB hits.
                var crossAssetProvider = sp.GetRequiredService<global::LascodiaTradingEngine.Application.Services.ML.CrossAssetFeatureProvider>();
                var eventProvider      = sp.GetRequiredService<global::LascodiaTradingEngine.Application.Services.ML.EconomicEventFeatureProvider>();

                var eventLookup = await eventProvider.LoadForSymbolAsync(
                    run.Symbol,
                    candles[0].Timestamp,
                    candles[^1].Timestamp,
                    stoppingToken);

                // Cross-asset currently fetches fresh per-timestamp. Cache per-day so the
                // training loop doesn't issue thousands of DB hits for the same D1 bar.
                var crossAssetCache = new Dictionary<DateTime, global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot>();
                async Task<global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot> CrossAssetAsOf(DateTime ts)
                {
                    var day = new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc);
                    if (!crossAssetCache.TryGetValue(day, out var snap))
                    {
                        snap = await crossAssetProvider.GetAsync(day, stoppingToken);
                        crossAssetCache[day] = snap;
                    }
                    return snap;
                }

                // Prewarm the cache for every distinct D1 in the window (one pass = bounded
                // DB hits) so the training hot loop becomes fully in-memory.
                foreach (var c in candles)
                    _ = await CrossAssetAsOf(c.Timestamp);

                samples = MLFeatureHelper.BuildTrainingSamplesWithTripleBarrierV3(
                    candles, run.Symbol, basket,
                    ts => crossAssetCache.TryGetValue(
                        new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc),
                        out var s) ? s : default,
                    eventLookup.SnapshotAt,
                    CotLookup,
                    (float)hp.TripleBarrierProfitAtrMult,
                    (float)hp.TripleBarrierStopAtrMult,
                    hp.TripleBarrierHorizonBars,
                    costBufferPriceUnits);
                _logger.LogInformation(
                    "Built {Samples} V3 training samples (features={Feat}, costBuffer={Buf:F5})",
                    samples.Count, MLFeatureHelper.FeatureCountV3, costBufferPriceUnits);
            }
            else if (useExtendedVector && basket is not null && basket.Count >= 5)
            {
                samples = hp.UseTripleBarrier
                    ? MLFeatureHelper.BuildTrainingSamplesWithTripleBarrierV2(
                        candles, run.Symbol, basket, CotLookup,
                        (float)hp.TripleBarrierProfitAtrMult,
                        (float)hp.TripleBarrierStopAtrMult,
                        hp.TripleBarrierHorizonBars,
                        costBufferPriceUnits)
                    : MLFeatureHelper.BuildTrainingSamplesV2(candles, run.Symbol, basket, CotLookup);
                _logger.LogInformation(
                    "Built {Samples} V2 training samples (features={Feat}, costBuffer={Buf:F5})",
                    samples.Count, MLFeatureHelper.FeatureCountV2, costBufferPriceUnits);
            }
            else
            {
                if (useExtendedVector)
                {
                    _logger.LogWarning(
                        "Run {RunId}: V2 requested but basket load returned {Count} pairs (<5) — " +
                        "falling back to V1 33-feature vector.",
                        run.Id, basket?.Count ?? 0);
                }
                samples = hp.UseTripleBarrier
                    ? MLFeatureHelper.BuildTrainingSamplesWithTripleBarrier(
                        candles, CotLookup,
                        (float)hp.TripleBarrierProfitAtrMult,
                        (float)hp.TripleBarrierStopAtrMult,
                        hp.TripleBarrierHorizonBars,
                        costBufferPriceUnits)
                    : MLFeatureHelper.BuildTrainingSamples(candles, CotLookup);
                _logger.LogInformation(
                    "Built {Samples} training samples (features={Feat}, costBuffer={Buf:F5})",
                    samples.Count, MLFeatureHelper.FeatureCount, costBufferPriceUnits);
            }

            if (samples.Count < hp.MinSamples)
                throw new InvalidOperationException(
                    $"Insufficient training samples: {samples.Count} < {hp.MinSamples}");

            run.TotalSamples = samples.Count;

            // ── Minimum sample count guard ───────────────────────────────────
            // Training on a small dataset produces labels whose buy/sell ratio is noisy —
            // the 28-35% imbalance we see in live logs for ~1800-sample runs is almost
            // entirely label-sampling noise, not true directional asymmetry. Under-sized
            // datasets also systematically fail the imbalance check below, wasting compute.
            // Reject up-front when sample count is below the floor so the operator expands
            // the window instead of burning cycles on retries.
            int minSamples = await GetConfigAsync<int>(
                ctx, "MLTraining:MinTrainingSamples", 3000, stoppingToken);
            if (samples.Count < minSamples)
            {
                throw new InvalidOperationException(
                    $"Training sample count {samples.Count} below minimum {minSamples}. " +
                    "Expand the training window (MLTraining:TrainingLookbackDays) — small datasets " +
                    "produce noisy label ratios that fail imbalance guards and overfit to sampling artefacts.");
            }

            // ── Symmetric triple-barrier guard ───────────────────────────────
            // Triple-barrier labels with asymmetric profit/stop multipliers introduce a
            // directional bias into the label prior (e.g. 2x profit vs 1x stop yields ~66%
            // buy-labels on random walks). That bias is then falsely attributed to
            // "strategy edge" by downstream metrics. Enforce symmetric multipliers so the
            // model has to find real asymmetry in the data to beat a 50/50 prior.
            bool useTripleBarrier = await GetConfigAsync<bool>(
                ctx, CK_UseTripleBarrier, false, stoppingToken);
            bool requireSymmetric = await GetConfigAsync<bool>(
                ctx, "MLTraining:RequireSymmetricTripleBarrier", true, stoppingToken);
            if (useTripleBarrier && requireSymmetric)
            {
                double profitMult = hp.TripleBarrierProfitAtrMult;
                double stopMult   = hp.TripleBarrierStopAtrMult;
                double ratio = stopMult > 0 ? profitMult / stopMult : double.PositiveInfinity;
                if (ratio < 0.9 || ratio > 1.1)
                {
                    throw new InvalidOperationException(
                        $"Triple-barrier multipliers asymmetric (profit={profitMult:F2}, stop={stopMult:F2}, ratio={ratio:F2}). " +
                        "Must be within [0.9, 1.1] so the label prior is 50/50 by construction — asymmetric barriers " +
                        "bake directional bias into labels and produce fake Sharpe from the prior rather than real edge. " +
                        "Disable via MLTraining:RequireSymmetricTripleBarrier=false only if you have explicit evidence that " +
                        "asymmetric barriers produce live P&L above the symmetric baseline.");
                }
            }

            // ── Class imbalance guard ────────────────────────────────────────
            // A heavily skewed label distribution silently biases the model toward the majority
            // direction. Reject the run if imbalance exceeds the configurable ceiling so we only
            // train on reasonably balanced data.
            int     buyCount         = samples.Count(s => s.Direction == 1);
            int     sellCount        = samples.Count - buyCount;
            decimal imbalanceRatio   = samples.Count > 0 ? (decimal)buyCount / samples.Count : 0.5m;
            run.LabelImbalanceRatio  = imbalanceRatio;

            double maxImbalance = await GetConfigAsync<double>(ctx, CK_MaxLabelImbalance, 0.65, stoppingToken);
            double minImbalance = 1.0 - maxImbalance;

            // SMOTE retries are selected specifically because the prior run tripped this
            // very gate — applying the same gate again guarantees the retry fails and the
            // self-healer exhausts its attempts. SMOTE synthesises minority-class samples
            // during training, so the strict 35%/65% floor is counter-productive. Require
            // just a 15 % minority floor so the synthetic-neighbourhood search has enough
            // real examples to interpolate between — below that, SMOTE's k-NN basis is
            // unreliable and we still want the operator to expand the training window.
            bool isSmote = run.LearnerArchitecture == LearnerArchitecture.Smote;
            if (isSmote)
            {
                const double smoteMinMinority = 0.15;
                double minorityFraction = Math.Min((double)imbalanceRatio, 1.0 - (double)imbalanceRatio);
                if (minorityFraction < smoteMinMinority)
                {
                    throw new InvalidOperationException(
                        $"Label imbalance too severe for SMOTE: minority fraction {minorityFraction:P1} " +
                        $"below SMOTE floor {smoteMinMinority:P0} (buy={buyCount} sell={sellCount} " +
                        $"total={samples.Count}). Expand the training window — SMOTE k-NN neighbourhoods " +
                        $"are unreliable below this threshold.");
                }
            }
            else if ((double)imbalanceRatio > maxImbalance || (double)imbalanceRatio < minImbalance)
            {
                throw new InvalidOperationException(
                    $"Label imbalance out of bounds: buy fraction {imbalanceRatio:P1} outside " +
                    $"[{minImbalance:P1}, {maxImbalance:P1}] (buy={buyCount} sell={sellCount} " +
                    $"total={samples.Count}). Expand the training window or review label logic.");
            }

            _logger.LogInformation(
                "Run {RunId}: label balance buy={Buy} sell={Sell} ratio={Ratio:P1} (threshold={Max:P0})",
                run.Id, buyCount, sellCount, imbalanceRatio, maxImbalance);

            // ── Dataset stats snapshot (pre-training) ────────────────────────
            // Capture a reproducibility record before training begins. Feature means/stds
            // are not yet available at this point; they will be appended in the snapshot
            // patch block after training completes.
            run.TrainingDatasetStatsJson = JsonSerializer.Serialize(new
            {
                fromDate       = candles.First().Timestamp,
                toDate         = candles.Last().Timestamp,
                totalCandles   = candles.Count,
                totalSamples   = samples.Count,
                buyCount,
                sellCount,
                imbalanceRatio = (double)imbalanceRatio,
                featureCount   = MLFeatureHelper.FeatureCount,
            });

            // ── Auto-select trainer architecture ────────────────────────────
            // If the run carries the enum default (BaggedLogistic = 0), it means the caller
            // did not explicitly choose an architecture — let the selector decide based on
            // historical performance, the operator-configured default, regime affinity, and
            // a sample-count safety gate. Explicit non-default values (e.g. set by a
            // hyperparam search worker) are always respected as-is.
            //
            // The ITrainerSelector implementation evaluates candidate architectures against
            // recent walk-forward results for the same symbol/timeframe, applies regime
            // affinity rules (e.g. TCN preferred for trending regimes, BaggedLogistic for
            // ranging), and falls back to BaggedLogistic when sample count is below the
            // minimum threshold for heavier architectures.
            if (run.LearnerArchitecture == LearnerArchitecture.BaggedLogistic)
            {
                var latestRegimeSnapshot = await ctx.Set<MarketRegimeSnapshot>()
                    .Where(r => r.Symbol == run.Symbol && r.Timeframe == run.Timeframe)
                    .OrderByDescending(r => r.DetectedAt)
                    .Select(r => new { Regime = (Domain.Enums.MarketRegime?)r.Regime, r.DetectedAt })
                    .FirstOrDefaultAsync(stoppingToken);

                var selector = sp.GetRequiredService<ITrainerSelector>();
                run.LearnerArchitecture = await selector.SelectAsync(
                    run.Symbol, run.Timeframe, samples.Count,
                    latestRegimeSnapshot?.Regime,
                    latestRegimeSnapshot?.DetectedAt,
                    stoppingToken);
            }

            var trainer = run.LearnerArchitecture == LearnerArchitecture.BaggedLogistic
                ? sp.GetRequiredService<IMLModelTrainer>()
                : sp.GetRequiredKeyedService<IMLModelTrainer>(run.LearnerArchitecture);

            // ── Train with timeout ───────────────────────────────────────────
            // Link a deadline CTS to the host's stopping token so that either a
            // training timeout or an application shutdown will abort the trainer.
            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromMinutes(hp.TrainingTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, timeoutCts.Token);

            // ── Load active champion for lineage tracking + warm-start ──────────
            long?          parentModelId = null;
            ModelSnapshot? warmStart     = null;
            {
                var prevModel = await ctx.Set<MLModel>()
                    .Where(m => m.Symbol == run.Symbol && m.Timeframe == run.Timeframe &&
                                m.IsActive && !m.IsDeleted && m.ModelBytes != null)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(stoppingToken);

                if (prevModel is not null)
                {
                    parentModelId = prevModel.Id;
                    if (run.TriggerType == TriggerType.AutoDegrading && prevModel.ModelBytes is { Length: > 0 })
                    {
                        try
                        {
                            warmStart = JsonSerializer.Deserialize<ModelSnapshot>(prevModel.ModelBytes);
                            _logger.LogInformation(
                                "Run {RunId}: warm-starting from model {ModelId}", run.Id, prevModel.Id);
                        }
                        catch
                        {
                            _logger.LogWarning("Run {RunId}: warm-start deserialization failed — cold start", run.Id);
                        }
                    }
                }
            }

            TrainingResult result;
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["RunId"]     = run.Id,
                ["Symbol"]    = run.Symbol,
                ["Timeframe"] = run.Timeframe.ToString(),
            }))
            {
                result = await trainer.TrainAsync(samples, hp, warmStart, parentModelId, linkedCts.Token);
            }

            sw.Stop();

            // ── Extract snapshot fields needed before quality gate ───────────
            double snapEce = 0.0;
            double snapBss = double.NegativeInfinity;
            bool snapshotContractValid = true;
            string snapshotContractIssues = string.Empty;
            if (result.ModelBytes is { Length: > 0 })
            {
                try
                {
                    var snapForGate = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
                    if (snapForGate is not null)
                    {
                        if (string.Equals(snapForGate.Type, "TABNET", StringComparison.OrdinalIgnoreCase))
                        {
                            snapForGate = TabNetSnapshotSupport.NormalizeSnapshotCopy(snapForGate);
                            var validation = TabNetSnapshotSupport.ValidateSnapshot(snapForGate, allowLegacyV2: true);
                            snapshotContractValid = validation.IsValid;
                            snapshotContractIssues = string.Join("; ", validation.Issues);
                        }
                        else if (string.Equals(snapForGate.Type, "GBM", StringComparison.OrdinalIgnoreCase))
                        {
                            snapForGate = GbmSnapshotSupport.NormalizeSnapshotCopy(snapForGate);
                            (snapshotContractValid, snapshotContractIssues) = ValidateGbmPromotionSnapshot(snapForGate);
                        }
                        else if (string.Equals(snapForGate.Type, "AdaBoost", StringComparison.OrdinalIgnoreCase))
                        {
                            snapForGate = AdaBoostSnapshotSupport.NormalizeSnapshotCopy(snapForGate);
                            (snapshotContractValid, snapshotContractIssues) = ValidateAdaBoostPromotionSnapshot(snapForGate);
                        }
                        else if (string.Equals(snapForGate.Type, "FTTRANSFORMER", StringComparison.OrdinalIgnoreCase))
                        {
                            snapForGate = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapForGate);
                            (snapshotContractValid, snapshotContractIssues) = ValidateFtTransformerPromotionSnapshot(snapForGate);
                        }
                        else if (string.Equals(snapForGate.Type, "elm", StringComparison.OrdinalIgnoreCase))
                        {
                            snapForGate = ElmSnapshotSupport.NormalizeSnapshotCopy(snapForGate);
                            var validation = ElmSnapshotSupport.ValidateNormalizedSnapshot(snapForGate, allowLegacy: false);
                            snapshotContractValid = validation.IsValid;
                            snapshotContractIssues = string.Join("; ", validation.Issues);
                        }
                        snapEce = snapForGate.Ece;
                        snapBss = snapForGate.BrierSkillScore;
                    }
                }
                catch { /* ECE/BSS stay at defaults — gates effectively disabled */ }
            }

            // ── Extract parent OOB accuracy for regression guard ─────────────
            double parentOobAccuracy = 0.0;
            if (warmStart is not null)
            {
                parentOobAccuracy = warmStart.OobAccuracy;
            }
            else if (parentModelId.HasValue)
            {
                // For non-AutoDegrading triggers: deserialize parent to get OobAccuracy
                var parentModel = await ctx.Set<MLModel>()
                    .Where(m => m.Id == parentModelId.Value && m.ModelBytes != null)
                    .Select(m => new { m.ModelBytes })
                    .AsNoTracking()
                    .FirstOrDefaultAsync(stoppingToken);
                if (parentModel?.ModelBytes is { Length: > 0 })
                {
                    try
                    {
                        var parentSnap = JsonSerializer.Deserialize<ModelSnapshot>(parentModel.ModelBytes);
                        if (parentSnap is not null) parentOobAccuracy = parentSnap.OobAccuracy;
                    }
                    catch { /* regression guard disabled */ }
                }
            }

            // ── Multi-gate quality check ─────────────────────────────────────
            // A model must pass ALL of the following gates before being promoted to champion:
            //
            //   Accuracy          — direction accuracy on the hold-out set must meet MinAccuracyToPromote
            //   ExpectedValue     — EV = P(correct) × avgWin - P(incorrect) × avgLoss must be ≥ 0
            //   BrierScore        — probabilistic calibration loss: lower is better (≤ MaxBrierScore)
            //   SharpeRatio       — simulated strategy Sharpe on the hold-out set (≥ MinSharpeRatio)
            //   WalkForward std   — cross-fold accuracy std dev must be low (≤ MaxWalkForwardStdDev)
            //                       to ensure the model generalises rather than fitting one fold
            //   ECE               — Expected Calibration Error (optional; gate disabled when MaxEce=0)
            //   BrierSkillScore   — BSS = 1 − (Brier / Brier_climatology); positive means better than
            //                       naive always-predict-base-rate baseline (optional; disabled when <-1.0)
            //   OOB regression    — new OOB accuracy must be ≥ MinQualityRetentionRatio × parent OOB
            //                       to prevent quality regression after retraining
            var m       = result.FinalMetrics;
            var cvCheck = result.CvResult;
            bool qualityRegressionFailed =
                hp.MinQualityRetentionRatio > 0.0 &&
                parentOobAccuracy > 0.0           &&
                result.FinalMetrics.OobAccuracy < parentOobAccuracy * hp.MinQualityRetentionRatio;

            // ── Regime-conditional F1 gate ──────────────────────────────────
            // In Trending regime, allow directional (single-class) models if they
            // have high accuracy and positive EV — a sell-only model during a
            // sustained downtrend is a valid directional strategy.
            // In all other regimes, enforce the standard MinF1Score threshold.
            var currentRegime = await ctx.Set<MarketRegimeSnapshot>()
                .Where(r => r.Symbol == run.Symbol && r.Timeframe == run.Timeframe && !r.IsDeleted)
                .OrderByDescending(r => r.DetectedAt)
                .Select(r => (Domain.Enums.MarketRegime?)r.Regime)
                .FirstOrDefaultAsync(stoppingToken);

            bool isTrending = currentRegime == Domain.Enums.MarketRegime.Trending
                           || currentRegime == Domain.Enums.MarketRegime.Breakout;

            double trendingMinAccuracy = await GetConfigAsync<double>(ctx, CK_TrendingMinAccuracy, 0.65, stoppingToken);
            double trendingMinEV       = await GetConfigAsync<double>(ctx, CK_TrendingMinEV, 0.02, stoppingToken);

            // ── Profitability-based F1 bypass ───────────────────────────────
            // High-EV models (ELM, GBM, TabNet) often predict one class exclusively
            // (F1=0) but deliver strong expected value per trade. Allow them through
            // if EV ≥ 0.10 and Sharpe ≥ 0.50 — these are selective, high-conviction
            // models that trade rarely but profitably.
            double evBypassMinEV     = await GetConfigAsync<double>(ctx, "MLTraining:F1BypassMinEV",     0.10, stoppingToken);
            double evBypassMinSharpe = await GetConfigAsync<double>(ctx, "MLTraining:F1BypassMinSharpe", 0.50, stoppingToken);
            bool   evBypassF1       = m.ExpectedValue >= evBypassMinEV && m.SharpeRatio >= evBypassMinSharpe;

            bool f1Passed = isTrending
                ? (m.F1 >= hp.MinF1Score || (m.Accuracy >= trendingMinAccuracy && m.ExpectedValue >= trendingMinEV))
                : (hp.MinF1Score <= 0 || m.F1 >= hp.MinF1Score || evBypassF1);

            // ── Profitability-based Brier bypass ──────────────────────────────
            // Models with very high EV and Sharpe that marginally miss the Brier
            // threshold (e.g. 0.2439 vs 0.2400) are genuinely profitable. Allow
            // a relaxed Brier ceiling (MaxBrier + 5%) when EV ≥ 0.10 and Sharpe ≥ 1.0.
            double brierBypassMinEV     = await GetConfigAsync<double>(ctx, "MLTraining:BrierBypassMinEV",     0.10, stoppingToken);
            double brierBypassMinSharpe = await GetConfigAsync<double>(ctx, "MLTraining:BrierBypassMinSharpe", 1.00, stoppingToken);
            double brierCeiling = hp.MaxBrierScore;
            bool   brierBypassed = false;
            if (m.ExpectedValue >= brierBypassMinEV && m.SharpeRatio >= brierBypassMinSharpe
                && m.BrierScore > hp.MaxBrierScore && m.BrierScore <= hp.MaxBrierScore * 1.05)
            {
                brierCeiling = hp.MaxBrierScore * 1.05;
                brierBypassed = true;
            }

            // ── F1-conditional accuracy bypass (class-imbalance rescue) ──────────
            // The flat MinAccuracyToPromote floor punishes class-imbalance-aware
            // models that legitimately predict the minority class well. A model with
            // F1 above a reasonable floor but accuracy below 50 % typically means the
            // model predicts the minority class with high precision/recall while the
            // majority class drives overall accuracy down. Two tiers:
            //
            // COLD-START tier (first model per combo):
            //   F1 ≥ ColdStart:F1BypassMinF1 (default 0.40) AND accuracy in
            //   [0.50-band, 0.50+band] (default ±0.10) AND Brier under ceiling.
            //   Relaxed so any reasonable class-imbalance-aware baseline is admitted.
            //
            // STRICT tier (champion exists):
            //   F1 ≥ Strict:F1BypassMinF1 (default 0.50) AND Sharpe ≥
            //   Strict:F1BypassMinSharpe (default 0.50) AND accuracy in
            //   [0.50-band, 0.50+band] AND Brier under ceiling. Tighter because
            //   admitting a class-imbalance model into an existing champion's
            //   combo means the shadow arbiter will SPRT-compare them; we want
            //   the challenger to be genuinely strong, not just different.
            //
            // Today's run 106 (GBPUSD/M15 SVGP, F1=0.643, acc=47.4%, Sharpe=0.97)
            // is exactly the class the strict tier is built to rescue — a real
            // class-imbalance-aware model that the cold-start-only bypass missed
            // because Model 26 already existed for that combo.
            double coldStartF1Bypass_MinF1 = await GetConfigAsync<double>(
                ctx, "MLTraining:ColdStart:F1BypassMinF1", 0.40, stoppingToken);
            double coldStartF1Bypass_AccBand = await GetConfigAsync<double>(
                ctx, "MLTraining:ColdStart:F1BypassAccBand", 0.10, stoppingToken);
            double strictF1Bypass_MinF1 = await GetConfigAsync<double>(
                ctx, "MLTraining:F1Bypass:MinF1", 0.50, stoppingToken);
            double strictF1Bypass_MinSharpe = await GetConfigAsync<double>(
                ctx, "MLTraining:F1Bypass:MinSharpe", 0.50, stoppingToken);
            double strictF1Bypass_AccBand = await GetConfigAsync<double>(
                ctx, "MLTraining:F1Bypass:AccBand", 0.10, stoppingToken);

            bool coldStartAccuracyBypass =
                coldStart
                && m.F1 >= coldStartF1Bypass_MinF1
                && m.Accuracy >= (0.50 - coldStartF1Bypass_AccBand)
                && m.Accuracy <= (0.50 + coldStartF1Bypass_AccBand)
                && m.BrierScore <= brierCeiling;

            bool strictAccuracyBypass =
                !coldStart
                && m.F1 >= strictF1Bypass_MinF1
                && m.SharpeRatio >= strictF1Bypass_MinSharpe
                && m.Accuracy >= (0.50 - strictF1Bypass_AccBand)
                && m.Accuracy <= (0.50 + strictF1Bypass_AccBand)
                && m.BrierScore <= brierCeiling;

            bool accuracyOk = m.Accuracy >= hp.MinAccuracyToPromote
                           || coldStartAccuracyBypass
                           || strictAccuracyBypass;

            if (coldStartAccuracyBypass)
            {
                _logger.LogInformation(
                    "Run {RunId} ({Symbol}/{Tf}): cold-start F1 bypass — accuracy {Acc:P1} " +
                    "below floor {MinAcc:P1} but F1 {F1:F3} \u2265 {MinF1:F3} (class-imbalance rescue)",
                    run.Id, run.Symbol, run.Timeframe,
                    m.Accuracy, hp.MinAccuracyToPromote, m.F1, coldStartF1Bypass_MinF1);
            }
            else if (strictAccuracyBypass)
            {
                _logger.LogInformation(
                    "Run {RunId} ({Symbol}/{Tf}): strict-mode F1 bypass — accuracy {Acc:P1} " +
                    "below floor {MinAcc:P1} but F1 {F1:F3} \u2265 {MinF1:F3} and Sharpe {Sh:F2} \u2265 {MinSh:F2} " +
                    "(class-imbalance challenger rescue)",
                    run.Id, run.Symbol, run.Timeframe,
                    m.Accuracy, hp.MinAccuracyToPromote, m.F1, strictF1Bypass_MinF1,
                    m.SharpeRatio, strictF1Bypass_MinSharpe);
            }

            bool passed =
                accuracyOk                                                                         &&
                m.ExpectedValue      >= hp.MinExpectedValue                                        &&
                m.BrierScore         <= brierCeiling                                               &&
                m.SharpeRatio        >= hp.MinSharpeRatio                                          &&
                f1Passed                                                                           &&
                cvCheck.FoldCount    > 0                                                          &&
                cvCheck.StdAccuracy  <= hp.MaxWalkForwardStdDev                                    &&
                (hp.MaxEce <= 0 || snapEce <= hp.MaxEce)                                           &&
                (hp.MinBrierSkillScore <= -1.0 || snapBss >= hp.MinBrierSkillScore)                &&
                snapshotContractValid                                                               &&
                !qualityRegressionFailed;

            _logger.LogInformation(
                "Quality gates — acc={Acc:P1}/{MinAcc:P1} ev={EV:F4}/{MinEV:F4} " +
                "brier={Brier:F4}/{MaxBrier:F4} sharpe={Sharpe:F2}/{MinSharpe:F2} " +
                "f1={F1:F3}/{MinF1:F3} regime={Regime} f1Passed={F1Passed} evBypass={EvBypass} " +
                "brierBypass={BrierBypass} " +
                "wfStd={WfStd:P1}/{MaxWfStd:P1} ece={Ece:F4}/{MaxEce:F4} " +
                "bss={Bss:F4}/{MinBss:F4} snapshotValid={SnapshotValid} oobReg={OobNew:P1}/{OobParent:P1} passed={Passed}",
                m.Accuracy,              hp.MinAccuracyToPromote,
                m.ExpectedValue,         hp.MinExpectedValue,
                m.BrierScore,            brierCeiling,
                m.SharpeRatio,           hp.MinSharpeRatio,
                m.F1,                    hp.MinF1Score,
                currentRegime?.ToString() ?? "unknown", f1Passed, evBypassF1,
                brierBypassed,
                cvCheck.StdAccuracy,     hp.MaxWalkForwardStdDev,
                snapEce,                 hp.MaxEce,
                snapBss,                 hp.MinBrierSkillScore, snapshotContractValid,
                result.FinalMetrics.OobAccuracy, parentOobAccuracy,
                passed);

            // ── Update run record ────────────────────────────────────────────
            run.Status             = passed ? RunStatus.Completed : RunStatus.Failed;
            run.CompletedAt        = DateTime.UtcNow;
            run.TrainingDurationMs = sw.ElapsedMilliseconds;
            run.DirectionAccuracy  = (decimal)m.Accuracy;
            run.MagnitudeRMSE      = (decimal)m.MagnitudeRmse;
            run.F1Score            = (decimal)m.F1;
            run.ExpectedValue      = (decimal)m.ExpectedValue;
            run.BrierScore         = (decimal)m.BrierScore;
            run.SharpeRatio        = (decimal)m.SharpeRatio;
            run.ErrorMessage       = passed ? null : BuildGateFailureMessage(
                m, cvCheck, hp, snapEce, snapBss, result.FinalMetrics.OobAccuracy, parentOobAccuracy,
                isTrending, trendingMinAccuracy, trendingMinEV, evBypassF1);
            if (!passed && !snapshotContractValid)
                run.ErrorMessage = $"Invalid model snapshot contract: {snapshotContractIssues}";

            // ── Training cost tracking (observability) ──────────────────────
            try
            {
                long peakMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);
                double peakMemoryMb = peakMemoryBytes / (1024.0 * 1024.0);
                var costPrefix = $"MLTrainingCost:{run.Symbol}:{run.Timeframe}";

                await UpsertConfigAsync(ctx, $"{costPrefix}:LastDurationMs",
                    run.TrainingDurationMs.Value.ToString(), stoppingToken);
                await UpsertConfigAsync(ctx, $"{costPrefix}:LastSampleCount",
                    run.TotalSamples.ToString(), stoppingToken);
                await UpsertConfigAsync(ctx, $"{costPrefix}:LastPeakMemoryMB",
                    peakMemoryMb.ToString("F1"), stoppingToken);

                _logger.LogInformation(
                    "Training cost for run {RunId} ({Symbol}/{Tf}): duration={DurationMs}ms " +
                    "samples={Samples} peakMemory={MemMB:F1}MB",
                    run.Id, run.Symbol, run.Timeframe,
                    run.TrainingDurationMs.Value, run.TotalSamples, peakMemoryMb);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to persist training cost diagnostics for run {RunId} ({Symbol}/{Tf}) — non-fatal.",
                    run.Id,
                    run.Symbol,
                    run.Timeframe);
            }

            if (!passed)
            {
                await db.SaveChangesAsync(stoppingToken);
                sp.GetRequiredService<ITrainerSelector>()
                    .InvalidateCache(run.Symbol, run.Timeframe);
                _logger.LogWarning("Run {RunId} did not pass quality gates — model not promoted", run.Id);
                // Increment consecutive-retrain-failure count on the outgoing champion model
                // (if any). After N consecutive failed retrains, retire the model instead of
                // looping retrain attempts indefinitely. This stops the "infinite retrain on a
                // strategy whose edge is gone" pattern where drift monitoring repeatedly
                // triggers retrains that each fail to beat the degraded baseline.
                await TrackRetrainOutcomeAsync(ctx, db, run, promoted: false, stoppingToken);
                await MaybeCreateTrainingFailureAlertAsync(ctx, run, stoppingToken);
                await MaybeQueueSelfTuningRetryAsync(ctx, db, run, m, hp, stoppingToken);
                return;
            }

            // ── Demote previous active model + snapshot its live performance ──
            // Advisory lock scoped to symbol+timeframe prevents concurrent promotion
            // from both MLTrainingWorker and MLShadowArbiterWorker.
            var lockKey = $"ml:promote:{run.Symbol}:{run.Timeframe}";
            await using var promotionLock = await _distributedLock.TryAcquireAsync(lockKey, stoppingToken);
            if (promotionLock is null)
            {
                _logger.LogWarning(
                    "Run {RunId}: could not acquire promotion lock for {Symbol}/{Tf} — another promotion in progress. Deferring.",
                    run.Id, run.Symbol, run.Timeframe);
                // Revert run status with a short backoff to prevent a tight retry loop
                // when two workers keep racing for the same symbol/timeframe promotion.
                await ctx.Set<MLTrainingRun>().Where(r => r.Id == run.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status,           RunStatus.Queued)
                        .SetProperty(r => r.WorkerInstanceId, (Guid?)null)
                        .SetProperty(r => r.NextRetryAt,      DateTime.UtcNow.AddSeconds(30)), stoppingToken);
                return;
            }

            var previousChampion = await ctx.Set<MLModel>()
                .FirstOrDefaultAsync(
                    x => x.Symbol == run.Symbol && x.Timeframe == run.Timeframe && x.IsActive,
                    stoppingToken);

            // ── Profitability-based promotion gate ──────────────────────────
            // Only supersede the current champion if the new model is genuinely
            // better. A model with lower EV should not replace a profitable champion
            // unless it offers significantly better balance (F1) or the champion
            // has been flagged by drift workers.
            if (previousChampion is not null)
            {
                double champEV    = (double)(previousChampion.ExpectedValue ?? 0m);
                double champF1    = (double)(previousChampion.F1Score ?? 0m);
                double champSharpe = (double)(previousChampion.SharpeRatio ?? 0m);
                double newEV      = m.ExpectedValue;
                double newF1      = m.F1;
                double newSharpe  = m.SharpeRatio;

                // Compute a composite profitability score: weighted blend of EV, Sharpe, and F1
                // EV is weighted highest (profit matters most), F1 provides balance bonus
                double champScore = champEV * 5.0 + champSharpe * 0.1 + champF1 * 0.5;
                double newScore   = newEV   * 5.0 + newSharpe   * 0.1 + newF1   * 0.5;

                // Hard rule: a less profitable model must never replace a more profitable one.
                // The F1 bypass only applies when the champion has low EV (≤0.05) or the
                // challenger retains at least 50 % of the champion's EV — prevents a model
                // with marginal F1 improvement from destroying a high-EV champion.
                bool f1BypassAllowed = newF1 > champF1 + 0.15
                    && newEV >= -0.01
                    && (champEV <= 0.05 || newEV >= champEV * 0.5);

                bool newModelIsBetter =
                    newScore > champScore                           // composite score is higher
                    || f1BypassAllowed                              // F1 balance bypass (guarded by EV floor)
                    || champEV <= 0.0;                              // champion has zero/negative EV — always replace

                if (!newModelIsBetter)
                {
                    _logger.LogInformation(
                        "Run {RunId}: new model passed gates but is not more profitable than champion {ChampId} " +
                        "(new: score={NewScore:F4} ev={NewEV:F4} f1={NewF1:F3} | champ: score={ChampScore:F4} ev={ChampEV:F4} f1={ChampF1:F3}). " +
                        "Saving as Superseded without promoting.",
                        run.Id, previousChampion.Id, newScore, newEV, newF1, champScore, champEV, champF1);

                    run.Status = RunStatus.Completed;
                    run.CompletedAt = DateTime.UtcNow;
                    run.TrainingDurationMs = sw.ElapsedMilliseconds;
                    run.DirectionAccuracy = (decimal)m.Accuracy;
                    run.F1Score = (decimal)m.F1;
                    run.BrierScore = (decimal)m.BrierScore;
                    run.SharpeRatio = (decimal)m.SharpeRatio;
                    run.ErrorMessage = null;

                    // Save as a non-active model for shadow evaluation
                    var nonActiveModel = new MLModel
                    {
                        Symbol              = run.Symbol,
                        Timeframe           = run.Timeframe,
                        LearnerArchitecture = run.LearnerArchitecture,
                        ModelVersion        = $"{run.Symbol}_{run.Timeframe}_{run.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}_challenger",
                        Status              = MLModelStatus.Superseded,
                        IsActive            = false,
                        TrainingSamples     = samples.Count,
                        TrainedAt           = DateTime.UtcNow,
                        DirectionAccuracy   = (decimal)m.Accuracy,
                        F1Score             = (decimal)m.F1,
                        ExpectedValue       = (decimal)m.ExpectedValue,
                        BrierScore          = (decimal)m.BrierScore,
                        SharpeRatio         = (decimal)m.SharpeRatio,
                    };
                    ctx.Set<MLModel>().Add(nonActiveModel);
                    await db.SaveChangesAsync(stoppingToken);
                    // Set FK after save so nonActiveModel.Id is the real DB-generated ID,
                    // not a temporary negative value that triggers FK violations.
                    run.MLModelId = nonActiveModel.Id;
                    await db.SaveChangesAsync(stoppingToken);

                    // Improvement #3: Create shadow evaluation with tournament group ID.
                    // Derive the group from the champion ID so that all challengers for the
                    // same champion within the same hour share a tournament group. This lets
                    // the MLShadowArbiterWorker evaluate them as a cohort.
                    var tournamentGroup = DeterministicTournamentGroup(previousChampion.Id);
                    var shadow = new MLShadowEvaluation
                    {
                        Symbol             = run.Symbol,
                        Timeframe          = run.Timeframe,
                        ChampionModelId    = previousChampion.Id,
                        ChallengerModelId  = nonActiveModel.Id,
                        Status             = ShadowEvaluationStatus.Running,
                        RequiredTrades     = hp.ShadowRequiredTrades,
                        ExpiresAt          = DateTime.UtcNow.AddDays(hp.ShadowExpiryDays),
                        PromotionThreshold = (decimal)hp.MinAccuracyToPromote,
                        StartedAt          = DateTime.UtcNow,
                        TournamentGroupId  = tournamentGroup,
                    };
                    ctx.Set<MLShadowEvaluation>().Add(shadow);
                    await db.SaveChangesAsync(stoppingToken);

                    sp.GetRequiredService<ITrainerSelector>().InvalidateCache(run.Symbol, run.Timeframe);
                    return;
                }

                // New model is better — proceed with promotion.
                // The distributed lock acquired at line ~937 (promotionLock) already
                // serializes this path against concurrent promotions.
                await SnapshotChampionPerformanceAsync(previousChampion, ctx, stoppingToken);

                // Deactivate ALL active models for this symbol/timeframe, not just the one
                // returned by FirstOrDefaultAsync. Multiple models can accumulate as active
                // when concurrent promotions from different architectures each only deactivate
                // the single model they found, leaving others orphaned as active.
                await ctx.Set<MLModel>()
                    .Where(m => m.Symbol == run.Symbol && m.Timeframe == run.Timeframe
                                && m.IsActive && !m.IsDeleted)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.IsActive, false)
                        .SetProperty(m => m.Status, MLModelStatus.Superseded), stoppingToken);

                _logger.LogInformation(
                    "Promoting run {RunId} over champion {ChampId}: new score={NewScore:F4} > champ score={ChampScore:F4}",
                    run.Id, previousChampion.Id, newScore, champScore);
            }

            var (finalModelBytes, plattA, plattB) = await PatchSnapshotAsync(
                result.ModelBytes, run, candles, samples, buyCount, sellCount,
                imbalanceRatio, cotNetMin, cotNetMax, cotMomMin, cotMomMax,
                ctx, stoppingToken);

            // ── ONNX export (opt-in) ───────────────────────────────────────────
            // Attempts to serialise the trained snapshot into an ONNX inference graph so
            // MLSignalScorer can route scoring through IOnnxInferenceEngine (GPU-accelerated
            // or optimised CPU) once MLScoring:PreferOnnx is enabled. Returns null when no
            // registered exporter can handle this snapshot, or when a concrete exporter
            // signals a dependency gap (NotSupportedException). Failures are intentionally
            // non-fatal — the model still promotes without ONNX bytes and scoring falls
            // back to the legacy pure-C# engine.
            byte[]? onnxBytes = await TryExportOnnxBytesAsync(sp, finalModelBytes, stoppingToken);

            // ── Safety: ensure no other active models remain for this symbol/timeframe ──
            // This catches edge cases where the FirstOrDefaultAsync above returned null
            // (no champion found) but orphaned active models still exist from prior bugs.
            // When previousChampion was null we don't hold the promotion lock yet, so
            // acquire it here to prevent concurrent promotions from racing.
            IAsyncDisposable? safetyLock = null;
            if (previousChampion is null)
            {
                var safetyLockKey = $"ml:promote:{run.Symbol}:{run.Timeframe}";
                safetyLock = await _distributedLock.TryAcquireAsync(safetyLockKey, TimeSpan.FromSeconds(30), stoppingToken);
                if (safetyLock is null)
                {
                    _logger.LogWarning(
                        "Run {RunId}: could not acquire safety promotion lock for {Symbol}/{Tf} — proceeding cautiously.",
                        run.Id, run.Symbol, run.Timeframe);
                }
            }
            try
            {
                await ctx.Set<MLModel>()
                    .Where(m => m.Symbol == run.Symbol && m.Timeframe == run.Timeframe
                                && m.IsActive && !m.IsDeleted)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.IsActive, false)
                        .SetProperty(m => m.Status, MLModelStatus.Superseded), stoppingToken);
            }
            finally
            {
                if (safetyLock is not null)
                    await safetyLock.DisposeAsync();
            }

            // ── Persist new model ────────────────────────────────────────────
            var cv           = result.CvResult;
            var modelVersion = $"{run.Symbol}_{run.Timeframe}_{run.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";

            var model = new MLModel
            {
                Symbol                 = run.Symbol,
                Timeframe              = run.Timeframe,
                LearnerArchitecture    = run.LearnerArchitecture,
                ModelVersion           = modelVersion,
                Status                 = MLModelStatus.Active,
                IsActive               = true,
                ModelBytes             = finalModelBytes,
                OnnxBytes              = onnxBytes,
                TrainingSamples        = samples.Count,
                TrainedAt              = DateTime.UtcNow,
                ActivatedAt            = DateTime.UtcNow,
                DirectionAccuracy      = (decimal)m.Accuracy,
                MagnitudeRMSE          = (decimal)m.MagnitudeRmse,
                F1Score                = (decimal)m.F1,
                ExpectedValue          = (decimal)m.ExpectedValue,
                BrierScore             = (decimal)m.BrierScore,
                SharpeRatio            = (decimal)m.SharpeRatio,
                PlattA                 = plattA,
                PlattB                 = plattB,
                WalkForwardFolds       = cv.FoldCount,
                WalkForwardAvgAccuracy = (decimal)cv.AvgAccuracy,
                WalkForwardStdDev      = (decimal)cv.StdAccuracy,
            };

            ctx.Set<MLModel>().Add(model);

            // Save model first to get the real DB-generated Id (not a temp negative value),
            // then link the run in a separate save to avoid FK violations.
            await db.SaveChangesAsync(stoppingToken);
            run.MLModelId = model.Id;
            await db.SaveChangesAsync(stoppingToken);

            // ── Shadow evaluation for challenger vs champion ─────────────────
            // When a previous champion exists the newly promoted model does NOT immediately
            // replace it in production. Instead both models run side-by-side:
            //   - The champion continues serving live signals (IsActive = true).
            //   - The challenger (new model) records shadow predictions via MLSignalScorer.
            // MLShadowArbiterWorker periodically evaluates the challenger against the champion
            // using SPRT and a two-proportion z-test. Only when the challenger demonstrates
            // statistically significant superiority (or the champion is significantly worse)
            // does the challenger get promoted to replace the champion.
            //
            // When there is no previous champion (first model for a symbol/timeframe), the
            // new model is activated immediately without a shadow evaluation period.
            if (previousChampion is not null)
            {
                // Improvement #3: assign tournament group for parallel shadow evaluation.
                // Use deterministic group derived from champion ID + hour so all challengers
                // for the same champion within the same hour join the same tournament.
                var tournamentGroup = DeterministicTournamentGroup(previousChampion.Id);
                var shadow = new MLShadowEvaluation
                {
                    Symbol             = run.Symbol,
                    Timeframe          = run.Timeframe,
                    ChampionModelId    = previousChampion.Id,
                    ChallengerModelId  = model.Id,
                    Status             = ShadowEvaluationStatus.Running,
                    RequiredTrades     = hp.ShadowRequiredTrades,
                    ExpiresAt          = DateTime.UtcNow.AddDays(hp.ShadowExpiryDays),
                    PromotionThreshold = (decimal)hp.MinAccuracyToPromote,
                    StartedAt          = DateTime.UtcNow,
                    TournamentGroupId  = tournamentGroup,
                };
                ctx.Set<MLShadowEvaluation>().Add(shadow);
                _logger.LogInformation(
                    "Shadow eval queued: champion={Champion} vs challenger={Challenger} tournament={Tournament}",
                    previousChampion.Id, model.Id, tournamentGroup);
            }

            await db.SaveChangesAsync(stoppingToken);

            // Invalidate TrainerSelector cache so the next selection for this
            // symbol/timeframe picks up the freshly completed run immediately.
            var trainerSelector = sp.GetRequiredService<ITrainerSelector>();
            trainerSelector.InvalidateCache(run.Symbol, run.Timeframe);

            // Queue shadow training runs for alternative architectures so that the
            // MLShadowArbiterWorker can compare them against the newly promoted model.
            // Only queue shadows when the current run was NOT itself a shadow run
            // (TriggerType != AutoDegrading) to prevent cascading retrain loops.
            // Also enforce a 1-hour cooldown per architecture to prevent excessive churn.
            try
            {
                if (run.TriggerType != TriggerType.AutoDegrading)
                {
                    var latestRegime = await ctx.Set<MarketRegimeSnapshot>()
                        .Where(r => r.Symbol == run.Symbol && r.Timeframe == run.Timeframe)
                        .OrderByDescending(r => r.DetectedAt)
                        .Select(r => new { Regime = (Domain.Enums.MarketRegime?)r.Regime, r.DetectedAt })
                        .FirstOrDefaultAsync(stoppingToken);

                    var shadowArchs = await trainerSelector.SelectShadowArchitecturesAsync(
                        run.LearnerArchitecture, run.Symbol, run.Timeframe, samples.Count,
                        latestRegime?.Regime, latestRegime?.DetectedAt, stoppingToken);

                    var cooldownCutoff = DateTime.UtcNow.AddHours(-1);

                    foreach (var arch in shadowArchs)
                    {
                        // Skip if a run for this architecture is already queued or completed recently
                        bool recentlyHandled = await ctx.Set<MLTrainingRun>()
                            .AnyAsync(r => r.Symbol == run.Symbol
                                        && r.Timeframe == run.Timeframe
                                        && r.LearnerArchitecture == arch
                                        && !r.IsDeleted
                                        && (r.Status == RunStatus.Queued
                                            || r.Status == RunStatus.Running
                                            || (r.CompletedAt != null && r.CompletedAt > cooldownCutoff)),
                                stoppingToken);

                        if (!recentlyHandled)
                        {
                            // Use a fresh training window rather than copying the parent run's
                            // dates, which may be stale. Default widened from 730 to 1825
                            // days (5 years) so training samples capture at least one full
                            // rate-hike-to-cut cycle. The effective window is bounded by
                            // min(1825, actual_candle_history_days) — on a fresh database
                            // with only a few months of EA-streamed candles, this reduces
                            // to "use all available candles". Once the EA accumulates more
                            // history the default starts hitting its intended ceiling.
                            var shadowNow = DateTime.UtcNow;
                            int windowDays = await GetConfigAsync<int>(
                                ctx, "MLTraining:TrainingDataWindowDays", 1825, stoppingToken);

                            ctx.Set<MLTrainingRun>().Add(new MLTrainingRun
                            {
                                Symbol              = run.Symbol,
                                Timeframe           = run.Timeframe,
                                TriggerType         = TriggerType.AutoDegrading,
                                Status              = RunStatus.Queued,
                                FromDate            = shadowNow.AddDays(-windowDays),
                                ToDate              = shadowNow,
                                StartedAt           = shadowNow,
                                LearnerArchitecture = arch,
                            });

                            _logger.LogInformation(
                                "Shadow architecture run queued: {Arch} for {Symbol}/{Tf}",
                                arch, run.Symbol, run.Timeframe);
                        }
                    }

                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception shadowEx)
            {
                _logger.LogWarning(shadowEx,
                    "Failed to queue shadow architecture runs for {Symbol}/{Tf} — non-critical",
                    run.Symbol, run.Timeframe);
            }

            await PublishPromotionAsync(
                run, model, previousChampion, result.FinalMetrics, result.CvResult,
                mediator, eventService, db, sw.ElapsedMilliseconds, samples.Count, stoppingToken);

            // ── Regime-specific sub-models ───────────────────────────────────
            // Use stoppingToken (host shutdown) rather than linkedCts (main training
            // timeout) so regime sub-models get their own independent timeout budget.
            // Each sub-model creates its own CTS capped at TrainingTimeoutMinutes / 3.
            if (hp.EnableRegimeSpecificModels)
            {
                await TrainRegimeSubModelsAsync(
                    run, samples, hp, candles, trainer, ctx, db, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellations (timeout or shutdown) are NOT counted as failures.
            // A training run cancelled due to host shutdown should be retried immediately on the
            // next startup. A run cancelled by the timeout CTS simply means the architecture was
            // too slow for the configured budget — re-queue without penalty so an operator can
            // increase MLTraining:TrainingTimeoutMinutes or choose a lighter architecture.
            // Reset to Queued so another instance or the next startup can pick it up immediately.
            run.Status             = RunStatus.Queued;
            run.WorkerInstanceId   = null;
            run.NextRetryAt        = null;
            run.ErrorMessage       = "Re-queued: training was cancelled (timeout or shutdown).";
            run.TrainingDurationMs = sw.ElapsedMilliseconds;

            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch { /* best-effort */ }

            _logger.LogWarning("Run {RunId} was cancelled and re-queued for immediate retry.", run.Id);
        }
        catch (Exception ex)
        {
            run.AttemptCount++;

            // Terminal-error classifier — errors that retry cannot resolve. Triple-barrier
            // asymmetric-multiplier is a config error (profit/stop mults in EngineConfig):
            // four minutes of backoff won't rewrite the config, and the operator needs to
            // act. Marking terminal frees the queue slot and stops the per-attempt warning
            // spam. Sample-count failures remain retryable because data can accumulate or
            // the window can be widened between attempts.
            //
            // Drift-gate rejection from TabNet is also policy-terminal: the training window
            // is non-stationary, and retrying immediately won't fix it — the window has to
            // age out and new data has to arrive. Retry loops there just spam fail-level logs.
            bool isDriftGateRejection = ex.Message.Contains("drift gate rejected training", StringComparison.OrdinalIgnoreCase);
            bool isTerminalError =
                ex.Message.Contains("Triple-barrier multipliers asymmetric", StringComparison.OrdinalIgnoreCase)
                || isDriftGateRejection;

            bool canRetry = !isTerminalError && run.AttemptCount < run.MaxAttempts;

            if (canRetry)
            {
                // Exponential back-off: 2^attempt × 60 s, capped at 1 hour.
                int backoffSecs      = Math.Min(3600, (int)Math.Pow(2, run.AttemptCount) * 60);
                run.Status           = RunStatus.Queued;
                run.WorkerInstanceId = null;
                run.NextRetryAt      = DateTime.UtcNow.AddSeconds(backoffSecs);
                run.ErrorMessage     = $"[Attempt {run.AttemptCount}/{run.MaxAttempts}] {ex.Message}";
                run.TrainingDurationMs = sw.ElapsedMilliseconds;

                _logger.LogWarning(
                    "Run {RunId} failed (attempt {Attempt}/{Max}). Retrying in {Secs}s. Error: {Err}",
                    run.Id, run.AttemptCount, run.MaxAttempts, backoffSecs, ex.Message);
            }
            else
            {
                run.Status             = RunStatus.Failed;
                run.CompletedAt        = DateTime.UtcNow;
                run.ErrorMessage       = $"[Permanently failed after {run.AttemptCount} attempt(s)] {ex.Message}";
                run.TrainingDurationMs = sw.ElapsedMilliseconds;

                // Policy-driven terminal outcomes (drift gate refusing to fit a non-stationary
                // window, etc.) are expected operational events rather than faults. Log them
                // at Warning with a one-line summary instead of a full Error + stack trace so
                // the operator error stream stays focused on genuine exceptions.
                if (isDriftGateRejection)
                {
                    _logger.LogWarning(
                        "Run {RunId} terminated by TabNet drift gate after {Attempts} attempt(s): {Reason}",
                        run.Id, run.AttemptCount, ex.Message);
                }
                else
                {
                    _logger.LogError(ex,
                        "Run {RunId} permanently failed after {Attempts} attempt(s).", run.Id, run.AttemptCount);
                }
            }

            try
            {
                // Clear stale tracked entities (e.g. partially-created MLModel) that may
                // cause FK violations when we only want to persist the run's failure status.
                // Also reset MLModelId — it may reference a model that was never saved to DB.
                run.MLModelId = null;
                ctx.ChangeTracker.Clear();
                ctx.Attach(run);
                ctx.Entry(run).State = Microsoft.EntityFrameworkCore.EntityState.Modified;

                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to persist failure/retry status for run {RunId}", run.Id);
            }

            if (!canRetry)
                await MaybeCreateTrainingFailureAlertAsync(ctx, run, CancellationToken.None);
        }
    }

    // ── Champion performance snapshot ────────────────────────────────────────

    /// <summary>
    /// Captures live prediction accuracy stats from the outgoing champion model's
    /// prediction logs before it is superseded, so historical comparison data is
    /// preserved without requiring a full log re-query later.
    /// </summary>
    private async Task SnapshotChampionPerformanceAsync(
        MLModel                                 champion,
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        try
        {
            var liveLogs = await ctx.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == champion.Id &&
                            l.DirectionCorrect != null &&
                            !l.IsDeleted)
                .AsNoTracking()
                .Select(l => new { l.DirectionCorrect })
                .ToListAsync(ct);

            if (liveLogs.Count > 0)
            {
                champion.LiveTotalPredictions  = liveLogs.Count;
                champion.LiveDirectionAccuracy = (decimal)liveLogs.Count(l => l.DirectionCorrect == true) / liveLogs.Count;
                champion.LiveActiveDays        = champion.ActivatedAt.HasValue
                    ? (int)(DateTime.UtcNow - champion.ActivatedAt.Value).TotalDays
                    : 0;

                _logger.LogInformation(
                    "Champion model {Id} retirement snapshot: live_acc={Acc:P1} predictions={N} active_days={Days}",
                    champion.Id,
                    champion.LiveDirectionAccuracy,
                    champion.LiveTotalPredictions,
                    champion.LiveActiveDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to snapshot live performance for model {Id} — non-critical", champion.Id);
        }
    }

    // ── Snapshot patching ──────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to export the final trained snapshot to ONNX bytes using any registered
    /// <see cref="LascodiaTradingEngine.Application.Services.Inference.IOnnxModelExporter"/>
    /// that claims it can handle the snapshot. Returns null when no exporter matches,
    /// when the exporter throws <see cref="NotSupportedException"/> (dependency-gap
    /// indicator), or when snapshot deserialisation fails. Never throws — callers rely
    /// on this being a strictly optional, best-effort optimisation.
    /// </summary>
    private async Task<byte[]?> TryExportOnnxBytesAsync(
        IServiceProvider sp, byte[] finalModelBytes, CancellationToken ct)
    {
        try
        {
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<ModelSnapshot>(
                finalModelBytes,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (snapshot is null) return null;

            var exporters = sp.GetServices<
                LascodiaTradingEngine.Application.Services.Inference.IOnnxModelExporter>();
            var exporter = exporters.FirstOrDefault(e => e.CanExport(snapshot));
            if (exporter is null) return null;

            var onnxBytes = await exporter.ExportToBytesAsync(snapshot, ct);
            return onnxBytes is { Length: > 0 } ? onnxBytes : null;
        }
        catch (NotSupportedException)
        {
            // Exporter signalled a known dependency gap (e.g. Onnx proto builder not
            // installed yet). Fall back to no ONNX bytes; legacy path handles scoring.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ONNX export failed — continuing without ONNX bytes");
            return null;
        }
    }

    /// <summary>
    /// Patches the trainer-produced <see cref="ModelSnapshot"/> with COT normalisation
    /// bounds, per-feature empirical variances, per-regime standardisation parameters,
    /// and enriched dataset stats. Returns the final serialised bytes and Platt coefficients.
    /// </summary>
    private async Task<(byte[] FinalModelBytes, decimal PlattA, decimal PlattB)> PatchSnapshotAsync(
        byte[]                                  rawModelBytes,
        MLTrainingRun                           run,
        List<Candle>                            candles,
        List<TrainingSample>                    samples,
        int                                     buyCount,
        int                                     sellCount,
        decimal                                 imbalanceRatio,
        float                                   cotNetMin,
        float                                   cotNetMax,
        float                                   cotMomMin,
        float                                   cotMomMax,
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        decimal plattA = 1m, plattB = 0m;
        byte[]  finalModelBytes = rawModelBytes;

        if (rawModelBytes is not { Length: > 0 })
            return (finalModelBytes, plattA, plattB);

        try
        {
            var snap = JsonSerializer.Deserialize<ModelSnapshot>(rawModelBytes);
            if (snap is null)
                return (finalModelBytes, plattA, plattB);

            if (string.Equals(snap.Type, "TABNET", StringComparison.OrdinalIgnoreCase))
                snap = TabNetSnapshotSupport.NormalizeSnapshotCopy(snap);
            else if (string.Equals(snap.Type, "AdaBoost", StringComparison.OrdinalIgnoreCase))
                snap = AdaBoostSnapshotSupport.NormalizeSnapshotCopy(snap);

            plattA = (decimal)snap.PlattA;
            plattB = (decimal)snap.PlattB;

            // Inject training-window COT normalisation bounds
            snap.CotNetNormMin = cotNetMin;
            snap.CotNetNormMax = cotNetMax;
            snap.CotMomNormMin = cotMomMin;
            snap.CotMomNormMax = cotMomMax;

            // Record the raw feature-vector dimension the trainer consumed so that
            // CompositeMLEvaluator can dispatch between V1 (33), V2 (37), and V3 (43)
            // builders at inference time. Derived from the actual first sample to stay
            // authoritative regardless of which path built the samples. Also persist
            // FeatureSchemaVersion explicitly so future schema collisions on the raw
            // count (e.g. a V4 that happens to be 43) stay distinguishable.
            if (samples.Count > 0 && samples[0].Features is { Length: > 0 })
            {
                snap.ExpectedInputFeatures = samples[0].Features.Length;
                snap.FeatureSchemaVersion =
                    snap.ExpectedInputFeatures == MLFeatureHelper.FeatureCountV6 ? 6 :
                    snap.ExpectedInputFeatures == MLFeatureHelper.FeatureCountV5 ? 5 :
                    snap.ExpectedInputFeatures == MLFeatureHelper.FeatureCountV4 ? 4 :
                    snap.ExpectedInputFeatures == MLFeatureHelper.FeatureCountV3 ? 3 :
                    snap.ExpectedInputFeatures == MLFeatureHelper.FeatureCountV2 ? 2 :
                    1;
                // Guardrail: reject feature creep at the training boundary so a bloated
                // schema cannot enter the snapshot cache and contaminate live scoring.
                MLFeatureHelper.AssertFeatureCountWithinCap(
                    snap.ExpectedInputFeatures,
                    $"MLTrainingWorker snapshot persist (model={run.MLModelId})");
            }

            // Compute per-feature empirical variances from the deployed feature pipeline.
            // This keeps OOD gating aligned with the exact feature layout inference consumes.
            if (samples.Count > 0)
            {
                int f = snap.Features.Length > 0 ? snap.Features.Length : snap.Means.Length;
                var variances = new double[f];
                var meansVec = new double[f];
                int n = 0;
                foreach (var s in samples)
                {
                    float[] z = MLSignalScorer.StandardiseFeatures(s.Features, snap.Means, snap.Stds, f);
                    InferenceHelpers.ApplyModelSpecificFeatureTransforms(z, snap);
                    MLSignalScorer.ApplyFeatureMask(z, snap.ActiveFeatureMask, f);
                    n++;
                    for (int fi = 0; fi < f && fi < z.Length; fi++)
                    {
                        double x = z[fi];
                        double delta = x - meansVec[fi];
                        meansVec[fi] += delta / n;
                        variances[fi] += delta * (x - meansVec[fi]);
                    }
                }
                for (int fi = 0; fi < f; fi++)
                    variances[fi] = n > 1 ? Math.Max(0.0, variances[fi] / (n - 1)) : 0.0;
                snap.FeatureVariances = variances;
            }

            // Per-regime feature standardisation
            try
            {
                var regimeSnapsForStd = await ctx.Set<MarketRegimeSnapshot>()
                    .Where(r => r.Symbol    == run.Symbol &&
                                r.DetectedAt >= run.FromDate &&
                                r.DetectedAt <= run.ToDate)
                    .OrderBy(r => r.DetectedAt)
                    .AsNoTracking()
                    .ToListAsync(ct);

                if (regimeSnapsForStd.Count >= 10)
                {
                    var regimeSampleGroups = new Dictionary<string, List<float[]>>();

                    for (int ci = 0; ci < candles.Count; ci++)
                    {
                        int si = ci - MLFeatureHelper.LookbackWindow;
                        if (si < 0 || si >= samples.Count) continue;

                        var rs = regimeSnapsForStd.LastOrDefault(
                            r => r.DetectedAt <= candles[ci].Timestamp);
                        if (rs is null) continue;

                        var rName = rs.Regime.ToString();
                        if (!regimeSampleGroups.TryGetValue(rName, out var rList))
                            regimeSampleGroups[rName] = rList = [];
                        rList.Add(samples[si].Features);
                    }

                    foreach (var (rName, rFeatures) in regimeSampleGroups)
                    {
                        if (rFeatures.Count < 30) continue;

                        var (rMeans, rStds) = MLFeatureHelper.ComputeStandardization(rFeatures);
                        snap.RegimeMeans[rName] = rMeans;
                        snap.RegimeStds[rName]  = rStds;
                    }

                    if (snap.RegimeMeans.Count > 0)
                        _logger.LogInformation(
                            "Run {RunId}: stored per-regime standardisation for {N} regimes",
                            run.Id, snap.RegimeMeans.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId}: per-regime standardisation failed — skipped", run.Id);
            }

            if (string.Equals(snap.Type, "TABNET", StringComparison.OrdinalIgnoreCase))
            {
                var validation = TabNetSnapshotSupport.ValidateSnapshot(snap, allowLegacyV2: true);
                if (!validation.IsValid)
                    throw new InvalidOperationException($"Patched TabNet snapshot failed validation: {string.Join("; ", validation.Issues)}");
            }
            else if (string.Equals(snap.Type, "AdaBoost", StringComparison.OrdinalIgnoreCase))
            {
                var validation = AdaBoostSnapshotSupport.ValidateSnapshot(snap, allowLegacy: false);
                if (!validation.IsValid)
                    throw new InvalidOperationException($"Patched AdaBoost snapshot failed validation: {string.Join("; ", validation.Issues)}");
            }

            finalModelBytes = JsonSerializer.SerializeToUtf8Bytes(snap);

            // Enrich dataset stats with feature means/stds
            try
            {
                run.TrainingDatasetStatsJson = JsonSerializer.Serialize(new
                {
                    fromDate       = candles.First().Timestamp,
                    toDate         = candles.Last().Timestamp,
                    totalCandles   = candles.Count,
                    totalSamples   = samples.Count,
                    buyCount,
                    sellCount,
                    imbalanceRatio = (double)imbalanceRatio,
                    featureCount   = MLFeatureHelper.FeatureCount,
                    featureMeans   = snap.Means,
                    featureStds    = snap.Stds,
                });
            }
            catch { /* enrichment is best-effort; pre-training JSON already saved */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId}: failed to patch snapshot metadata — raw bytes kept", run.Id);
        }

        return (finalModelBytes, plattA, plattB);
    }

    // ── Promotion publishing ──────────────────────────────────────────────────

    /// <summary>
    /// Writes the audit log entry and publishes the <see cref="MLModelActivatedIntegrationEvent"/>
    /// after a model has been promoted. Both operations are best-effort.
    /// </summary>
    private async Task PublishPromotionAsync(
        MLTrainingRun    run,
        MLModel          model,
        MLModel?         previousChampion,
        EvalMetrics      m,
        WalkForwardResult cv,
        IMediator        mediator,
        IIntegrationEventService? eventService,
        IWriteApplicationDbContext db,
        long             durationMs,
        int              sampleCount,
        CancellationToken ct)
    {
        // Audit log
        try
        {
            await mediator.Send(new LogDecisionCommand
            {
                EntityType   = nameof(MLModel),
                EntityId     = model.Id,
                DecisionType = "MLModelPromotion",
                Outcome      = "Promoted",
                Reason       = $"acc={m.Accuracy:P1} f1={m.F1:F3} ev={m.ExpectedValue:F4} " +
                               $"brier={m.BrierScore:F4} sharpe={m.SharpeRatio:F2} " +
                               $"wf_avg={cv.AvgAccuracy:P1} wf_std={cv.StdAccuracy:P3}",
                Source       = nameof(MLTrainingWorker),
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit log failed for run {RunId} — non-critical", run.Id);
        }

        // Integration event
        if (eventService is not null)
        {
            try
            {
                await eventService.SaveAndPublish(db, new MLModelActivatedIntegrationEvent
                {
                    NewModelId        = model.Id,
                    OldModelId        = previousChampion?.Id,
                    Symbol            = model.Symbol,
                    Timeframe         = model.Timeframe,
                    TrainingRunId     = run.Id,
                    DirectionAccuracy = model.DirectionAccuracy ?? 0,
                    ActivatedAt       = model.ActivatedAt ?? DateTime.UtcNow,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event service publish failed — non-critical");
            }
        }

        _logger.LogInformation(
            "Run {RunId} complete. ModelId={ModelId} Version={Version} DurationMs={Ms} Samples={N}",
            run.Id, model.Id, model.ModelVersion, durationMs, sampleCount);
    }

    // ── Consecutive-failure alert ─────────────────────────────────────────────

    /// <summary>
    /// After every training failure, checks whether the last N runs for the same
    /// symbol/timeframe have all failed. If so, creates an <see cref="Alert"/> of type
    /// <see cref="AlertType.MLModelDegraded"/> so operators are notified.
    /// Skips creation when an active alert already exists to avoid alert spam.
    /// N is configurable via <c>MLTraining:ConsecutiveFailureAlertThreshold</c> (default 3).
    /// </summary>
    private async Task MaybeCreateTrainingFailureAlertAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        MLTrainingRun                           run,
        CancellationToken                       ct)
    {
        try
        {
            int threshold = await GetConfigAsync<int>(ctx, CK_ConsecFailThreshold, 3, ct);

            // Fetch the last `threshold` completed/failed runs for this symbol/timeframe
            // regardless of status, then check whether ALL of them failed. The previous
            // implementation only queried Failed runs, so it could never detect a successful
            // run breaking the streak (e.g. F-S-F-F would still trigger with threshold=3).
            var recentStatuses = await ctx.Set<MLTrainingRun>()
                .Where(r => r.Symbol    == run.Symbol    &&
                            r.Timeframe == run.Timeframe &&
                            (r.Status   == RunStatus.Failed || r.Status == RunStatus.Completed))
                .OrderByDescending(r => r.CompletedAt)
                .Take(threshold)
                .AsNoTracking()
                .Select(r => r.Status)
                .ToListAsync(ct);

            if (recentStatuses.Count < threshold || recentStatuses.Any(s => s != RunStatus.Failed))
                return; // not enough runs or streak broken by a success

            // Avoid duplicate alerts — skip if one is already active
            bool alertExists = await ctx.Set<Alert>()
                .AnyAsync(a => a.Symbol    == run.Symbol          &&
                               a.AlertType == AlertType.MLModelDegraded &&
                               a.IsActive  && !a.IsDeleted,
                          ct);
            if (alertExists)
                return;

            string destination = await GetConfigAsync<string>(ctx, CK_AlertDestination, "ml-ops", ct);

            var alert = new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = run.Symbol,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    reason    = "consecutive_training_failures",
                    count     = threshold,
                    symbol    = run.Symbol,
                    timeframe = run.Timeframe.ToString(),
                }),
                IsActive = true,
            };

            ctx.Set<Alert>().Add(alert);
            await ctx.SaveChangesAsync(ct);

            _logger.LogWarning(
                "MLModelDegraded alert created for {Symbol}/{Tf} after {N} consecutive training failures.",
                run.Symbol, run.Timeframe, threshold);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create training failure alert — non-critical");
        }
    }

    // ── Regime-specific sub-model training ───────────────────────────────────

    /// <summary>
    /// Trains one sub-model per distinct market regime found in the training window.
    /// Each sub-model is persisted as a non-active <see cref="MLModel"/> with
    /// <see cref="MLModel.RegimeScope"/> set to the regime name.
    /// <c>MLSignalScorer</c> prefers a matching regime-specific model over the global one.
    /// </summary>
    private async Task TrainRegimeSubModelsAsync(
        MLTrainingRun                           run,
        List<TrainingSample>                    allSamples,
        TrainingHyperparams                     hp,
        List<Candle>                            candles,
        IMLModelTrainer                         trainer,
        Microsoft.EntityFrameworkCore.DbContext ctx,
        IWriteApplicationDbContext              db,
        CancellationToken                       ct)
    {
        try
        {
            // Load regime snapshots for the training window
            var regimeSnaps = await ctx.Set<MarketRegimeSnapshot>()
                .Where(r => r.Symbol    == run.Symbol &&
                            r.DetectedAt >= run.FromDate &&
                            r.DetectedAt <= run.ToDate)
                .OrderBy(r => r.DetectedAt)
                .AsNoTracking()
                .ToListAsync(ct);

            if (regimeSnaps.Count < 10)
            {
                _logger.LogDebug("Not enough regime snapshots for sub-model training — skipping");
                return;
            }

            // Map each candle index to its regime using LOCF (last-observation-carried-forward).
            // For each candle, the most recent regime snapshot at or before that candle's
            // timestamp is used. This is the same assignment logic used by the shadow arbiter
            // and ensures regime-specific sub-models are trained on consistently tagged data.
            var regimeGroups = new Dictionary<string, List<int>>(); // regimeName → candle indices
            for (int i = 0; i < candles.Count; i++)
            {
                var snap = regimeSnaps.LastOrDefault(r => r.DetectedAt <= candles[i].Timestamp);
                if (snap is null) continue;
                var name = snap.Regime.ToString();
                if (!regimeGroups.TryGetValue(name, out var list))
                    regimeGroups[name] = list = [];
                list.Add(i);
            }

            // Build COT lookup (reuse same logic as caller — no COT data reloaded)
            // For simplicity we reuse the allSamples already built from the full candle set.
            // We select samples whose index falls in each regime group.
            var candleIndexToSampleIndex = BuildCandleToSampleIndexMap(candles.Count, allSamples.Count);

            foreach (var (regimeName, candleIndices) in regimeGroups)
            {
                ct.ThrowIfCancellationRequested();

                // Collect samples belonging to this regime
                var regimeSamples = candleIndices
                    .Where(ci => candleIndexToSampleIndex.TryGetValue(ci, out _))
                    .Select(ci => allSamples[candleIndexToSampleIndex[ci]])
                    .ToList();

                int minRegimeSamples = Math.Max(100, hp.MinSamples / 3);
                if (regimeSamples.Count < minRegimeSamples)
                {
                    _logger.LogDebug(
                        "Regime {Regime}: only {N} samples — skipping sub-model", regimeName, regimeSamples.Count);
                    continue;
                }

                _logger.LogInformation(
                    "Training regime sub-model for {Symbol}/{Tf} regime={Regime} samples={N}",
                    run.Symbol, run.Timeframe, regimeName, regimeSamples.Count);

                // Use relaxed hyperparameters for regime sub-models:
                //   - Fewer epochs and more aggressive early stopping since regime datasets
                //     are smaller and prone to overfitting with a full training budget.
                //   - One fewer walk-forward fold to avoid each fold being too small to train on.
                //   - Lower minimum sample requirement (1/3 of global) so rare regimes still
                //     produce a sub-model rather than being skipped entirely.
                var regimeHp = hp with
                {
                    MinSamples            = minRegimeSamples,
                    MaxEpochs             = Math.Max(50, hp.MaxEpochs / 2),
                    EarlyStoppingPatience = Math.Max(5, hp.EarlyStoppingPatience / 2),
                    WalkForwardFolds      = Math.Max(2, hp.WalkForwardFolds - 1),
                };

                // Demote any existing active regime sub-model for this scope
                var existingRegimeModel = await ctx.Set<MLModel>()
                    .FirstOrDefaultAsync(
                        m => m.Symbol      == run.Symbol    &&
                             m.Timeframe   == run.Timeframe &&
                             m.RegimeScope == regimeName    &&
                             m.IsActive    && !m.IsDeleted,
                        ct);

                if (existingRegimeModel is not null)
                {
                    existingRegimeModel.IsActive = false;
                    existingRegimeModel.Status   = MLModelStatus.Superseded;
                }

                TrainingResult regimeResult;
                using var regimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                regimeCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(5, hp.TrainingTimeoutMinutes / 3)));

                try
                {
                    regimeResult = await trainer.TrainAsync(regimeSamples, regimeHp, ct: regimeCts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Regime sub-model training failed for {Regime} — skipping", regimeName);
                    continue;
                }

                var rm = regimeResult.FinalMetrics;
                if (rm.Accuracy < hp.MinAccuracyToPromote * 0.95) // allow 5 % grace for smaller samples
                {
                    _logger.LogInformation(
                        "Regime sub-model {Regime} did not pass quality gate (acc={Acc:P1}) — skipping",
                        regimeName, rm.Accuracy);
                    continue;
                }

                var regimeModel = new MLModel
                {
                    Symbol              = run.Symbol,
                    Timeframe           = run.Timeframe,
                    LearnerArchitecture = run.LearnerArchitecture,
                    RegimeScope         = regimeName,
                    ModelVersion      = $"{run.Symbol}_{run.Timeframe}_{regimeName}_{run.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Status            = MLModelStatus.Active,
                    IsActive          = true,
                    ModelBytes        = regimeResult.ModelBytes,
                    TrainingSamples   = regimeSamples.Count,
                    TrainedAt         = DateTime.UtcNow,
                    ActivatedAt       = DateTime.UtcNow,
                    DirectionAccuracy = (decimal)rm.Accuracy,
                    F1Score           = (decimal)rm.F1,
                    ExpectedValue     = (decimal)rm.ExpectedValue,
                    BrierScore        = (decimal)rm.BrierScore,
                    SharpeRatio       = (decimal)rm.SharpeRatio,
                    WalkForwardFolds  = regimeResult.CvResult.FoldCount,
                    WalkForwardAvgAccuracy = (decimal)regimeResult.CvResult.AvgAccuracy,
                    WalkForwardStdDev      = (decimal)regimeResult.CvResult.StdAccuracy,
                };

                ctx.Set<MLModel>().Add(regimeModel);
                await db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Regime sub-model persisted: {Regime} ModelId={Id} acc={Acc:P1}",
                    regimeName, regimeModel.Id, rm.Accuracy);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Regime sub-model training loop failed — non-critical");
        }
    }

    /// <summary>
    /// Builds a mapping from candle list index to training sample index.
    /// <c>MLFeatureHelper.BuildTrainingSamples</c> skips the first <c>LookbackWindow</c> candles
    /// and the last candle (no next-bar label), so sample[i] corresponds to candle[LookbackWindow + i].
    /// </summary>
    private static Dictionary<int, int> BuildCandleToSampleIndexMap(int candleCount, int sampleCount)
    {
        var map = new Dictionary<int, int>(sampleCount);
        int offset = MLFeatureHelper.LookbackWindow;
        for (int si = 0; si < sampleCount; si++)
            map[offset + si] = si;
        return map;
    }

    // ── Hyperparameter loading ────────────────────────────────────────────────

    /// <summary>
    /// Loads all training hyperparameters from <see cref="EngineConfig"/> database rows,
    /// falling back to safe defaults for any key that is absent or unparseable.
    ///
    /// <para>
    /// This design allows operators to tune the ML pipeline at runtime without redeployment.
    /// All 60+ hyperparameters are consolidated here so a single call produces a complete,
    /// immutable <see cref="TrainingHyperparams"/> record. Individual runs may then overlay
    /// a subset via <c>HyperparamConfigJson</c> (see hyperparameter search worker).
    /// </para>
    ///
    /// <para>Notable parameter groups:</para>
    /// <list type="bullet">
    ///   <item><b>Model architecture</b>: K (ensemble size), LearningRate, L2Lambda, L1Lambda,
    ///         MaxEpochs, EarlyStoppingPatience, MaxGradNorm</item>
    ///   <item><b>Walk-forward CV</b>: WalkForwardFolds, EmbargoBarCount, MaxBadFoldFraction,
    ///         MaxWalkForwardStdDev, PurgeHorizonBars</item>
    ///   <item><b>Label engineering</b>: UseTripleBarrier, TripleBarrier*Mult, LabelSmoothing,
    ///         UseAdaptiveLabelSmoothing, AtrLabelSensitivity, MaxLabelImbalance</item>
    ///   <item><b>Regularisation / augmentation</b>: NoiseSigma, MixupAlpha, FgsmEpsilon,
    ///         TemporalDecayLambda, AgeDecayLambda, UseCovariateShiftWeights</item>
    ///   <item><b>Calibration</b>: FitTemperatureScale, PlattA/B (set from snapshot), MaxEce,
    ///         RecalibrationDecayLambda</item>
    ///   <item><b>Ensemble</b>: DiversityLambda, MaxEnsembleDiversity, NclLambda,
    ///         EnableGreedyEnsembleSelection, OobPruningEnabled, MaxLearnerCorrelation</item>
    ///   <item><b>Promotion quality gates</b>: MinAccuracyToPromote, MinExpectedValue,
    ///         MaxBrierScore, MinSharpeRatio, MinBrierSkillScore, MinQualityRetentionRatio</item>
    ///   <item><b>Shadow evaluation</b>: ShadowRequiredTrades, ShadowExpiryDays, ShadowMinZScore</item>
    /// </list>
    /// </summary>
    private static async Task<TrainingHyperparams> LoadHyperparamsAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        // Bulk-load all MLTraining:* config keys in a single query instead of ~60 individual round-trips.
        var configMap = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLTraining:"))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        T Cfg<T>(string key, T defaultValue)
        {
            if (!configMap.TryGetValue(key, out var raw) || raw is null) return defaultValue;
            try   { return (T)Convert.ChangeType(raw, typeof(T)); }
            catch { return defaultValue; }
        }

        return new TrainingHyperparams(
            K:                        Cfg<int>   (CK_K,                   5),
            LearningRate:             Cfg<double>(CK_LR,                  0.01),
            L2Lambda:                 Cfg<double>(CK_L2,                  0.001),
            MaxEpochs:                Cfg<int>   (CK_MaxEpochs,           200),
            EarlyStoppingPatience:    Cfg<int>   (CK_ESPatience,          15),
            MinAccuracyToPromote:     Cfg<double>(CK_MinAccuracy,         0.55),
            MinExpectedValue:         Cfg<double>(CK_MinEV,               0.0),
            MaxBrierScore:            Cfg<double>(CK_MaxBrier,            0.25),
            MinSharpeRatio:           Cfg<double>(CK_MinSharpe,           0.5),
            MinSamples:               Cfg<int>   (CK_MinSamples,          500),
            ShadowRequiredTrades:     Cfg<int>   (CK_ShadowTrades,        50),
            ShadowExpiryDays:         Cfg<int>   (CK_ShadowExpiry,        30),
            WalkForwardFolds:         Cfg<int>   (CK_WFolds,              4),
            EmbargoBarCount:          Cfg<int>   (CK_Embargo,             30),
            TrainingTimeoutMinutes:   Cfg<int>   (CK_Timeout,             30),
            TemporalDecayLambda:      Cfg<double>(CK_DecayLambda,         2.0),
            DriftWindowDays:          Cfg<int>   (CK_DriftWindowDays,     14),
            DriftMinPredictions:      Cfg<int>   (CK_DriftMinPredictions, 30),
            DriftAccuracyThreshold:   Cfg<double>(CK_DriftAccThreshold,   0.50),
            MaxWalkForwardStdDev:     Cfg<double>(CK_MaxWfStdDev,         0.15),
            LabelSmoothing:              Cfg<double>(CK_LabelSmoothing,              0.05),
            MinFeatureImportance:        Cfg<double>(CK_MinFeatureImportance,        0.0),
            EnableRegimeSpecificModels:  Cfg<bool>  (CK_EnableRegimeModels,          false),
            FeatureSampleRatio:          Cfg<double>(CK_FeatureSampleRatio,          0.0),
            MaxEce:                      Cfg<double>(CK_MaxEce,                      0.0),
            // Default flipped from false to true: triple-barrier labels align targets
            // with actual trading P&L (did a profitable move happen before a losing one
            // within N bars) rather than next-bar direction, which is noise-dominated
            // on short timeframes. The existing BuildTrainingSamplesWithTripleBarrier
            // implementation has been production-ready but disabled by default. On
            // M5/M15 FX, where we empirically can't train next-bar direction above
            // 55 % accuracy, triple-barrier should substantially raise the quality
            // ceiling by denoising the training labels themselves. Operators can
            // revert via MLTraining:UseTripleBarrier=false.
            UseTripleBarrier:            Cfg<bool>  (CK_UseTripleBarrier,            true),
            // Symmetric multipliers (ratio 1.0) — required by the symmetric-barrier
            // guardrail in ProcessRunAsync, which rejects anything outside [0.9, 1.1].
            // The previous 1.5 / 1.0 defaults gave ratio=1.5 and caused every
            // triple-barrier-enabled run to fail on attempt 1 with "Triple-barrier
            // multipliers asymmetric". Symmetric multipliers are also correct on
            // statistical grounds: asymmetric ratios bias the label prior so Sharpe
            // reflects the ratio rather than real directional edge (López de Prado
            // "Advances in Financial ML" §3 meta-labelling discussion).
            TripleBarrierProfitAtrMult:  Cfg<double>(CK_TripleBarrierProfitAtrMult,  1.5),
            TripleBarrierStopAtrMult:    Cfg<double>(CK_TripleBarrierStopAtrMult,    1.5),
            TripleBarrierHorizonBars:    Cfg<int>   (CK_TripleBarrierHorizonBars,    24),
            NoiseSigma:                  Cfg<double>(CK_NoiseSigma,                  0.0),
            FpCostWeight:                Cfg<double>(CK_FpCostWeight,                0.5),
            NclLambda:                   Cfg<double>(CK_NclLambda,                   0.0),
            FracDiffD:                   Cfg<double>("MLTraining:FracDiffD",           0.0),
            MaxFoldDrawdown:             Cfg<double>("MLTraining:MaxFoldDrawdown",     1.0),
            MinFoldCurveSharpe:          Cfg<double>("MLTraining:MinFoldCurveSharpe", -99.0),
            PolyLearnerFraction:         Cfg<double>("MLTraining:PolyLearnerFraction",         0.0),
            PurgeHorizonBars:            Cfg<int>   ("MLTraining:PurgeHorizonBars",            0),
            NoiseCorrectionThreshold:    Cfg<double>("MLTraining:NoiseCorrectionThreshold",    0.0),
            MaxLearnerCorrelation:       Cfg<double>("MLTraining:MaxLearnerCorrelation",       1.0),
            SwaStartEpoch:               Cfg<int>   (CK_SwaStartEpoch,              0),
            SwaFrequency:                Cfg<int>   (CK_SwaFrequency,               1),
            MixupAlpha:                  Cfg<double>(CK_MixupAlpha,                 0.0),
            EnableGreedyEnsembleSelection: Cfg<bool>(CK_EnableGes,                  false),
            MaxGradNorm:                 Cfg<double>(CK_MaxGradNorm,                0.0),
            AtrLabelSensitivity:         Cfg<double>(CK_AtrLabelSensitivity,        0.0),
            ShadowMinZScore:             Cfg<double>(CK_ShadowMinZScore,            1.645),
            L1Lambda:                    Cfg<double>(CK_L1Lambda,                   0.0),
            MagnitudeQuantileTau:        Cfg<double>(CK_MagnitudeQuantileTau,       0.0),
            MagLossWeight:               Cfg<double>(CK_MagLossWeight,              0.0),
            DensityRatioWindowDays:      Cfg<int>   (CK_DensityRatioWindowDays,     0),
            BarsPerDay:                  Cfg<int>   ("MLTraining:BarsPerDay",          24),
            DurbinWatsonThreshold:       Cfg<double>(CK_DurbinWatsonThreshold,      0.0),
            AdaptiveLrDecayFactor:       Cfg<double>(CK_AdaptiveLrDecayFactor,      0.0),
            OobPruningEnabled:           Cfg<bool>  (CK_OobPruningEnabled,          false),
            MutualInfoRedundancyThreshold: Cfg<double>(CK_MutualInfoRedundancyThr,  0.0),
            MinSharpeTrendSlope:         Cfg<double>(CK_MinSharpeTrendSlope,        -99.0),
            FitTemperatureScale:         Cfg<bool>  (CK_FitTemperatureScale,        false),
            MinBrierSkillScore:          Cfg<double>(CK_MinBrierSkillScore,         -1.0),
            RecalibrationDecayLambda:    Cfg<double>(CK_RecalibrationDecayLambda,   0.0),
            MaxEnsembleDiversity:        Cfg<double>(CK_MaxEnsembleDiversity,       1.0),
            UseSymmetricCE:              Cfg<bool>  (CK_UseSymmetricCE,             false),
            SymmetricCeAlpha:            Cfg<double>(CK_SymmetricCeAlpha,           0.0),
            DiversityLambda:             Cfg<double>(CK_DiversityLambda,            0.0),
            UseAdaptiveLabelSmoothing:   Cfg<bool>  (CK_UseAdaptiveLabelSmoothing,  false),
            AgeDecayLambda:              Cfg<double>(CK_AgeDecayLambda,             0.0),
            UseCovariateShiftWeights:    Cfg<bool>  (CK_UseCovariateShiftWeights,   false),
            MaxBadFoldFraction:          Cfg<double>(CK_MaxBadFoldFraction,         0.5),
            MinQualityRetentionRatio:    Cfg<double>(CK_MinQualityRetentionRatio,   0.0),
            MultiTaskMagnitudeWeight:    Cfg<double>("MLTraining:MultiTaskMagnitudeWeight", 0.3),
            CurriculumEasyFraction:      Cfg<double>("MLTraining:CurriculumEasyFraction",   0.3),
            SelfDistillTemp:             Cfg<double>("MLTraining:SelfDistillTemp",          3.0),
            FgsmEpsilon:                 Cfg<double>("MLTraining:FgsmEpsilon",              0.01),
            MinF1Score:                  Cfg<double>(CK_MinF1,                             0.10),
            UseClassWeights:             Cfg<bool>  (CK_UseClassWeights,                   true),
            MinIsotonicCalibrationSamples: Cfg<int> ("MLTraining:MinIsotonicCalibrationSamples", 50),
            FtTransformerHeads:          Cfg<int>   ("MLTraining:FtTransformerHeads",       0),
            FtTransformerArchitectureNumLayers:
                                          Cfg<int>   ("MLTraining:FtTransformerNumLayers",  0));
    }

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key does not exist or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target type (e.g. <c>int</c>, <c>double</c>, <c>bool</c>).</typeparam>
    /// <param name="ctx">EF read context — queries are always <c>AsNoTracking</c>.</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> to look up.</param>
    /// <param name="defaultValue">Value returned when the key is missing or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    /// <summary>
    /// Upserts a value into <see cref="EngineConfig"/>. If the key already exists, updates
    /// the value; otherwise inserts a new row. Used for training cost tracking metrics.
    /// </summary>
    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(ctx, key, value, ct: ct);

    // ── Quality gate failure message ──────────────────────────────────────────

    /// <summary>
    /// Builds a human-readable diagnostic string describing which quality gates the model
    /// failed and by how much. Written to <see cref="MLTrainingRun.ErrorMessage"/> and
    /// surfaced in logs so operators can tune thresholds without reading code.
    /// </summary>
    /// <param name="m">Evaluation metrics from the training run hold-out set.</param>
    /// <param name="cv">Walk-forward cross-validation result (fold accuracy statistics).</param>
    /// <param name="hp">Hyperparameter thresholds used as gate boundaries.</param>
    /// <param name="snapEce">Expected Calibration Error extracted from the model snapshot.</param>
    /// <param name="snapBss">Brier Skill Score extracted from the model snapshot.</param>
    /// <param name="newOobAccuracy">OOB accuracy of the newly trained model.</param>
    /// <param name="parentOobAccuracy">OOB accuracy of the previous champion (for regression guard).</param>
    // ── Self-tuning retry: analyze failure and queue adjusted run ──────────

    /// <summary>
    /// Track the outcome of a retrain attempt on the current-champion model for the
    /// symbol+timeframe of the failed run. On a failed retrain, increments
    /// <see cref="MLModel.ConsecutiveRetrainFailures"/>; when the count exceeds the
    /// configured threshold, retires the champion (Status=Failed, DegradationRetiredAt=now,
    /// lifecycle log). This prevents an infinite retrain loop for a strategy that has
    /// lost its edge — the drift monitor triggers a retrain, the new model fails to beat
    /// the degraded baseline, drift fires again, repeat. After N such failures the edge
    /// is almost certainly gone; retire the model and alert for human review.
    /// </summary>
    private async Task TrackRetrainOutcomeAsync(
        DbContext                  ctx,
        IWriteApplicationDbContext db,
        MLTrainingRun              run,
        bool                       promoted,
        CancellationToken          ct)
    {
        try
        {
            if (promoted) return; // Success path: new champion created fresh with counter=0; nothing to do on outgoing.

            int maxFailures = await GetConfigAsync<int>(ctx, "MLTraining:MaxConsecutiveRetrainFailures", 3, ct);
            if (maxFailures <= 0) return; // disabled

            // Find the current active champion for this symbol+timeframe
            var champion = await ctx.Set<MLModel>()
                .Where(m => m.Symbol == run.Symbol
                         && m.Timeframe == run.Timeframe
                         && m.Status == MLModelStatus.Active
                         && !m.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (champion is null) return; // No champion to track against (first-ever train for this pair)

            champion.ConsecutiveRetrainFailures++;
            _logger.LogWarning(
                "Retrain failure tracked: model {ModelId} ({Symbol}/{Tf}) now at {Count}/{Max} consecutive failures",
                champion.Id, champion.Symbol, champion.Timeframe,
                champion.ConsecutiveRetrainFailures, maxFailures);

            if (champion.ConsecutiveRetrainFailures >= maxFailures)
            {
                // Retire the champion — its edge is gone. A new strategy/model must be
                // generated via StrategyGenerationCycleRunner rather than another retrain
                // of the same architecture on the same (now-unprofitable) regime.
                champion.Status               = MLModelStatus.Failed;
                champion.IsActive             = false;
                champion.DegradationRetiredAt = DateTime.UtcNow;

                db.GetDbContext().Add(new MLModelLifecycleLog
                {
                    MLModelId = champion.Id,
                    EventType = "DegradationRetirement",
                    OccurredAt = DateTime.UtcNow,
                    Reason = $"Retired after {champion.ConsecutiveRetrainFailures} consecutive failed retrains — edge likely gone, generate a new strategy rather than retrain",
                });

                // Log at Warning rather than Error: retirement is the intended terminal
                // state of the degradation-retry policy, not a fault. Using Error triggers
                // operator error alerts for an outcome the policy is deliberately producing.
                _logger.LogWarning(
                    "Model {ModelId} ({Symbol}/{Tf}) retired due to degradation — {Count} consecutive failed retrains exceeded threshold {Max}",
                    champion.Id, champion.Symbol, champion.Timeframe,
                    champion.ConsecutiveRetrainFailures, maxFailures);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrackRetrainOutcomeAsync failed for run {RunId} — retirement path skipped", run.Id);
        }
    }

    private async Task MaybeQueueSelfTuningRetryAsync(
        DbContext                  ctx,
        IWriteApplicationDbContext db,
        MLTrainingRun              failedRun,
        EvalMetrics                metrics,
        TrainingHyperparams        hp,
        CancellationToken          ct)
    {
        try
        {
            bool enabled = await GetConfigAsync<bool>(ctx, CK_SelfTuningEnabled, true, ct);
            if (!enabled) return;

            // Skip retry if the failed run's architecture is blocked
            var blockedStr = await GetConfigAsync<string>(ctx, CK_BlockedArchitectures, "", ct);
            if (!string.IsNullOrWhiteSpace(blockedStr))
            {
                var blockedArchitectures = new HashSet<LearnerArchitecture>();
                foreach (var token in blockedStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Enum.TryParse<LearnerArchitecture>(token, ignoreCase: true, out var blockedArch))
                        blockedArchitectures.Add(blockedArch);
                }

                if (blockedArchitectures.Contains(failedRun.LearnerArchitecture))
                {
                    _logger.LogInformation(
                        "Run {RunId}: skipping self-tuning retry — architecture {Arch} is in BlockedArchitectures",
                        failedRun.Id, failedRun.LearnerArchitecture);
                    return;
                }
            }

            int maxRetries = await GetConfigAsync<int>(ctx, CK_MaxSelfTuningRetries, 2, ct);

            // Check current generation from the failed run's overrides
            int currentGen = 0;
            if (!string.IsNullOrWhiteSpace(failedRun.HyperparamConfigJson))
            {
                try
                {
                    var prev = JsonSerializer.Deserialize<HyperparamOverrides>(failedRun.HyperparamConfigJson);
                    currentGen = prev?.SelfTuningGeneration ?? 0;
                }
                catch { /* ignore parse errors */ }
            }

            if (currentGen >= maxRetries)
            {
                _logger.LogDebug("Run {RunId}: self-tuning generation {Gen} >= max {Max} — no retry",
                    failedRun.Id, currentGen, maxRetries);
                return;
            }

            // Analyze failure patterns from error message.
            //
            // CRITICAL: distinguish TECHNICAL failures (retry sensibly) from PERFORMANCE
            // failures (don't retry — you're chasing noise). A NaN loss or CUDA OOM is
            // a compute problem: different hyperparams may actually help. A low Sharpe
            // or poor F1 is the data telling you "this strategy has no edge" — retrying
            // with tweaked hyperparams overfits to whatever validation-fold coincidence
            // gave the best metric, producing a model that looks good in CV but has no
            // predictive power out-of-sample. This is the #1 operational foot-gun in
            // retail quant pipelines.
            //
            // Gate by MLTraining:SelfTuningRetryOnPerformanceFailure (default false).
            // Leave false unless you have explicit evidence that retry-on-performance
            // actually lifts live P&L over baseline. Most teams do not.
            var error = failedRun.ErrorMessage ?? "";

            bool isTechnicalFailure =
                   error.Contains("NaN", StringComparison.OrdinalIgnoreCase)
                || error.Contains("Infinity", StringComparison.OrdinalIgnoreCase)
                || error.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase)
                || error.Contains("OOM", StringComparison.OrdinalIgnoreCase)
                || error.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
                || error.Contains("divergence", StringComparison.OrdinalIgnoreCase)
                || error.Contains("diverged", StringComparison.OrdinalIgnoreCase)
                || error.Contains("singular", StringComparison.OrdinalIgnoreCase)
                || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || error.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                || error.Contains("connection", StringComparison.OrdinalIgnoreCase);

            bool retryOnPerfFailure = await GetConfigAsync<bool>(
                ctx, "MLTraining:SelfTuningRetryOnPerformanceFailure", false, ct);

            var overrides = new HyperparamOverrides
            {
                TriggeredBy          = "SelfTuningRetry",
                ParentRunId          = failedRun.Id,
                SelfTuningGeneration = currentGen + 1,
            };

            var patterns = new List<string>();

            // Always honour technical-failure patterns — they legitimately benefit from retry
            if (error.Contains("nan",      StringComparison.OrdinalIgnoreCase)) patterns.Add("nan");
            if (error.Contains("oom",      StringComparison.OrdinalIgnoreCase)) patterns.Add("oom");
            if (error.Contains("diverge",  StringComparison.OrdinalIgnoreCase)) patterns.Add("divergence");

            // Performance-metric patterns — only honour when the operator has explicitly
            // opted in. The legacy behaviour (always retry) is now opt-in.
            if (retryOnPerfFailure)
            {
                if (error.Contains("f1", StringComparison.OrdinalIgnoreCase))
                    patterns.Add("f1");
                if (error.Contains("accuracy", StringComparison.OrdinalIgnoreCase))
                    patterns.Add("accuracy");
                if (error.Contains("sharpe", StringComparison.OrdinalIgnoreCase))
                    patterns.Add("sharpe");
                if (error.Contains("ev ", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("ev -", StringComparison.OrdinalIgnoreCase))
                    patterns.Add("ev");
                if (error.Contains("brier", StringComparison.OrdinalIgnoreCase))
                    patterns.Add("brier");
            }
            else if (!isTechnicalFailure)
            {
                // Performance-only failure AND retry-on-performance is disabled — abandon.
                _logger.LogInformation(
                    "Run {RunId}: performance-only failure ({Error}) — abandoning per SelfTuningRetryOnPerformanceFailure=false",
                    failedRun.Id, error.Length > 120 ? error[..120] : error);
                return;
            }

            if (patterns.Count == 0)
            {
                _logger.LogDebug("Run {RunId}: no recognizable failure patterns for self-tuning", failedRun.Id);
                return;
            }

            overrides.FailurePatterns = string.Join(",", patterns);

            // ── Profitability-aware bias: query top models for same symbol/timeframe ──
            HyperparamOverrides? refHp = null;
            long? refModelId = null;
            try
            {
                // Find top profitable models ranked by composite score: EV*5 + Sharpe*0.1 + F1*0.5
                var topModels = await ctx.Set<MLModel>()
                    .AsNoTracking()
                    .Where(m => m.Symbol == failedRun.Symbol
                             && m.Timeframe == failedRun.Timeframe
                             && !m.IsDeleted
                             && m.ExpectedValue != null
                             && m.ExpectedValue > 0)
                    .OrderByDescending(m =>
                        (double)(m.ExpectedValue ?? 0m) * 5.0
                      + (double)(m.SharpeRatio   ?? 0m) * 0.1
                      + (double)(m.F1Score       ?? 0m) * 0.5)
                    .Take(3)
                    .ToListAsync(ct);

                foreach (var topModel in topModels)
                {
                    // Find the training run that produced this model by matching symbol/timeframe/arch
                    // and CompletedAt close to TrainedAt (within 5 minutes)
                    var matchedRun = await ctx.Set<MLTrainingRun>()
                        .AsNoTracking()
                        .Where(r => r.Symbol == topModel.Symbol
                                 && r.Timeframe == topModel.Timeframe
                                 && r.LearnerArchitecture == topModel.LearnerArchitecture
                                 && r.CompletedAt != null
                                 && r.CompletedAt >= topModel.TrainedAt.AddMinutes(-5)
                                 && r.CompletedAt <= topModel.TrainedAt.AddMinutes(5)
                                 && r.HyperparamConfigJson != null
                                 && !r.IsDeleted)
                        .OrderByDescending(r => r.CompletedAt)
                        .FirstOrDefaultAsync(ct);

                    if (matchedRun?.HyperparamConfigJson is null) continue;

                    try
                    {
                        refHp = JsonSerializer.Deserialize<HyperparamOverrides>(matchedRun.HyperparamConfigJson);
                        if (refHp is not null)
                        {
                            refModelId = topModel.Id;
                            var compositeScore = (double)(topModel.ExpectedValue ?? 0m) * 5.0
                                               + (double)(topModel.SharpeRatio   ?? 0m) * 0.1
                                               + (double)(topModel.F1Score       ?? 0m) * 0.5;
                            _logger.LogInformation(
                                "Self-tuning: using reference model {ModelId} (score={Score:F3}, EV={EV:F4}, Sharpe={Sharpe:F2}, F1={F1:F3}) hyperparams to bias retry for run {RunId}",
                                topModel.Id, compositeScore,
                                topModel.ExpectedValue, topModel.SharpeRatio, topModel.F1Score,
                                failedRun.Id);
                            break;
                        }
                    }
                    catch { /* ignore deserialization errors, try next model */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Run {RunId}: profitability reference lookup failed — falling back to incremental adjustments", failedRun.Id);
            }

            // ── Apply adjustments: profitability-biased when reference exists, incremental fallback otherwise ──
            if (patterns.Contains("f1"))
            {
                if (refHp?.FpCostWeight is not null)
                {
                    // Blend 70% toward the profitable model's FpCostWeight, 30% current
                    overrides.FpCostWeight = Math.Min(hp.FpCostWeight * 0.3 + refHp.FpCostWeight.Value * 0.7, 0.85);
                }
                else
                {
                    overrides.FpCostWeight = Math.Min(hp.FpCostWeight + 0.15, 0.85);
                }

                overrides.UseClassWeights = refHp?.UseClassWeights ?? true;
            }

            if (patterns.Contains("accuracy"))
            {
                overrides.K = refHp?.K ?? hp.K + 3;
                overrides.MaxEpochs = refHp?.MaxEpochs ?? (int)(hp.MaxEpochs * 1.5);
            }

            if (patterns.Contains("sharpe"))
            {
                if (refHp?.TemporalDecayLambda is not null)
                {
                    // Blend 70% toward the profitable model's decay, 30% current
                    overrides.TemporalDecayLambda = hp.TemporalDecayLambda * 0.3 + refHp.TemporalDecayLambda.Value * 0.7;
                }
                else
                {
                    overrides.TemporalDecayLambda = hp.TemporalDecayLambda * 1.5;
                }
            }

            if (patterns.Contains("ev"))
            {
                // Widen barriers symmetrically so the retry still satisfies the
                // symmetric-barrier guard in ProcessRunAsync. Previous asymmetric
                // tweak (profit × 1.2, stop × 0.8) produced a 1.5× ratio that
                // tripped the guard on attempt 1 and kept the run in a retry loop.
                // Symmetric widening raises the signal-to-noise on labels without
                // introducing directional bias — same correction direction, safer math.
                const double symmetricWiden = 1.1;
                if (refHp?.UseTripleBarrier == true)
                {
                    overrides.UseTripleBarrier = true;
                    overrides.TripleBarrierProfitAtrMult = refHp.TripleBarrierProfitAtrMult
                        ?? hp.TripleBarrierProfitAtrMult * symmetricWiden;
                    overrides.TripleBarrierStopAtrMult = refHp.TripleBarrierStopAtrMult
                        ?? hp.TripleBarrierStopAtrMult * symmetricWiden;
                }
                else
                {
                    overrides.UseTripleBarrier = true;
                    overrides.TripleBarrierProfitAtrMult = hp.TripleBarrierProfitAtrMult * symmetricWiden;
                    overrides.TripleBarrierStopAtrMult = hp.TripleBarrierStopAtrMult * symmetricWiden;
                }
            }

            if (patterns.Contains("brier"))
            {
                overrides.L2Lambda = refHp?.L2Lambda ?? hp.L2Lambda * 2.0;
            }

            // Determine architecture — switch to SMOTE for label imbalance
            var arch = failedRun.LearnerArchitecture;
            if (error.Contains("Label imbalance", StringComparison.OrdinalIgnoreCase))
            {
                arch = LearnerArchitecture.Smote;
                overrides.UseClassWeights = true;
            }

            var now = DateTime.UtcNow;
            // 1825-day default (widened from 730 and originally 365) for chronic-failure
            // retries — bounded by actual candle history available.
            int windowDays = await GetConfigAsync<int>(ctx, "MLTraining:TrainingDataWindowDays", 1825, ct);

            ctx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol              = failedRun.Symbol,
                Timeframe           = failedRun.Timeframe,
                TriggerType         = TriggerType.AutoDegrading,
                Status              = RunStatus.Queued,
                FromDate            = now.AddDays(-windowDays),
                ToDate              = now,
                StartedAt           = now,
                LearnerArchitecture = arch,
                HyperparamConfigJson = JsonSerializer.Serialize(overrides),
            });

            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Self-tuning: queued retry for run {RunId} ({Symbol}/{Tf}) gen={Gen} patterns=[{Patterns}] arch={Arch} refModel={RefModelId}",
                failedRun.Id, failedRun.Symbol, failedRun.Timeframe,
                currentGen + 1, overrides.FailurePatterns, arch,
                refModelId.HasValue ? refModelId.Value.ToString() : "none");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Self-tuning retry failed for run {RunId} — non-critical", failedRun.Id);
        }
    }

    private static (bool IsValid, string Issues) ValidateGbmPromotionSnapshot(ModelSnapshot snapshot)
    {
        var normalized = GbmSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = GbmSnapshotSupport.ValidateSnapshot(normalized, allowLegacy: false);
        var issues = new List<string>(validation.Issues);

        if (normalized.TrainingSplitSummary is null)
        {
            issues.Add("TrainingSplitSummary is missing.");
        }
        else
        {
            if (normalized.TrainingSplitSummary.SelectionCount <= 0)
                issues.Add("GBM snapshots must persist a non-empty selection split.");
            if (normalized.TrainingSplitSummary.CalibrationCount <= 0)
                issues.Add("GBM snapshots must persist a non-empty calibration split.");
            if (normalized.TrainingSplitSummary.TestCount <= 0)
                issues.Add("GBM snapshots must persist a non-empty test split.");
        }

        if (normalized.GbmSelectionMetrics is null)
            issues.Add("GbmSelectionMetrics is missing.");
        if (normalized.GbmCalibrationMetrics is null)
            issues.Add("GbmCalibrationMetrics is missing.");
        if (normalized.GbmTestMetrics is null)
            issues.Add("GbmTestMetrics is missing.");
        if (normalized.GbmCalibrationArtifact is null)
            issues.Add("GbmCalibrationArtifact is missing.");

        if (normalized.GbmAuditArtifact is null)
        {
            issues.Add("GbmAuditArtifact is missing.");
        }
        else
        {
            if (!normalized.GbmAuditArtifact.SnapshotContractValid)
                issues.Add("GbmAuditArtifact reported an invalid snapshot contract.");
            if (normalized.GbmAuditArtifact.ThresholdDecisionMismatchCount > 0)
            {
                issues.Add(
                    $"GbmAuditArtifact reported {normalized.GbmAuditArtifact.ThresholdDecisionMismatchCount} threshold decision mismatches.");
            }
            if (normalized.GbmAuditArtifact.Findings.Length > 0)
                issues.Add($"GbmAuditArtifact reported findings: {string.Join(" | ", normalized.GbmAuditArtifact.Findings)}");
        }

        if (!double.IsFinite(normalized.GbmTrainInferenceParityMaxError) ||
            normalized.GbmTrainInferenceParityMaxError > 1e-6)
        {
            issues.Add(
                $"GbmTrainInferenceParityMaxError {normalized.GbmTrainInferenceParityMaxError:G} exceeded the 1e-6 promotion limit.");
        }

        return (issues.Count == 0, string.Join("; ", issues.Distinct()));
    }

    private static (bool IsValid, string Issues) ValidateFtTransformerPromotionSnapshot(ModelSnapshot snapshot)
    {
        var normalized = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = FtTransformerSnapshotSupport.ValidateNormalizedSnapshot(normalized);
        var issues = new List<string>(validation.Issues);

        if (normalized.TrainingSplitSummary is null)
        {
            issues.Add("TrainingSplitSummary is missing.");
        }
        else
        {
            if (normalized.TrainingSplitSummary.MetaLabelCount != 0)
                issues.Add("FT-Transformer snapshots must not advertise meta-label calibration splits.");
            if (normalized.TrainingSplitSummary.AbstentionCount != 0)
                issues.Add("FT-Transformer snapshots must not advertise abstention calibration splits.");
            if (normalized.TrainingSplitSummary.SelectionPruningCount <= 0 ||
                normalized.TrainingSplitSummary.SelectionThresholdCount <= 0 ||
                normalized.TrainingSplitSummary.SelectionKellyCount <= 0)
            {
                issues.Add("FT-Transformer snapshots must persist non-empty selection pruning, threshold, and Kelly sub-splits.");
            }
        }

        if (normalized.FtTransformerSelectionMetrics is null)
            issues.Add("FtTransformerSelectionMetrics is missing.");
        if (normalized.FtTransformerCalibrationMetrics is null)
            issues.Add("FtTransformerCalibrationMetrics is missing.");
        if (normalized.FtTransformerTestMetrics is null)
            issues.Add("FtTransformerTestMetrics is missing.");
        if (normalized.FtTransformerCalibrationArtifact is null)
            issues.Add("FtTransformerCalibrationArtifact is missing.");
        else if (normalized.TrainingSplitSummary is not null)
        {
            if (normalized.FtTransformerCalibrationArtifact.AdaptiveHeadCrossFitFoldCount !=
                normalized.TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount)
            {
                issues.Add("FtTransformerCalibrationArtifact cross-fit metadata does not match TrainingSplitSummary.");
            }

            if (normalized.FtTransformerCalibrationArtifact.ThresholdSelectionSampleCount > 0 &&
                normalized.FtTransformerCalibrationArtifact.ThresholdSelectionSampleCount !=
                normalized.TrainingSplitSummary.SelectionThresholdCount)
            {
                issues.Add("FtTransformerCalibrationArtifact threshold-selection sample count does not match TrainingSplitSummary.");
            }
            if (normalized.FtTransformerCalibrationArtifact.KellySelectionSampleCount > 0 &&
                normalized.FtTransformerCalibrationArtifact.KellySelectionSampleCount !=
                normalized.TrainingSplitSummary.SelectionKellyCount)
            {
                issues.Add("FtTransformerCalibrationArtifact Kelly-selection sample count does not match TrainingSplitSummary.");
            }

            if (normalized.FtTransformerCalibrationArtifact.KellySelectionSampleCount <= 0)
                issues.Add("FtTransformerCalibrationArtifact Kelly-selection sample count is missing.");
            if (normalized.FtTransformerCalibrationArtifact.RefitSampleCount <
                normalized.FtTransformerCalibrationArtifact.FitSampleCount)
            {
                issues.Add("FtTransformerCalibrationArtifact refit sample count is inconsistent.");
            }
            if (normalized.FtTransformerCalibrationArtifact.RoutingThresholdCandidateCount > 0)
            {
                if (normalized.FtTransformerCalibrationArtifact.RoutingThresholdCandidates.Length !=
                    normalized.FtTransformerCalibrationArtifact.RoutingThresholdCandidateCount ||
                    normalized.FtTransformerCalibrationArtifact.RoutingThresholdCandidateNlls.Length !=
                    normalized.FtTransformerCalibrationArtifact.RoutingThresholdCandidateCount ||
                    normalized.FtTransformerCalibrationArtifact.RoutingThresholdCandidateEces.Length !=
                    normalized.FtTransformerCalibrationArtifact.RoutingThresholdCandidateCount)
                {
                    issues.Add("FtTransformerCalibrationArtifact routing-threshold candidate metadata is inconsistent.");
                }
            }
        }
        if (normalized.FtTransformerWarmStartArtifact is null)
            issues.Add("FtTransformerWarmStartArtifact is missing.");

        if (normalized.FtTransformerAuditArtifact is null)
        {
            issues.Add("FtTransformerAuditArtifact is missing.");
        }
        else
        {
            if (!normalized.FtTransformerAuditArtifact.SnapshotContractValid)
                issues.Add("FtTransformerAuditArtifact reported an invalid snapshot contract.");
            if (normalized.FtTransformerAuditArtifact.ThresholdDecisionMismatchCount > 0)
                issues.Add($"FtTransformerAuditArtifact reported {normalized.FtTransformerAuditArtifact.ThresholdDecisionMismatchCount} threshold decision mismatches.");
            if (normalized.FtTransformerAuditArtifact.Findings.Length > 0)
                issues.Add($"FtTransformerAuditArtifact reported findings: {string.Join(" | ", normalized.FtTransformerAuditArtifact.Findings)}");
        }

        if (!double.IsFinite(normalized.FtTransformerTrainInferenceParityMaxError) ||
            normalized.FtTransformerTrainInferenceParityMaxError > 1e-6)
        {
            issues.Add($"FtTransformerTrainInferenceParityMaxError {normalized.FtTransformerTrainInferenceParityMaxError:G} exceeded the 1e-6 promotion limit.");
        }

        return (issues.Count == 0, string.Join("; ", issues.Distinct()));
    }

    private static (bool IsValid, string Issues) ValidateAdaBoostPromotionSnapshot(ModelSnapshot snapshot)
    {
        var normalized = AdaBoostSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = AdaBoostSnapshotSupport.ValidateSnapshot(normalized, allowLegacy: false);
        var issues = new List<string>(validation.Issues);

        if (normalized.TrainingSplitSummary is null)
        {
            issues.Add("TrainingSplitSummary is missing.");
        }
        else
        {
            if (normalized.TrainingSplitSummary.SelectionPruningCount <= 0 ||
                normalized.TrainingSplitSummary.SelectionThresholdCount <= 0)
            {
                issues.Add("AdaBoost snapshots must persist non-empty selection pruning and threshold sub-splits.");
            }
        }

        if (normalized.AdaBoostSelectionMetrics is null)
            issues.Add("AdaBoostSelectionMetrics is missing.");
        if (normalized.AdaBoostCalibrationMetrics is null)
            issues.Add("AdaBoostCalibrationMetrics is missing.");
        if (normalized.AdaBoostTestMetrics is null)
            issues.Add("AdaBoostTestMetrics is missing.");

        if (normalized.AdaBoostCalibrationArtifact is null)
        {
            issues.Add("AdaBoostCalibrationArtifact is missing.");
        }
        else if (normalized.TrainingSplitSummary is not null)
        {
            if (normalized.AdaBoostCalibrationArtifact.ThresholdSelectionSampleCount > 0 &&
                normalized.AdaBoostCalibrationArtifact.ThresholdSelectionSampleCount !=
                normalized.TrainingSplitSummary.SelectionThresholdCount)
            {
                issues.Add("AdaBoostCalibrationArtifact threshold-selection sample count does not match TrainingSplitSummary.");
            }

            if (normalized.AdaBoostCalibrationArtifact.MetaLabelSampleCount > 0 &&
                normalized.AdaBoostCalibrationArtifact.MetaLabelSampleCount !=
                normalized.TrainingSplitSummary.MetaLabelCount)
            {
                issues.Add("AdaBoostCalibrationArtifact meta-label sample count does not match TrainingSplitSummary.");
            }

            if (normalized.AdaBoostCalibrationArtifact.AbstentionSampleCount > 0 &&
                normalized.AdaBoostCalibrationArtifact.AbstentionSampleCount !=
                normalized.TrainingSplitSummary.AbstentionCount)
            {
                issues.Add("AdaBoostCalibrationArtifact abstention sample count does not match TrainingSplitSummary.");
            }
        }

        if (normalized.AdaBoostAuditArtifact is null)
        {
            issues.Add("AdaBoostAuditArtifact is missing.");
        }
        else
        {
            if (!normalized.AdaBoostAuditArtifact.SnapshotContractValid)
                issues.Add("AdaBoostAuditArtifact reported an invalid snapshot contract.");
            if (normalized.AdaBoostAuditArtifact.ThresholdDecisionMismatchCount > 0)
            {
                issues.Add(
                    $"AdaBoostAuditArtifact reported {normalized.AdaBoostAuditArtifact.ThresholdDecisionMismatchCount} threshold decision mismatches.");
            }
            if (normalized.AdaBoostAuditArtifact.Findings.Length > 0)
                issues.Add($"AdaBoostAuditArtifact reported findings: {string.Join(" | ", normalized.AdaBoostAuditArtifact.Findings)}");
        }

        return (issues.Count == 0, string.Join("; ", issues.Distinct()));
    }

    private static string BuildGateFailureMessage(
        EvalMetrics         m,
        WalkForwardResult   cv,
        TrainingHyperparams hp,
        double              snapEce            = 0.0,
        double              snapBss            = double.NegativeInfinity,
        double              newOobAccuracy     = 0.0,
        double              parentOobAccuracy  = 0.0,
        bool                isTrending         = false,
        double              trendingMinAccuracy = 0.65,
        double              trendingMinEV       = 0.02,
        bool                evBypassF1         = false)
    {
        var failed = new List<string>(8);

        if (m.Accuracy        < hp.MinAccuracyToPromote)
            failed.Add($"accuracy {m.Accuracy:P1} < {hp.MinAccuracyToPromote:P1}");
        if (m.ExpectedValue   < hp.MinExpectedValue)
            failed.Add($"ev {m.ExpectedValue:F4} < {hp.MinExpectedValue:F4}");
        if (m.BrierScore      > hp.MaxBrierScore)
            failed.Add($"brier {m.BrierScore:F4} > {hp.MaxBrierScore:F4}");
        if (m.SharpeRatio     < hp.MinSharpeRatio)
            failed.Add($"sharpe {m.SharpeRatio:F2} < {hp.MinSharpeRatio:F2}");

        // Regime-conditional F1 gate
        if (isTrending)
        {
            bool f1Ok = m.F1 >= hp.MinF1Score
                     || (m.Accuracy >= trendingMinAccuracy && m.ExpectedValue >= trendingMinEV);
            if (!f1Ok)
                failed.Add($"f1 {m.F1:F3} < {hp.MinF1Score:F3} (trending bypass requires acc≥{trendingMinAccuracy:P0} + ev≥{trendingMinEV:F3})");
        }
        else if (hp.MinF1Score > 0 && m.F1 < hp.MinF1Score && !evBypassF1)
        {
            failed.Add($"f1 {m.F1:F3} < {hp.MinF1Score:F3}");
        }
        if (cv.StdAccuracy    > hp.MaxWalkForwardStdDev)
            failed.Add($"wf_std {cv.StdAccuracy:P1} > {hp.MaxWalkForwardStdDev:P1}");
        if (hp.MaxEce > 0 && snapEce > hp.MaxEce)
            failed.Add($"ece {snapEce:F4} > {hp.MaxEce:F4}");
        if (hp.MinBrierSkillScore > -1.0 && snapBss < hp.MinBrierSkillScore)
            failed.Add($"bss {snapBss:F4} < {hp.MinBrierSkillScore:F4}");
        if (hp.MinQualityRetentionRatio > 0.0 && parentOobAccuracy > 0.0 &&
            newOobAccuracy < parentOobAccuracy * hp.MinQualityRetentionRatio)
            failed.Add($"oob_regression {newOobAccuracy:P1} < parent {parentOobAccuracy:P1} × {hp.MinQualityRetentionRatio:F2}");

        return $"Quality gate failed: {string.Join(", ", failed)}";
    }

    // ── Training data poisoning detection ─────────────────────────────────────

    /// <summary>
    /// Validates candle data for common data quality issues that could poison ML training:
    /// zero/negative OHLC, inverted High/Low, extreme single-bar price spikes (&gt;20%),
    /// and suspiciously identical consecutive closes (&gt;10 in a row indicating a stuck feed).
    /// </summary>
    private static List<CandleValidationWarning> ValidateCandleData(List<Candle> candles)
    {
        var warnings = new List<CandleValidationWarning>();
        int consecutiveIdenticalCloses = 1;

        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];

            // Check for zero or negative OHLC values
            if (c.Open <= 0 || c.High <= 0 || c.Low <= 0 || c.Close <= 0)
            {
                warnings.Add(new CandleValidationWarning(i, c.Timestamp, "ZeroOrNegativeOHLC"));
                consecutiveIdenticalCloses = 1;
                continue; // skip further checks on invalid candle
            }

            // Check for inverted High/Low
            if (c.High < c.Low)
            {
                warnings.Add(new CandleValidationWarning(i, c.Timestamp, "HighLessThanLow"));
            }

            // Check for extreme price spikes (close-to-close return > 20%)
            if (i > 0 && candles[i - 1].Close > 0)
            {
                double prevClose = (double)candles[i - 1].Close;
                double returnPct = Math.Abs(((double)c.Close - prevClose) / prevClose);
                if (returnPct > 0.20)
                {
                    warnings.Add(new CandleValidationWarning(i, c.Timestamp, "ExtremePriceSpike"));
                }
            }

            // Check for suspiciously identical consecutive closes (>10 in a row)
            if (i > 0 && c.Close == candles[i - 1].Close)
            {
                consecutiveIdenticalCloses++;
                if (consecutiveIdenticalCloses > 10)
                {
                    warnings.Add(new CandleValidationWarning(i, c.Timestamp, "IdenticalConsecutiveCloses"));
                }
            }
            else
            {
                consecutiveIdenticalCloses = 1;
            }
        }

        return warnings;
    }

    /// <summary>Per-candle validation warning with position and reason.</summary>
    private readonly record struct CandleValidationWarning(int Index, DateTime Timestamp, string Reason);
}
