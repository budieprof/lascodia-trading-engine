using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects covariate feature-distribution shift in active ML models by comparing recent
/// feature vectors against each model snapshot's training-time standardisation statistics.
/// </summary>
public sealed class MLCovariateShiftWorker : BackgroundService
{
    private const string WorkerName = nameof(MLCovariateShiftWorker);
    private const string DistributedLockKey = "ml:covariate-shift:cycle";
    private const string DriftTriggerType = "CovariateShift";

    private const string CK_PollSecs = "MLCovariate:PollIntervalSeconds";
    private const string CK_WindowDays = "MLCovariate:WindowDays";
    private const string CK_PsiThreshold = "MLCovariate:PsiThreshold";
    private const string CK_MinCandles = "MLCovariate:MinCandles";
    private const string CK_TrainingDays = "MLTraining:TrainingDataWindowDays";
    private const string CK_MultivariateThreshold = "MLCovariate:MultivariateThreshold";
    private const string CK_PerFeaturePsiThreshold = "MLCovariate:PerFeaturePsiThreshold";

    private const int NumInnerBins = 10;
    private const double BinWidth = 6.0 / NumInnerBins;
    private const double ZMin = -3.0;
    private const double ZScoreCap = 10.0;
    private const double StdEpsilon = 1e-8;

