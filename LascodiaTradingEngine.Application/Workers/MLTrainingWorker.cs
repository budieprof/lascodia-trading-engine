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
                    await ProcessRunAsync(run, db, ctx, scope.ServiceProvider, stoppingToken);
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
    private static async Task<MLTrainingRun?> ClaimNextRunAsync(
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

        // ── Claim one queued run atomically ───────────────────────────────
        // Atomic UPDATE: only succeeds for one worker even if many race concurrently.
        var rowsUpdated = await runSet
            .Where(r => r.Status == RunStatus.Queued && r.WorkerInstanceId == null &&
                        (r.NextRetryAt == null || r.NextRetryAt <= DateTime.UtcNow))
            .OrderBy(r => r.StartedAt)
            .Take(1)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status,           RunStatus.Running)
                .SetProperty(r => r.PickedUpAt,       DateTime.UtcNow)
                .SetProperty(r => r.WorkerInstanceId, _instanceId),
                ct);

        if (rowsUpdated == 0)
            return null;

        var now = DateTime.UtcNow;
        return await runSet.FirstOrDefaultAsync(
            r => r.WorkerInstanceId == _instanceId &&
                 r.Status           == RunStatus.Running &&
                 r.PickedUpAt       >= now.AddSeconds(-10),
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
                        // Count freshness bounces toward the retry budget so the run
                        // eventually fails with an alert instead of bouncing forever
                        // when market data ingestion is down.
                        run.AttemptCount++;
                        if (run.AttemptCount >= run.MaxAttempts)
                        {
                            run.Status      = RunStatus.Failed;
                            run.CompletedAt = DateTime.UtcNow;
                            run.ErrorMessage =
                                $"Freshness gate: latest {run.Symbol}/{run.Timeframe} candle is " +
                                $"{ageMinutes:F0} min old (threshold: {maxCandleAgeMinutes} min). " +
                                $"Permanently failed after {run.AttemptCount} freshness bounces.";
                            await db.SaveChangesAsync(stoppingToken);
                            _logger.LogError(
                                "Run {RunId}: freshness gate permanently failed after {Attempts} bounces.",
                                run.Id, run.AttemptCount);
                            await MaybeCreateTrainingFailureAlertAsync(ctx, run, stoppingToken);
                            return;
                        }

                        run.Status           = RunStatus.Queued;
                        run.WorkerInstanceId = null;
                        run.NextRetryAt      = DateTime.UtcNow.AddMinutes(30);
                        run.ErrorMessage     =
                            $"Freshness gate: latest {run.Symbol}/{run.Timeframe} candle is " +
                            $"{ageMinutes:F0} min old (threshold: {maxCandleAgeMinutes} min). " +
                            $"Re-queued (attempt {run.AttemptCount}/{run.MaxAttempts}).";
                        await db.SaveChangesAsync(stoppingToken);
                        _logger.LogWarning(
                            "Run {RunId}: stale data gate — latest {Symbol}/{Tf} candle is {Age:F0} min old " +
                            "(threshold: {Max} min). Re-queuing with 30 min delay (attempt {Attempt}/{Max}).",
                            run.Id, run.Symbol, run.Timeframe, ageMinutes, maxCandleAgeMinutes,
                            run.AttemptCount, run.MaxAttempts);
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
            var samples = hp.UseTripleBarrier
                ? MLFeatureHelper.BuildTrainingSamplesWithTripleBarrier(
                    candles, CotLookup,
                    (float)hp.TripleBarrierProfitAtrMult,
                    (float)hp.TripleBarrierStopAtrMult,
                    hp.TripleBarrierHorizonBars)
                : MLFeatureHelper.BuildTrainingSamples(candles, CotLookup);
            _logger.LogInformation(
                "Built {Samples} training samples (features={Feat})",
                samples.Count, MLFeatureHelper.FeatureCount);

            if (samples.Count < hp.MinSamples)
                throw new InvalidOperationException(
                    $"Insufficient training samples: {samples.Count} < {hp.MinSamples}");

            run.TotalSamples = samples.Count;

            // ── Class imbalance guard ────────────────────────────────────────
            // A heavily skewed label distribution silently biases the model toward the majority
            // direction. Reject the run if imbalance exceeds the configurable ceiling so we only
            // train on reasonably balanced data.
            int     buyCount         = samples.Count(s => s.Direction == 1);
            int     sellCount        = samples.Count - buyCount;
            decimal imbalanceRatio   = samples.Count > 0 ? (decimal)buyCount / samples.Count : 0.5m;
            run.LabelImbalanceRatio  = imbalanceRatio;

            double maxImbalance = await GetConfigAsync<double>(ctx, CK_MaxLabelImbalance, 0.65, stoppingToken);
            if ((double)imbalanceRatio > maxImbalance || (double)imbalanceRatio < 1.0 - maxImbalance)
            {
                throw new InvalidOperationException(
                    $"Label imbalance {imbalanceRatio:P1} exceeds threshold {maxImbalance:P0} " +
                    $"(buy={buyCount} sell={sellCount} total={samples.Count}). " +
                    $"Expand the training window or review label logic.");
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
            if (result.ModelBytes is { Length: > 0 })
            {
                try
                {
                    var snapForGate = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
                    if (snapForGate is not null)
                    {
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

            bool f1Passed = isTrending
                ? (m.F1 >= hp.MinF1Score || (m.Accuracy >= trendingMinAccuracy && m.ExpectedValue >= trendingMinEV))
                : (hp.MinF1Score <= 0 || m.F1 >= hp.MinF1Score);

            bool passed =
                m.Accuracy           >= hp.MinAccuracyToPromote                                    &&
                m.ExpectedValue      >= hp.MinExpectedValue                                        &&
                m.BrierScore         <= hp.MaxBrierScore                                           &&
                m.SharpeRatio        >= hp.MinSharpeRatio                                          &&
                f1Passed                                                                           &&
                cvCheck.StdAccuracy  <= hp.MaxWalkForwardStdDev                                    &&
                (hp.MaxEce <= 0 || snapEce <= hp.MaxEce)                                           &&
                (hp.MinBrierSkillScore <= -1.0 || snapBss >= hp.MinBrierSkillScore)                &&
                !qualityRegressionFailed;

            _logger.LogInformation(
                "Quality gates — acc={Acc:P1}/{MinAcc:P1} ev={EV:F4}/{MinEV:F4} " +
                "brier={Brier:F4}/{MaxBrier:F4} sharpe={Sharpe:F2}/{MinSharpe:F2} " +
                "f1={F1:F3}/{MinF1:F3} regime={Regime} f1Passed={F1Passed} " +
                "wfStd={WfStd:P1}/{MaxWfStd:P1} ece={Ece:F4}/{MaxEce:F4} " +
                "bss={Bss:F4}/{MinBss:F4} oobReg={OobNew:P1}/{OobParent:P1} passed={Passed}",
                m.Accuracy,              hp.MinAccuracyToPromote,
                m.ExpectedValue,         hp.MinExpectedValue,
                m.BrierScore,            hp.MaxBrierScore,
                m.SharpeRatio,           hp.MinSharpeRatio,
                m.F1,                    hp.MinF1Score,
                currentRegime?.ToString() ?? "unknown", f1Passed,
                cvCheck.StdAccuracy,     hp.MaxWalkForwardStdDev,
                snapEce,                 hp.MaxEce,
                snapBss,                 hp.MinBrierSkillScore,
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
                isTrending, trendingMinAccuracy, trendingMinEV);

            if (!passed)
            {
                await db.SaveChangesAsync(stoppingToken);
                sp.GetRequiredService<ITrainerSelector>()
                    .InvalidateCache(run.Symbol, run.Timeframe);
                _logger.LogWarning("Run {RunId} did not pass quality gates — model not promoted", run.Id);
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

                bool newModelIsBetter =
                    newScore > champScore                           // composite score is higher
                    || (newF1 > champF1 + 0.15 && newEV >= -0.01)  // significantly more balanced with non-negative EV
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
                    run.MLModelId = nonActiveModel.Id;
                    await db.SaveChangesAsync(stoppingToken);

                    // Create shadow evaluation so the arbiter can compare them on live data
                    var shadow = new MLShadowEvaluation
                    {
                        Symbol            = run.Symbol,
                        Timeframe         = run.Timeframe,
                        ChampionModelId   = previousChampion.Id,
                        ChallengerModelId = nonActiveModel.Id,
                        Status            = ShadowEvaluationStatus.Running,
                        RequiredTrades    = hp.ShadowRequiredTrades,
                        ExpiresAt         = DateTime.UtcNow.AddDays(hp.ShadowExpiryDays),
                        PromotionThreshold = (decimal)hp.MinAccuracyToPromote,
                        StartedAt         = DateTime.UtcNow,
                    };
                    ctx.Set<MLShadowEvaluation>().Add(shadow);
                    await db.SaveChangesAsync(stoppingToken);

                    sp.GetRequiredService<ITrainerSelector>().InvalidateCache(run.Symbol, run.Timeframe);
                    return;
                }

                // New model is better — proceed with promotion
                await SnapshotChampionPerformanceAsync(previousChampion, ctx, stoppingToken);
                previousChampion.IsActive = false;
                previousChampion.Status   = MLModelStatus.Superseded;

                _logger.LogInformation(
                    "Promoting run {RunId} over champion {ChampId}: new score={NewScore:F4} > champ score={ChampScore:F4}",
                    run.Id, previousChampion.Id, newScore, champScore);
            }

            var (finalModelBytes, plattA, plattB) = await PatchSnapshotAsync(
                result.ModelBytes, run, candles, samples, buyCount, sellCount,
                imbalanceRatio, cotNetMin, cotNetMax, cotMomMin, cotMomMax,
                ctx, stoppingToken);

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

            // First save to get the model Id, then link the run
            await db.SaveChangesAsync(stoppingToken);
            run.MLModelId = model.Id;

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
                };
                ctx.Set<MLShadowEvaluation>().Add(shadow);
                _logger.LogInformation(
                    "Shadow eval queued: champion={Champion} vs challenger={Challenger}",
                    previousChampion.Id, model.Id);
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
                            // dates, which may be stale. Default to 365 days matching the drift
                            // workers' MLTraining:TrainingDataWindowDays default.
                            var shadowNow = DateTime.UtcNow;
                            int windowDays = await GetConfigAsync<int>(
                                ctx, "MLTraining:TrainingDataWindowDays", 365, stoppingToken);

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
            bool canRetry = run.AttemptCount < run.MaxAttempts;

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

                _logger.LogError(ex,
                    "Run {RunId} permanently failed after {Attempts} attempt(s).", run.Id, run.AttemptCount);
            }

            try { await db.SaveChangesAsync(CancellationToken.None); }
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

            plattA = (decimal)snap.PlattA;
            plattB = (decimal)snap.PlattB;

            // Inject training-window COT normalisation bounds
            snap.CotNetNormMin = cotNetMin;
            snap.CotNetNormMax = cotNetMax;
            snap.CotMomNormMin = cotMomMin;
            snap.CotMomNormMax = cotMomMax;

            // Compute per-feature empirical variances from the standardised matrix.
            // Under N(0,1) each variance should be ≈1.0; drift shows as deviation.
            if (samples.Count > 0 && snap.Means.Length == MLFeatureHelper.FeatureCount)
            {
                int f = MLFeatureHelper.FeatureCount;
                var variances = new double[f];
                foreach (var s in samples)
                {
                    for (int fi = 0; fi < f && fi < s.Features.Length; fi++)
                    {
                        double std = snap.Stds.Length > fi && snap.Stds[fi] > 0
                            ? snap.Stds[fi] : 1.0;
                        double z = (s.Features[fi] - snap.Means[fi]) / std;
                        variances[fi] += z * z;
                    }
                }
                for (int fi = 0; fi < f; fi++)
                    variances[fi] /= samples.Count;
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
                Channel       = AlertChannel.Webhook,
                Destination   = destination,
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
            UseTripleBarrier:            Cfg<bool>  (CK_UseTripleBarrier,            false),
            TripleBarrierProfitAtrMult:  Cfg<double>(CK_TripleBarrierProfitAtrMult,  1.5),
            TripleBarrierStopAtrMult:    Cfg<double>(CK_TripleBarrierStopAtrMult,    1.0),
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
            UseClassWeights:             Cfg<bool>  (CK_UseClassWeights,                   true));
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

            // Analyze failure patterns from error message
            var error = failedRun.ErrorMessage ?? "";
            var overrides = new HyperparamOverrides
            {
                TriggeredBy          = "SelfTuningRetry",
                ParentRunId          = failedRun.Id,
                SelfTuningGeneration = currentGen + 1,
            };

            var patterns = new List<string>();

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
                if (refHp?.UseTripleBarrier == true)
                {
                    overrides.UseTripleBarrier = true;
                    overrides.TripleBarrierProfitAtrMult = refHp.TripleBarrierProfitAtrMult
                        ?? hp.TripleBarrierProfitAtrMult * 1.2;
                    overrides.TripleBarrierStopAtrMult = refHp.TripleBarrierStopAtrMult
                        ?? hp.TripleBarrierStopAtrMult * 0.8;
                }
                else
                {
                    overrides.UseTripleBarrier = true;
                    overrides.TripleBarrierProfitAtrMult = hp.TripleBarrierProfitAtrMult * 1.2;
                    overrides.TripleBarrierStopAtrMult = hp.TripleBarrierStopAtrMult * 0.8;
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
            int windowDays = await GetConfigAsync<int>(ctx, "MLTraining:TrainingDataWindowDays", 365, ct);

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
        double              trendingMinEV       = 0.02)
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
        else if (hp.MinF1Score > 0 && m.F1 < hp.MinF1Score)
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
}
