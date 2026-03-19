using System.Diagnostics;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Slim orchestrator that processes queued <see cref="MLTrainingRun"/> records.
/// All feature engineering is delegated to <see cref="MLFeatureHelper"/>;
/// all model fitting is delegated to <see cref="IMLModelTrainer"/>.
///
/// Concurrency guarantee: the worker atomically claims a run via
/// <c>ExecuteUpdateAsync</c> + a per-process <see cref="_instanceId"/> Guid,
/// so multiple worker instances running in parallel cannot process the same run.
/// </summary>
public sealed class MLTrainingWorker : BackgroundService
{
    // ── Per-process identity (TOCTOU-safe atomic claim) ───────────────────────
    private static readonly Guid _instanceId = Guid.NewGuid();

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

    public MLTrainingWorker(
        IServiceScopeFactory       scopeFactory,
        ILogger<MLTrainingWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

                await ProcessRunAsync(run, db, ctx, scope.ServiceProvider, stoppingToken);
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

    private static async Task<MLTrainingRun?> ClaimNextRunAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        var runSet = ctx.Set<MLTrainingRun>();

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

    private async Task ProcessRunAsync(
        MLTrainingRun                           run,
        IWriteApplicationDbContext              db,
        Microsoft.EntityFrameworkCore.DbContext ctx,
        IServiceProvider                        sp,
        CancellationToken                       stoppingToken)
    {
        var sw = Stopwatch.StartNew();

        var mediator = sp.GetRequiredService<IMediator>();
        IEventBus? eventBus = null;
        try { eventBus = sp.GetService<IEventBus>(); } catch { /* optional */ }

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
                            K                   = overrides.K                   ?? hp.K,
                            LearningRate        = overrides.LearningRate        ?? hp.LearningRate,
                            L2Lambda            = overrides.L2Lambda            ?? hp.L2Lambda,
                            TemporalDecayLambda = overrides.TemporalDecayLambda ?? hp.TemporalDecayLambda,
                            MaxEpochs           = overrides.MaxEpochs           ?? hp.MaxEpochs,
                            EmbargoBarCount     = overrides.EmbargoBarCount     ?? hp.EmbargoBarCount,
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
                        run.Status           = RunStatus.Queued;
                        run.WorkerInstanceId = null;
                        run.NextRetryAt      = DateTime.UtcNow.AddMinutes(30);
                        run.ErrorMessage     =
                            $"Freshness gate: latest {run.Symbol}/{run.Timeframe} candle is " +
                            $"{ageMinutes:F0} min old (threshold: {maxCandleAgeMinutes} min). Re-queued.";
                        await db.SaveChangesAsync(stoppingToken);
                        _logger.LogWarning(
                            "Run {RunId}: stale data gate — latest {Symbol}/{Tf} candle is {Age:F0} min old " +
                            "(threshold: {Max} min). Re-queuing with 30 min delay.",
                            run.Id, run.Symbol, run.Timeframe, ageMinutes, maxCandleAgeMinutes);
                        return;
                    }
                }
            }

            // ── Load COT data for base currency ─────────────────────────────
            var baseCurrency = run.Symbol.Length >= 3 ? run.Symbol[..3] : run.Symbol;
            var cotReports = await ctx.Set<COTReport>()
                .Where(c => c.Currency == baseCurrency)
                .OrderBy(c => c.ReportDate)
                .AsNoTracking()
                .ToListAsync(stoppingToken);

            // Compute training-window COT min/max bounds for consistent inference normalisation
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
            // historical performance, the operator-configured default, and a sample-count
            // safety gate. Explicit non-default values (e.g. set by a hyperparam search worker)
            // are always respected as-is.
            if (run.LearnerArchitecture == LearnerArchitecture.BaggedLogistic)
            {
                var selector = sp.GetRequiredService<ITrainerSelector>();
                run.LearnerArchitecture = await selector.SelectAsync(
                    run.Symbol, run.Timeframe, samples.Count, stoppingToken);
            }

            var trainer = run.LearnerArchitecture == LearnerArchitecture.BaggedLogistic
                ? sp.GetRequiredService<IMLModelTrainer>()
                : sp.GetRequiredKeyedService<IMLModelTrainer>(run.LearnerArchitecture);

            // ── Train with timeout ───────────────────────────────────────────
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
            var m       = result.FinalMetrics;
            var cvCheck = result.CvResult;
            bool qualityRegressionFailed =
                hp.MinQualityRetentionRatio > 0.0 &&
                parentOobAccuracy > 0.0           &&
                result.FinalMetrics.OobAccuracy < parentOobAccuracy * hp.MinQualityRetentionRatio;

            bool passed =
                m.Accuracy           >= hp.MinAccuracyToPromote                                    &&
                m.ExpectedValue      >= hp.MinExpectedValue                                        &&
                m.BrierScore         <= hp.MaxBrierScore                                           &&
                m.SharpeRatio        >= hp.MinSharpeRatio                                          &&
                cvCheck.StdAccuracy  <= hp.MaxWalkForwardStdDev                                    &&
                (hp.MaxEce <= 0 || snapEce <= hp.MaxEce)                                           &&
                (hp.MinBrierSkillScore <= -1.0 || snapBss >= hp.MinBrierSkillScore)                &&
                !qualityRegressionFailed;

            _logger.LogInformation(
                "Quality gates — acc={Acc:P1}/{MinAcc:P1} ev={EV:F4}/{MinEV:F4} " +
                "brier={Brier:F4}/{MaxBrier:F4} sharpe={Sharpe:F2}/{MinSharpe:F2} " +
                "wfStd={WfStd:P1}/{MaxWfStd:P1} ece={Ece:F4}/{MaxEce:F4} " +
                "bss={Bss:F4}/{MinBss:F4} oobReg={OobNew:P1}/{OobParent:P1} passed={Passed}",
                m.Accuracy,              hp.MinAccuracyToPromote,
                m.ExpectedValue,         hp.MinExpectedValue,
                m.BrierScore,            hp.MaxBrierScore,
                m.SharpeRatio,           hp.MinSharpeRatio,
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
                m, cvCheck, hp, snapEce, snapBss, result.FinalMetrics.OobAccuracy, parentOobAccuracy);

            if (!passed)
            {
                await db.SaveChangesAsync(stoppingToken);
                _logger.LogWarning("Run {RunId} did not pass quality gates — model not promoted", run.Id);
                await MaybeCreateTrainingFailureAlertAsync(ctx, run, stoppingToken);
                return;
            }

            // ── Demote previous active model + snapshot its live performance ──
            var previousChampion = await ctx.Set<MLModel>()
                .FirstOrDefaultAsync(
                    x => x.Symbol == run.Symbol && x.Timeframe == run.Timeframe && x.IsActive,
                    stoppingToken);

            if (previousChampion is not null)
            {
                // Capture live performance stats from prediction logs before superseding so they
                // are preserved for historical comparison without requiring a full log re-query.
                try
                {
                    var liveLogs = await ctx.Set<MLModelPredictionLog>()
                        .Where(l => l.MLModelId == previousChampion.Id &&
                                    l.DirectionCorrect != null         &&
                                    !l.IsDeleted)
                        .AsNoTracking()
                        .Select(l => new { l.DirectionCorrect })
                        .ToListAsync(stoppingToken);

                    if (liveLogs.Count > 0)
                    {
                        previousChampion.LiveTotalPredictions  = liveLogs.Count;
                        previousChampion.LiveDirectionAccuracy = (decimal)liveLogs.Count(l => l.DirectionCorrect == true) / liveLogs.Count;
                        previousChampion.LiveActiveDays        = previousChampion.ActivatedAt.HasValue
                            ? (int)(DateTime.UtcNow - previousChampion.ActivatedAt.Value).TotalDays
                            : 0;

                        _logger.LogInformation(
                            "Champion model {Id} retirement snapshot: live_acc={Acc:P1} predictions={N} active_days={Days}",
                            previousChampion.Id,
                            previousChampion.LiveDirectionAccuracy,
                            previousChampion.LiveTotalPredictions,
                            previousChampion.LiveActiveDays);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to snapshot live performance for model {Id} — non-critical", previousChampion.Id);
                }

                previousChampion.IsActive = false;
                previousChampion.Status   = MLModelStatus.Superseded;
            }

            // ── Patch snapshot: inject COT bounds + feature variances ────────
            decimal plattA = 1m, plattB = 0m;
            byte[]  finalModelBytes = result.ModelBytes;
            if (result.ModelBytes is { Length: > 0 })
            {
                try
                {
                    var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
                    if (snap is not null)
                    {
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

                        // ── Per-regime feature standardisation ────────────────
                        // Load regime snapshots and partition training samples by regime.
                        // Compute regime-specific means/stds for use at inference time.
                        try
                        {
                            var regimeSnapsForStd = await ctx.Set<MarketRegimeSnapshot>()
                                .Where(r => r.Symbol    == run.Symbol &&
                                            r.DetectedAt >= run.FromDate &&
                                            r.DetectedAt <= run.ToDate)
                                .OrderBy(r => r.DetectedAt)
                                .AsNoTracking()
                                .ToListAsync(stoppingToken);

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

                        // ── Enrich dataset stats with feature means/stds ─────
                        // Now that the snapshot is available, update the pre-training JSON
                        // with the normalisation parameters so the full picture is preserved.
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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Run {RunId}: failed to patch snapshot metadata — raw bytes kept", run.Id);
                }
            }

            // ── Persist new model ────────────────────────────────────────────
            var cv           = result.CvResult;
            var modelVersion = $"{run.Symbol}_{run.Timeframe}_{run.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";

            var model = new MLModel
            {
                Symbol                 = run.Symbol,
                Timeframe              = run.Timeframe,
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

            // ── Audit log ────────────────────────────────────────────────────
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
                }, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit log failed for run {RunId} — non-critical", run.Id);
            }

            // ── Publish integration event ────────────────────────────────────
            if (eventBus is not null)
            {
                try
                {
                    eventBus.Publish(new MLModelActivatedIntegrationEvent
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
                    _logger.LogWarning(ex, "Event bus publish failed — non-critical");
                }
            }

            _logger.LogInformation(
                "Run {RunId} complete. ModelId={ModelId} Version={Version} DurationMs={Ms} Samples={N}",
                run.Id, model.Id, model.ModelVersion, sw.ElapsedMilliseconds, samples.Count);

            // ── Regime-specific sub-models ───────────────────────────────────
            if (hp.EnableRegimeSpecificModels)
            {
                await TrainRegimeSubModelsAsync(
                    run, samples, hp, candles, ctx, db, linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellations (timeout or shutdown) are not ML failures — do not count against AttemptCount.
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

            // Fetch the last `threshold` completed runs for this symbol/timeframe (including current)
            var recentStatuses = await ctx.Set<MLTrainingRun>()
                .Where(r => r.Symbol    == run.Symbol    &&
                            r.Timeframe == run.Timeframe &&
                            r.Status    == RunStatus.Failed)
                .OrderByDescending(r => r.CompletedAt)
                .Take(threshold)
                .AsNoTracking()
                .Select(r => r.Status)
                .ToListAsync(ct);

            if (recentStatuses.Count < threshold)
                return; // not enough consecutive failures yet

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
        Microsoft.EntityFrameworkCore.DbContext ctx,
        IWriteApplicationDbContext              db,
        CancellationToken                       ct)
    {
        var trainer = ctx.GetType().Assembly    // get trainer from DI — not available directly here;
                                                // we rely on scope being still alive
            .GetType();                         // placeholder — resolved below via workaround

        // Resolve trainer from the DB context's service provider is not directly available.
        // We only have ctx and db here; trainer must be re-resolved from the original scope.
        // Since this runs synchronously in ProcessRunAsync's scope, we use the field reference
        // that was captured — but it's a local in ProcessRunAsync.
        // Solution: pass the IMLModelTrainer in through a parameter.
        // Rather than refactoring the signature, skip if samples too small.
        //
        // NOTE: regime sub-training is triggered via a dedicated MLTrainingRun with
        // TriggerType = Scheduled and RegimeScope set in HyperparamConfigJson. This method
        // instead performs a lightweight in-process train using the already-loaded candles.

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

            // Map each candle index to its regime via binary search
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

                var regimeTrainer = new Services.BaggedLogisticTrainer(
                    _logger as Microsoft.Extensions.Logging.ILogger<Services.BaggedLogisticTrainer>
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.BaggedLogisticTrainer>.Instance);

                TrainingResult regimeResult;
                using var regimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                regimeCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(5, hp.TrainingTimeoutMinutes / 3)));

                try
                {
                    regimeResult = await regimeTrainer.TrainAsync(regimeSamples, regimeHp, ct: regimeCts.Token);
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
                    Symbol            = run.Symbol,
                    Timeframe         = run.Timeframe,
                    RegimeScope       = regimeName,
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

    private static async Task<TrainingHyperparams> LoadHyperparamsAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        CancellationToken                       ct)
    {
        return new TrainingHyperparams(
            K:                        await GetConfigAsync<int>   (ctx, CK_K,                   5,     ct),
            LearningRate:             await GetConfigAsync<double>(ctx, CK_LR,                  0.01,  ct),
            L2Lambda:                 await GetConfigAsync<double>(ctx, CK_L2,                  0.001, ct),
            MaxEpochs:                await GetConfigAsync<int>   (ctx, CK_MaxEpochs,           200,   ct),
            EarlyStoppingPatience:    await GetConfigAsync<int>   (ctx, CK_ESPatience,          15,    ct),
            MinAccuracyToPromote:     await GetConfigAsync<double>(ctx, CK_MinAccuracy,         0.55,  ct),
            MinExpectedValue:         await GetConfigAsync<double>(ctx, CK_MinEV,               0.0,   ct),
            MaxBrierScore:            await GetConfigAsync<double>(ctx, CK_MaxBrier,            0.25,  ct),
            MinSharpeRatio:           await GetConfigAsync<double>(ctx, CK_MinSharpe,           0.5,   ct),
            MinSamples:               await GetConfigAsync<int>   (ctx, CK_MinSamples,          500,   ct),
            ShadowRequiredTrades:     await GetConfigAsync<int>   (ctx, CK_ShadowTrades,        50,    ct),
            ShadowExpiryDays:         await GetConfigAsync<int>   (ctx, CK_ShadowExpiry,        30,    ct),
            WalkForwardFolds:         await GetConfigAsync<int>   (ctx, CK_WFolds,              4,     ct),
            EmbargoBarCount:          await GetConfigAsync<int>   (ctx, CK_Embargo,             30,    ct),
            TrainingTimeoutMinutes:   await GetConfigAsync<int>   (ctx, CK_Timeout,             30,    ct),
            TemporalDecayLambda:      await GetConfigAsync<double>(ctx, CK_DecayLambda,         2.0,   ct),
            DriftWindowDays:          await GetConfigAsync<int>   (ctx, CK_DriftWindowDays,     14,    ct),
            DriftMinPredictions:      await GetConfigAsync<int>   (ctx, CK_DriftMinPredictions, 30,    ct),
            DriftAccuracyThreshold:   await GetConfigAsync<double>(ctx, CK_DriftAccThreshold,   0.50,  ct),
            MaxWalkForwardStdDev:     await GetConfigAsync<double>(ctx, CK_MaxWfStdDev,         0.15,  ct),
            LabelSmoothing:              await GetConfigAsync<double>(ctx, CK_LabelSmoothing,              0.05,  ct),
            MinFeatureImportance:        await GetConfigAsync<double>(ctx, CK_MinFeatureImportance,        0.0,   ct),
            EnableRegimeSpecificModels:  await GetConfigAsync<bool>  (ctx, CK_EnableRegimeModels,          false, ct),
            FeatureSampleRatio:          await GetConfigAsync<double>(ctx, CK_FeatureSampleRatio,          0.0,   ct),
            MaxEce:                      await GetConfigAsync<double>(ctx, CK_MaxEce,                      0.0,   ct),
            UseTripleBarrier:            await GetConfigAsync<bool>  (ctx, CK_UseTripleBarrier,            false, ct),
            TripleBarrierProfitAtrMult:  await GetConfigAsync<double>(ctx, CK_TripleBarrierProfitAtrMult,  1.5,   ct),
            TripleBarrierStopAtrMult:    await GetConfigAsync<double>(ctx, CK_TripleBarrierStopAtrMult,    1.0,   ct),
            TripleBarrierHorizonBars:    await GetConfigAsync<int>   (ctx, CK_TripleBarrierHorizonBars,    24,    ct),
            NoiseSigma:                  await GetConfigAsync<double>(ctx, CK_NoiseSigma,                  0.0,   ct),
            FpCostWeight:                await GetConfigAsync<double>(ctx, CK_FpCostWeight,                0.5,   ct),
            NclLambda:                   await GetConfigAsync<double>(ctx, CK_NclLambda,                   0.0,   ct),
            FracDiffD:                   await GetConfigAsync<double>(ctx, "MLTraining:FracDiffD",           0.0,   ct),
            MaxFoldDrawdown:             await GetConfigAsync<double>(ctx, "MLTraining:MaxFoldDrawdown",     1.0,   ct),
            MinFoldCurveSharpe:          await GetConfigAsync<double>(ctx, "MLTraining:MinFoldCurveSharpe", -99.0, ct),
            PolyLearnerFraction:         await GetConfigAsync<double>(ctx, "MLTraining:PolyLearnerFraction",         0.0,  ct),
            PurgeHorizonBars:            await GetConfigAsync<int>   (ctx, "MLTraining:PurgeHorizonBars",            0,    ct),
            NoiseCorrectionThreshold:    await GetConfigAsync<double>(ctx, "MLTraining:NoiseCorrectionThreshold",    0.0,  ct),
            MaxLearnerCorrelation:       await GetConfigAsync<double>(ctx, "MLTraining:MaxLearnerCorrelation",       1.0,  ct),
            SwaStartEpoch:               await GetConfigAsync<int>   (ctx, CK_SwaStartEpoch,              0,     ct),
            SwaFrequency:                await GetConfigAsync<int>   (ctx, CK_SwaFrequency,               1,     ct),
            MixupAlpha:                  await GetConfigAsync<double>(ctx, CK_MixupAlpha,                 0.0,   ct),
            EnableGreedyEnsembleSelection: await GetConfigAsync<bool>(ctx, CK_EnableGes,                  false, ct),
            MaxGradNorm:                 await GetConfigAsync<double>(ctx, CK_MaxGradNorm,                0.0,   ct),
            AtrLabelSensitivity:         await GetConfigAsync<double>(ctx, CK_AtrLabelSensitivity,        0.0,   ct),
            ShadowMinZScore:             await GetConfigAsync<double>(ctx, CK_ShadowMinZScore,            1.645, ct),
            L1Lambda:                    await GetConfigAsync<double>(ctx, CK_L1Lambda,                   0.0,   ct),
            MagnitudeQuantileTau:        await GetConfigAsync<double>(ctx, CK_MagnitudeQuantileTau,       0.0,   ct),
            MagLossWeight:               await GetConfigAsync<double>(ctx, CK_MagLossWeight,              0.0,   ct),
            DensityRatioWindowDays:      await GetConfigAsync<int>   (ctx, CK_DensityRatioWindowDays,     0,     ct),
            BarsPerDay:                  await GetConfigAsync<int>   (ctx, "MLTraining:BarsPerDay",          24,    ct),
            DurbinWatsonThreshold:       await GetConfigAsync<double>(ctx, CK_DurbinWatsonThreshold,      0.0,   ct),
            AdaptiveLrDecayFactor:       await GetConfigAsync<double>(ctx, CK_AdaptiveLrDecayFactor,      0.0,   ct),
            OobPruningEnabled:           await GetConfigAsync<bool>  (ctx, CK_OobPruningEnabled,          false, ct),
            MutualInfoRedundancyThreshold: await GetConfigAsync<double>(ctx, CK_MutualInfoRedundancyThr,  0.0,   ct),
            MinSharpeTrendSlope:         await GetConfigAsync<double>(ctx, CK_MinSharpeTrendSlope,        -99.0, ct),
            FitTemperatureScale:         await GetConfigAsync<bool>  (ctx, CK_FitTemperatureScale,        false, ct),
            MinBrierSkillScore:          await GetConfigAsync<double>(ctx, CK_MinBrierSkillScore,         -1.0,  ct),
            RecalibrationDecayLambda:    await GetConfigAsync<double>(ctx, CK_RecalibrationDecayLambda,   0.0,   ct),
            MaxEnsembleDiversity:        await GetConfigAsync<double>(ctx, CK_MaxEnsembleDiversity,       1.0,   ct),
            UseSymmetricCE:              await GetConfigAsync<bool>  (ctx, CK_UseSymmetricCE,             false, ct),
            SymmetricCeAlpha:            await GetConfigAsync<double>(ctx, CK_SymmetricCeAlpha,           0.0,   ct),
            DiversityLambda:             await GetConfigAsync<double>(ctx, CK_DiversityLambda,            0.0,   ct),
            UseAdaptiveLabelSmoothing:   await GetConfigAsync<bool>  (ctx, CK_UseAdaptiveLabelSmoothing,  false, ct),
            AgeDecayLambda:              await GetConfigAsync<double>(ctx, CK_AgeDecayLambda,             0.0,   ct),
            UseCovariateShiftWeights:    await GetConfigAsync<bool>  (ctx, CK_UseCovariateShiftWeights,   false, ct),
            MaxBadFoldFraction:          await GetConfigAsync<double>(ctx, CK_MaxBadFoldFraction,         0.5,   ct),
            MinQualityRetentionRatio:    await GetConfigAsync<double>(ctx, CK_MinQualityRetentionRatio,   0.0,   ct),
            MultiTaskMagnitudeWeight:    await GetConfigAsync<double>(ctx, "MLTraining:MultiTaskMagnitudeWeight", 0.3,  ct),
            CurriculumEasyFraction:      await GetConfigAsync<double>(ctx, "MLTraining:CurriculumEasyFraction",   0.3,  ct),
            SelfDistillTemp:             await GetConfigAsync<double>(ctx, "MLTraining:SelfDistillTemp",          3.0,  ct),
            FgsmEpsilon:                 await GetConfigAsync<double>(ctx, "MLTraining:FgsmEpsilon",              0.01, ct));
    }

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

    private static string BuildGateFailureMessage(
        EvalMetrics         m,
        WalkForwardResult   cv,
        TrainingHyperparams hp,
        double              snapEce            = 0.0,
        double              snapBss            = double.NegativeInfinity,
        double              newOobAccuracy     = 0.0,
        double              parentOobAccuracy  = 0.0)
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