    private static readonly double[] ExpectedBinProb = ComputeExpectedBinProbs();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLCovariateShiftWorker> _logger;
    private readonly MLCovariateShiftOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public MLCovariateShiftWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLCovariateShiftWorker> logger,
        MLCovariateShiftOptions? options = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLCovariateShiftOptions();
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = BuildSettings(_options);
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Detects ML covariate shift using PSI and multivariate z-score diagnostics.",
            initialSettings.PollInterval);

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName) + initialSettings.InitialDelay;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var delaySettings = BuildSettings(_options);

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                    var result = await RunCycleAsync(stoppingToken);
                    delaySettings = result.Settings;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.TrainingBacklogDepth);
                    _healthMonitor?.RecordCycleSuccess(
                        WorkerName,
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _consecutiveFailures = 0;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName));
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                await Task.Delay(
                    CalculateDelay(GetIntervalWithJitter(delaySettings), _consecutiveFailures),
                    _timeProvider,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopping.", WorkerName);
        }
    }

    internal async Task<MLCovariateShiftCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = BuildSettings(_options);

        try
        {
            if (!settings.Enabled)
            {
                RecordCycleSkipped("disabled");
                return MLCovariateShiftCycleResult.Skipped(settings, "disabled");
            }

            IAsyncDisposable? cycleLock = null;
            if (_distributedLock is null)
            {
                _metrics?.MLCovariateShiftLockAttempts.Add(1, Tag("outcome", "unavailable"));
                if (!_missingDistributedLockWarningEmitted)
                {
                    _logger.LogWarning(
                        "{Worker} running without IDistributedLock; duplicate retraining decisions are possible in multi-instance deployments.",
                        WorkerName);
                    _missingDistributedLockWarningEmitted = true;
                }
            }
            else
            {
                cycleLock = await _distributedLock.TryAcquireAsync(
                    DistributedLockKey,
                    settings.LockTimeout,
                    ct);

                if (cycleLock is null)
                {
                    _metrics?.MLCovariateShiftLockAttempts.Add(1, Tag("outcome", "busy"));
                    RecordCycleSkipped("lock_busy");
                    return MLCovariateShiftCycleResult.Skipped(settings, "lock_busy");
                }

                _metrics?.MLCovariateShiftLockAttempts.Add(1, Tag("outcome", "acquired"));
            }

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = writeContext.GetDbContext();

                    var runtimeSettings = await LoadRuntimeSettingsAsync(db, settings, ct);
                    if (!runtimeSettings.Enabled)
                    {
                        RecordCycleSkipped("disabled");
                        return MLCovariateShiftCycleResult.Skipped(runtimeSettings, "disabled");
                    }

                    return await CheckAllModelsAsync(db, runtimeSettings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }
        finally
        {
            _metrics?.MLCovariateShiftCycleDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<MLCovariateShiftCycleResult> CheckAllModelsAsync(
        DbContext db,
        MLCovariateShiftSettings settings,
        CancellationToken ct)
    {
        var activeModels = await db.Set<MLModel>()
            .Where(model => model.IsActive && !model.IsDeleted && model.ModelBytes != null)
            .OrderBy(model => model.Symbol)
            .ThenBy(model => model.Timeframe)
            .ThenBy(model => model.Id)
            .Take(settings.MaxModelsPerCycle + 1)
            .AsNoTracking()
            .ToListAsync(ct);

        var truncated = activeModels.Count > settings.MaxModelsPerCycle;
        if (truncated)
            activeModels.RemoveAt(activeModels.Count - 1);

        var backlogDepth = await CountTrainingBacklogAsync(db, ct);
        if (activeModels.Count == 0)
        {
            RecordCycleSkipped("no_active_models");
            return new MLCovariateShiftCycleResult(
                settings,
                "no_active_models",
                0,
                0,
                0,
                0,
                0,
                0,
                backlogDepth,
                truncated);
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var configSpecs = new List<EngineConfigUpsertSpec>(activeModels.Count);
        var modelsEvaluated = 0;
        var modelsSkipped = 0;
        var shiftsDetected = 0;
        var retrainingQueued = 0;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            ModelCovariateShiftEvaluation evaluation;
            try
            {
                evaluation = await CheckModelCovariateShiftAsync(db, model, settings, nowUtc, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to evaluate model {ModelId} ({Symbol}/{Timeframe}); skipping this model.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
                evaluation = ModelCovariateShiftEvaluation.Skipped("evaluation_error");
            }

            if (!evaluation.Evaluated)
            {
                modelsSkipped++;
                RecordModelSkipped(evaluation.SkipReason ?? "unknown");
                continue;
            }

            modelsEvaluated++;
            _metrics?.MLCovariateShiftModelsEvaluated.Add(1, Tag("symbol", model.Symbol), Tag("timeframe", model.Timeframe));
            _metrics?.MLCovariateShiftWeightedPsi.Record(evaluation.WeightedPsi, Tag("symbol", model.Symbol), Tag("timeframe", model.Timeframe));
            _metrics?.MLCovariateShiftMaxPsi.Record(evaluation.MaxPsi, Tag("symbol", model.Symbol), Tag("timeframe", model.Timeframe));
            _metrics?.MLCovariateShiftMultivariateScore.Record(evaluation.MultivariateScore, Tag("symbol", model.Symbol), Tag("timeframe", model.Timeframe));

            configSpecs.Add(CreateDriftedFeaturesConfigSpec(model, evaluation.DriftedFeatures));

            if (!evaluation.ShiftDetected)
                continue;

            shiftsDetected++;
            _metrics?.MLCovariateShiftDetections.Add(1, Tag("symbol", model.Symbol), Tag("timeframe", model.Timeframe));

            var queueResult = await TryQueueRetrainingAsync(db, model, evaluation, settings, nowUtc, backlogDepth, ct);
            if (queueResult == RetrainingQueueResult.Queued)
            {
                retrainingQueued++;
                backlogDepth++;
                _metrics?.MLCovariateShiftRetrainingQueued.Add(1, Tag("symbol", model.Symbol), Tag("timeframe", model.Timeframe));
            }
            else
            {
                RecordRetrainingSkipped(queueResult.Reason);
            }
        }

        if (configSpecs.Count > 0)
            await EngineConfigUpsert.BatchUpsertAsync(db, configSpecs, ct);

        if (retrainingQueued > 0)
            await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "{Worker}: evaluated {Evaluated}/{Seen} active model(s), detected {Detected} shift(s), queued {Queued} retraining run(s).",
            WorkerName,
            modelsEvaluated,
            activeModels.Count,
            shiftsDetected,
            retrainingQueued);

        return new MLCovariateShiftCycleResult(
            settings,
            null,
            activeModels.Count,
            modelsEvaluated,
            modelsSkipped,
            shiftsDetected,
            retrainingQueued,
            configSpecs.Count,
            backlogDepth,
            truncated);
    }

    private async Task<ModelCovariateShiftEvaluation> CheckModelCovariateShiftAsync(
        DbContext db,
        MLModel model,
        MLCovariateShiftSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (!TryDeserializeSnapshot(model.ModelBytes, out var snapshot))
            return ModelCovariateShiftEvaluation.Skipped("invalid_snapshot");

        var snapshotFeatureCount = Math.Min(snapshot.Means.Length, snapshot.Stds.Length);
        if (snapshotFeatureCount <= 0)
            return ModelCovariateShiftEvaluation.Skipped("missing_standardization_stats");

        var windowStart = nowUtc.AddDays(-settings.WindowDays);
        var candles = await db.Set<Candle>()
            .Where(candle => candle.Symbol == model.Symbol
                          && candle.Timeframe == model.Timeframe
                          && candle.Timestamp >= windowStart
                          && candle.IsClosed
                          && !candle.IsDeleted)
            .OrderBy(candle => candle.Timestamp)
            .AsNoTracking()
            .ToListAsync(ct);

        var requiredCandles = MLFeatureHelper.LookbackWindow + settings.MinCandles + 1;
        if (candles.Count < requiredCandles)
        {
            _logger.LogDebug(
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) has {CandleCount} candles, need {Required}; skipping.",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe,
                candles.Count,
                requiredCandles);
            return ModelCovariateShiftEvaluation.Skipped("insufficient_candles");
        }

        var recentSamples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (recentSamples.Count < settings.MinCandles)
            return ModelCovariateShiftEvaluation.Skipped("insufficient_feature_samples");

        if (!TryBuildStandardizedMatrix(
                snapshot,
                recentSamples,
                snapshotFeatureCount,
                out var recentStd,
                out var activeFeatureIndices,
                out var skipReason))
        {
            return ModelCovariateShiftEvaluation.Skipped(skipReason);
        }

        var importanceWeights = ResolveFeatureImportanceWeights(snapshot, activeFeatureIndices);
        var perFeaturePsiValues = new Dictionary<int, double>(activeFeatureIndices.Count);
        var maxPsi = 0.0;
        var maxFeature = activeFeatureIndices[0];
        var weightedPsi = 0.0;

        for (var i = 0; i < activeFeatureIndices.Count; i++)
        {
            var featureIndex = activeFeatureIndices[i];
            var psi = ComputePsi(recentStd, featureIndex);
            perFeaturePsiValues[featureIndex] = psi;
            weightedPsi += psi * importanceWeights[i];

            if (psi > maxPsi)
            {
                maxPsi = psi;
                maxFeature = featureIndex;
            }
        }

        var driftedFeatures = perFeaturePsiValues
            .Where(pair => pair.Value >= settings.PerFeaturePsiThreshold)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new DriftedFeature(ResolveFeatureName(snapshot, pair.Key), pair.Value))
            .ToArray();

        var multivariateScore = ComputeMultivariateScore(recentStd, activeFeatureIndices);
        var univariateShift = weightedPsi >= settings.PsiThreshold;
        var multivariateShift = multivariateScore >= settings.MultivariateThreshold;
        var shiftDetected = univariateShift || multivariateShift;
        var maxFeatureName = ResolveFeatureName(snapshot, maxFeature);

        if (driftedFeatures.Length > 0)
        {
            _logger.LogWarning(
                "{Worker}: covariate drift for {Symbol}/{Timeframe}: {Count}/{Total} active features above PSI threshold (top: {Top}).",
                WorkerName,
                model.Symbol,
                model.Timeframe,
                driftedFeatures.Length,
                activeFeatureIndices.Count,
                string.Join(", ", driftedFeatures.Take(3).Select(feature => $"{feature.Name}(PSI={feature.Psi:F2})")));
        }

        _logger.LogDebug(
            "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) weightedPSI={WeightedPsi:F4}, maxPSI={MaxPsi:F4} ({Feature}), mvScore={MultivariateScore:F4}.",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            weightedPsi,
            maxPsi,
            maxFeatureName,
            multivariateScore);

        return new ModelCovariateShiftEvaluation(
            true,
            null,
            shiftDetected,
            weightedPsi,
            maxPsi,
            multivariateScore,
            maxFeatureName,
            driftedFeatures,
            univariateShift,
            multivariateShift);
    }

    private async Task<RetrainingQueueResult> TryQueueRetrainingAsync(
        DbContext db,
        MLModel model,
        ModelCovariateShiftEvaluation evaluation,
        MLCovariateShiftSettings settings,
        DateTime nowUtc,
        int trainingBacklogDepth,
        CancellationToken ct)
    {
        if (await HasQueuedOrRunningRetrainAsync(db, model.Symbol, model.Timeframe, ct))
            return RetrainingQueueResult.AlreadyQueued;

        if (trainingBacklogDepth >= settings.MaxQueuedRetrains)
            return RetrainingQueueResult.QueueFull;

        if (settings.RetrainCooldown > TimeSpan.Zero)
        {
            var cooldownStart = nowUtc - settings.RetrainCooldown;
            var recentlyQueued = await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(run => run.Symbol == model.Symbol
                              && run.Timeframe == model.Timeframe
                              && run.DriftTriggerType == DriftTriggerType
                              && run.StartedAt >= cooldownStart
                              && run.Status != RunStatus.Failed,
                    ct);

            if (recentlyQueued)
                return RetrainingQueueResult.Cooldown;
        }

        var metadata = JsonSerializer.Serialize(new
        {
            evaluation.WeightedPsi,
            evaluation.MaxPsi,
            psiFeature = evaluation.MaxFeatureName,
            multivariateScore = evaluation.MultivariateScore,
            evaluation.UnivariateShift,
            evaluation.MultivariateShift,
            driftedFeatures = evaluation.DriftedFeatures.Select(feature => new
            {
                featureName = feature.Name,
                psi = feature.Psi
            }),
            settings.PsiThreshold,
            settings.PerFeaturePsiThreshold,
            settings.MultivariateThreshold,
            settings.WindowDays,
            settings.MinCandles,
        }, JsonOptions);

        var run = new MLTrainingRun
        {
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status = RunStatus.Queued,
            FromDate = nowUtc.AddDays(-settings.TrainingDays),
            ToDate = nowUtc,
            StartedAt = nowUtc,
            DriftTriggerType = DriftTriggerType,
            DriftMetadataJson = metadata,
            Priority = 1,
        };

        db.Set<MLTrainingRun>().Add(run);

        _logger.LogWarning(
            "{Worker}: covariate shift detected for model {ModelId} ({Symbol}/{Timeframe}); queued retraining ({Reason}).",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            DescribeShiftKind(evaluation));

        return RetrainingQueueResult.Queued;
    }

    private static async Task<int> CountTrainingBacklogAsync(DbContext db, CancellationToken ct)
    {
        var persisted = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .CountAsync(run => run.Status == RunStatus.Queued || run.Status == RunStatus.Running, ct);

        var local = db.Set<MLTrainingRun>().Local
            .Count(run => run.Status == RunStatus.Queued || run.Status == RunStatus.Running);

        return persisted + local;
    }

    private static async Task<bool> HasQueuedOrRunningRetrainAsync(
        DbContext db,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var localMatch = db.Set<MLTrainingRun>().Local.Any(run =>
            run.Symbol == symbol
            && run.Timeframe == timeframe
            && (run.Status == RunStatus.Queued || run.Status == RunStatus.Running));

        if (localMatch)
            return true;

        return await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run => run.Symbol == symbol
                          && run.Timeframe == timeframe
                          && (run.Status == RunStatus.Queued || run.Status == RunStatus.Running),
                ct);
    }

    private async Task<MLCovariateShiftSettings> LoadRuntimeSettingsAsync(
        DbContext db,
        MLCovariateShiftSettings defaults,
        CancellationToken ct)
    {
        var keys = new[]
        {
            CK_PollSecs,
            CK_WindowDays,
            CK_PsiThreshold,
            CK_MinCandles,
            CK_TrainingDays,
            CK_MultivariateThreshold,
            CK_PerFeaturePsiThreshold,
        };

        var config = await db.Set<EngineConfig>()
            .Where(entry => keys.Contains(entry.Key) && !entry.IsDeleted)
            .AsNoTracking()
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Value, ct);

        return defaults with
        {
            PollInterval = TimeSpan.FromSeconds(GetInt(config, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds, 60, 86_400)),
            WindowDays = GetInt(config, CK_WindowDays, defaults.WindowDays, 1, 3_650),
            PsiThreshold = GetDouble(config, CK_PsiThreshold, defaults.PsiThreshold, 0.01, 5.0),
            MinCandles = GetInt(config, CK_MinCandles, defaults.MinCandles, 20, 100_000),
            TrainingDays = GetInt(config, CK_TrainingDays, defaults.TrainingDays, 1, 3_650),
            MultivariateThreshold = GetDouble(config, CK_MultivariateThreshold, defaults.MultivariateThreshold, 1.01, 100.0),
            PerFeaturePsiThreshold = GetDouble(config, CK_PerFeaturePsiThreshold, defaults.PerFeaturePsiThreshold, 0.01, 5.0),
        };
    }

    private static MLCovariateShiftSettings BuildSettings(MLCovariateShiftOptions options)
        => new()
        {
            Enabled = options.Enabled,
            InitialDelay = TimeSpan.FromSeconds(Clamp(options.InitialDelaySeconds, 0, 86_400)),
            PollInterval = TimeSpan.FromSeconds(Clamp(options.PollIntervalSeconds, 60, 86_400)),
            PollJitter = TimeSpan.FromSeconds(Clamp(options.PollJitterSeconds, 0, 86_400)),
            WindowDays = Clamp(options.WindowDays, 1, 3_650),
            PsiThreshold = ClampFinite(options.PsiThreshold, 0.01, 5.0),
            MinCandles = Clamp(options.MinCandles, 20, 100_000),
            TrainingDays = Clamp(options.TrainingDays, 1, 3_650),
            MultivariateThreshold = ClampFinite(options.MultivariateThreshold, 1.01, 100.0),
            PerFeaturePsiThreshold = ClampFinite(options.PerFeaturePsiThreshold, 0.01, 5.0),
            MaxModelsPerCycle = Clamp(options.MaxModelsPerCycle, 1, 10_000),
            MaxQueuedRetrains = Clamp(options.MaxQueuedRetrains, 1, 100_000),
            RetrainCooldown = TimeSpan.FromSeconds(Clamp(options.RetrainCooldownSeconds, 0, 2_592_000)),
            LockTimeout = TimeSpan.FromSeconds(Clamp(options.LockTimeoutSeconds, 0, 300)),
        };

    private static bool TryDeserializeSnapshot(byte[]? modelBytes, out ModelSnapshot snapshot)
    {
        snapshot = new ModelSnapshot();
        if (modelBytes is null || modelBytes.Length == 0)
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<ModelSnapshot>(modelBytes, JsonOptions);
            if (parsed is null)
                return false;

            snapshot = parsed;
            snapshot.Means ??= [];
            snapshot.Stds ??= [];
            snapshot.FeatureImportance ??= [];
            snapshot.Features ??= [];
            snapshot.RawFeatureIndices ??= [];
            snapshot.ActiveFeatureMask ??= [];
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryBuildStandardizedMatrix(
        ModelSnapshot snapshot,
        IReadOnlyList<TrainingSample> samples,
        int snapshotFeatureCount,
        out List<float[]> recentStd,
        out IReadOnlyList<int> activeFeatureIndices,
        out string skipReason)
    {
        recentStd = [];
        activeFeatureIndices = [];
        skipReason = string.Empty;

        var projectedSamples = new List<float[]>(samples.Count);
        foreach (var sample in samples)
        {
            if (!TryProjectFeatures(sample.Features, snapshot.RawFeatureIndices, out var projected))
            {
                skipReason = "invalid_raw_feature_indices";
                return false;
            }

            projectedSamples.Add(projected);
        }

        var observedFeatureCount = projectedSamples
            .Where(features => features.Length > 0)
            .Select(features => features.Length)
            .DefaultIfEmpty(0)
            .Min();

        var resolvedFeatureCount = snapshot.ResolveExpectedInputFeatures();
        var featureCount = Math.Min(snapshotFeatureCount, observedFeatureCount);
        if (resolvedFeatureCount > 0)
            featureCount = Math.Min(featureCount, resolvedFeatureCount);

        if (featureCount <= 0)
        {
            skipReason = "missing_recent_features";
            return false;
        }

        var active = ResolveActiveFeatureIndices(snapshot.ActiveFeatureMask, featureCount);
        if (active.Count == 0)
        {
            skipReason = "empty_active_feature_mask";
            return false;
        }

        foreach (var features in projectedSamples)
        {
            var standardized = new float[featureCount];
            for (var j = 0; j < featureCount; j++)
            {
                var value = j < features.Length ? features[j] : 0f;
                standardized[j] = Standardize(value, snapshot.Means[j], snapshot.Stds[j]);
            }

            recentStd.Add(standardized);
        }

        activeFeatureIndices = active;
        return true;
    }

    private static bool TryProjectFeatures(float[] features, int[] rawFeatureIndices, out float[] projected)
    {
        projected = features;
        if (rawFeatureIndices.Length == 0)
            return true;

        if (rawFeatureIndices.Distinct().Count() != rawFeatureIndices.Length)
            return false;

        projected = new float[rawFeatureIndices.Length];
        for (var i = 0; i < rawFeatureIndices.Length; i++)
        {
            var rawIndex = rawFeatureIndices[i];
            if (rawIndex < 0 || rawIndex >= features.Length)
                return false;

            projected[i] = features[rawIndex];
        }

        return true;
    }

    private static List<int> ResolveActiveFeatureIndices(bool[] activeFeatureMask, int featureCount)
    {
        if (activeFeatureMask.Length == 0)
            return Enumerable.Range(0, featureCount).ToList();

        var active = new List<int>(featureCount);
        var applyLength = Math.Min(activeFeatureMask.Length, featureCount);
        for (var i = 0; i < applyLength; i++)
        {
            if (activeFeatureMask[i])
                active.Add(i);
        }

        for (var i = applyLength; i < featureCount; i++)
            active.Add(i);

        return active;
    }

    private static double[] ResolveFeatureImportanceWeights(
        ModelSnapshot snapshot,
        IReadOnlyList<int> activeFeatureIndices)
    {
        var weights = new double[activeFeatureIndices.Count];
        var sum = 0.0;
        for (var i = 0; i < activeFeatureIndices.Count; i++)
        {
            var featureIndex = activeFeatureIndices[i];
            var importance = featureIndex < snapshot.FeatureImportance.Length
                ? snapshot.FeatureImportance[featureIndex]
                : 0f;

            if (float.IsFinite(importance) && importance > 0f)
            {
                weights[i] = importance;
                sum += importance;
            }
        }

        if (sum <= 0.0)
        {
            var equalWeight = 1.0 / activeFeatureIndices.Count;
            Array.Fill(weights, equalWeight);
            return weights;
        }

        for (var i = 0; i < weights.Length; i++)
            weights[i] /= sum;

        return weights;
    }

    private static float Standardize(float value, float mean, float std)
    {
        if (!float.IsFinite(value))
            value = 0f;
        if (!float.IsFinite(mean))
            mean = 0f;
        if (!float.IsFinite(std) || Math.Abs(std) <= StdEpsilon)
            std = 1f;

        var z = (value - mean) / std;
        if (!float.IsFinite(z))
            return 0f;

        return (float)Math.Clamp(z, -ZScoreCap, ZScoreCap);
    }

    private static double ComputePsi(List<float[]> recentStd, int featureIdx)
    {
        var numBins = NumInnerBins + 2;
        var counts = new int[numBins];
        var finiteCount = 0;

        foreach (var z in recentStd)
        {
            var value = featureIdx < z.Length ? z[featureIdx] : 0f;
            if (!float.IsFinite(value))
                continue;

            finiteCount++;
            if (value < ZMin)
            {
                counts[0]++;
            }
            else if (value >= -ZMin)
            {
                counts[numBins - 1]++;
            }
            else
            {
                var bin = (int)((value - ZMin) / BinWidth);
                bin = Math.Clamp(bin, 0, NumInnerBins - 1);
                counts[bin + 1]++;
            }
        }

        if (finiteCount == 0)
            return 0.0;

        var psi = 0.0;
        for (var bin = 0; bin < numBins; bin++)
        {
            var actual = (counts[bin] + 0.5) / (finiteCount + 0.5 * numBins);
            var expected = Math.Max(ExpectedBinProb[bin], 1e-12);
            psi += (actual - expected) * Math.Log(actual / expected);
        }

        return double.IsFinite(psi) ? Math.Max(0.0, psi) : 0.0;
    }

    private static double ComputeMultivariateScore(
        IReadOnlyList<float[]> recentStd,
        IReadOnlyList<int> activeFeatureIndices)
    {
        if (recentStd.Count == 0 || activeFeatureIndices.Count == 0)
            return 0.0;

        var totalSqZ = 0.0;
        foreach (var z in recentStd)
        {
            foreach (var featureIndex in activeFeatureIndices)
            {
                var value = featureIndex < z.Length ? z[featureIndex] : 0f;
                if (float.IsFinite(value))
                    totalSqZ += value * value;
            }
        }

        var score = totalSqZ / (recentStd.Count * (double)activeFeatureIndices.Count);
        return double.IsFinite(score) ? score : 0.0;
    }

    private static double[] ComputeExpectedBinProbs()
    {
        var numBins = NumInnerBins + 2;
        var probs = new double[numBins];

        probs[0] = NormalCdf(-3.0);
        for (var bin = 0; bin < NumInnerBins; bin++)
        {
            var lo = ZMin + bin * BinWidth;
            var hi = lo + BinWidth;
            probs[bin + 1] = NormalCdf(hi) - NormalCdf(lo);
        }

        probs[numBins - 1] = 1.0 - NormalCdf(3.0);
        return probs;
    }

    private static double NormalCdf(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    private static double Erf(double x)
    {
        const double p = 0.3275911;
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;

        var sign = x < 0 ? -1 : 1;
        var xAbs = Math.Abs(x);
        var t = 1.0 / (1.0 + p * xAbs);
        var poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
        return sign * (1.0 - poly * Math.Exp(-xAbs * xAbs));
    }

    private static EngineConfigUpsertSpec CreateDriftedFeaturesConfigSpec(
        MLModel model,
        IReadOnlyList<DriftedFeature> driftedFeatures)
    {
        var value = JsonSerializer.Serialize(
            driftedFeatures.Select(feature => new
            {
                featureName = feature.Name,
                psi = feature.Psi
            }),
            JsonOptions);

        return new EngineConfigUpsertSpec(
            $"MLCovariate:{model.Symbol}:{model.Timeframe}:DriftedFeatures",
            value,
            ConfigDataType.Json,
            "Per-feature PSI drift diagnostics from MLCovariateShiftWorker.",
            true);
    }

    private static string ResolveFeatureName(ModelSnapshot snapshot, int featureIndex)
    {
        if (featureIndex >= 0
            && featureIndex < snapshot.Features.Length
            && !string.IsNullOrWhiteSpace(snapshot.Features[featureIndex]))
        {
            return snapshot.Features[featureIndex];
        }

        var rawIndex = featureIndex;
        if (featureIndex >= 0 && featureIndex < snapshot.RawFeatureIndices.Length)
            rawIndex = snapshot.RawFeatureIndices[featureIndex];

        var names = MLFeatureHelper.ResolveFeatureNames(Math.Max(rawIndex + 1, MLFeatureHelper.FeatureCount));
        return rawIndex >= 0 && rawIndex < names.Length
            ? names[rawIndex]
            : $"feature[{featureIndex}]";
    }

    private static string DescribeShiftKind(ModelCovariateShiftEvaluation evaluation)
        => (evaluation.UnivariateShift, evaluation.MultivariateShift) switch
        {
            (true, true) => string.Create(
                CultureInfo.InvariantCulture,
                $"univariate weightedPSI={evaluation.WeightedPsi:F4}, maxPSI={evaluation.MaxPsi:F4} on {evaluation.MaxFeatureName}; multivariate={evaluation.MultivariateScore:F4}"),
            (true, false) => string.Create(
                CultureInfo.InvariantCulture,
                $"univariate weightedPSI={evaluation.WeightedPsi:F4}, maxPSI={evaluation.MaxPsi:F4} on {evaluation.MaxFeatureName}"),
            (false, true) => string.Create(
                CultureInfo.InvariantCulture,
                $"multivariate={evaluation.MultivariateScore:F4}"),
            _ => "unknown",
        };

    private static int GetInt(
        IReadOnlyDictionary<string, string> config,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        if (!config.TryGetValue(key, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return Clamp(parsed, min, max);
    }

    private static double GetDouble(
        IReadOnlyDictionary<string, string> config,
        string key,
        double defaultValue,
        double min,
        double max)
    {
        if (!config.TryGetValue(key, out var raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            return defaultValue;
        }

        return ClampFinite(parsed, min, max);
    }

    private static TimeSpan GetIntervalWithJitter(MLCovariateShiftSettings settings)
    {
        if (settings.PollJitter <= TimeSpan.Zero)
            return settings.PollInterval;

        var jitterMs = Random.Shared.NextDouble() * settings.PollJitter.TotalMilliseconds;
        return settings.PollInterval + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static TimeSpan CalculateDelay(TimeSpan baseDelay, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseDelay;

        var multiplier = Math.Min(8, 1 << Math.Min(consecutiveFailures, 3));
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * multiplier);
    }

    private void RecordModelSkipped(string reason)
        => _metrics?.MLCovariateShiftModelsSkipped.Add(1, Tag("reason", reason));

    private void RecordRetrainingSkipped(string reason)
        => _metrics?.MLCovariateShiftRetrainingSkipped.Add(1, Tag("reason", reason));

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLCovariateShiftCyclesSkipped.Add(1, Tag("reason", reason));

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static double ClampFinite(double value, double min, double max)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private sealed record DriftedFeature(string Name, double Psi);

    private sealed record ModelCovariateShiftEvaluation(
        bool Evaluated,
        string? SkipReason,
        bool ShiftDetected,
        double WeightedPsi,
        double MaxPsi,
        double MultivariateScore,
        string MaxFeatureName,
        IReadOnlyList<DriftedFeature> DriftedFeatures,
        bool UnivariateShift,
        bool MultivariateShift)
    {
        public static ModelCovariateShiftEvaluation Skipped(string reason)
            => new(
                false,
                reason,
                false,
                0.0,
                0.0,
                0.0,
                string.Empty,
                Array.Empty<DriftedFeature>(),
                false,
                false);
    }

    private sealed record RetrainingQueueResult(string Reason)
    {
        public static RetrainingQueueResult Queued { get; } = new("queued");
        public static RetrainingQueueResult AlreadyQueued { get; } = new("retrain_already_queued");
        public static RetrainingQueueResult QueueFull { get; } = new("training_queue_full");
        public static RetrainingQueueResult Cooldown { get; } = new("retrain_cooldown");
    }
}

internal sealed record MLCovariateShiftSettings
{
    public bool Enabled { get; init; }
    public TimeSpan InitialDelay { get; init; }
    public TimeSpan PollInterval { get; init; }
    public TimeSpan PollJitter { get; init; }
    public int WindowDays { get; init; }
    public double PsiThreshold { get; init; }
    public int MinCandles { get; init; }
    public int TrainingDays { get; init; }
    public double MultivariateThreshold { get; init; }
    public double PerFeaturePsiThreshold { get; init; }
    public int MaxModelsPerCycle { get; init; }
    public int MaxQueuedRetrains { get; init; }
    public TimeSpan RetrainCooldown { get; init; }
    public TimeSpan LockTimeout { get; init; }
}

internal sealed record MLCovariateShiftCycleResult(
    MLCovariateShiftSettings Settings,
    string? SkippedReason,
    int ModelsSeen,
    int ModelsEvaluated,
    int ModelsSkipped,
    int ShiftsDetected,
    int RetrainingQueued,
    int DriftedFeatureConfigWrites,
    int TrainingBacklogDepth,
    bool Truncated)
{
    public static MLCovariateShiftCycleResult Skipped(
        MLCovariateShiftSettings settings,
        string reason)
        => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, false);
}
