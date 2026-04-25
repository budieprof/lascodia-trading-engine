using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Builds a cross-symbol average-weight initializer for bagged-logistic cold starts.
/// </summary>
/// <remarks>
/// This worker deliberately restricts arithmetic weight averaging to
/// <see cref="LearnerArchitecture.BaggedLogistic"/> snapshots whose geometry and
/// feature/preprocessing contract match exactly. Averaging heterogeneous architectures or
/// incompatible snapshot layouts is not safe, so those sources are excluded.
///
/// The resulting initializer is stored as an <see cref="MLModel"/> with
/// <see cref="MLModel.IsMamlInitializer"/> set, <c>Symbol="ALL"</c>, and
/// <see cref="MLModel.LearnerArchitecture"/> fixed to
/// <see cref="LearnerArchitecture.BaggedLogistic"/>. <see cref="MLTrainingWorker"/>
/// can then use it as a warm-start parent for cold-start bagged-logistic training runs.
/// </remarks>
public sealed class MLAverageWeightInitWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLAverageWeightInitWorker);

    private const string DistributedLockKey = "workers:ml-average-weight-init:cycle";
    private const string InitializerSymbol = "ALL";
    private const string ColdStartConfigKey = "MLAvgWeightInit:UseForColdStart";
    private const int SourceFingerprintPrefixLength = 12;

    private const string CK_Enabled = "MLAvgWeightInit:Enabled";
    private const string CK_PollSecs = "MLAvgWeightInit:PollIntervalSeconds";
    private const string CK_MinRunsPerSourceContext = "MLAvgWeightInit:MinRunsPerSourceContext";
    private const string CK_MinSourceModelsPerInitializer = "MLAvgWeightInit:MinSourceModelsPerInitializer";
    private const string CK_LockTimeoutSeconds = "MLAvgWeightInit:LockTimeoutSeconds";

    private const int DefaultPollSeconds = 48 * 60 * 60;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 14 * 24 * 60 * 60;

    private const int DefaultMinRunsPerSourceContext = 5;
    private const int MinMinRunsPerSourceContext = 1;
    private const int MaxMinRunsPerSourceContext = 10_000;

    private const int DefaultMinSourceModelsPerInitializer = 5;
    private const int MinMinSourceModelsPerInitializer = 2;
    private const int MaxMinSourceModelsPerInitializer = 1_000;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly string[] WarmStartableModelTypes =
    [
        "BaggedLogisticEnsemble",
        "baggedlogisticensemble"
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLAverageWeightInitWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    private sealed record SourceRunCount(string Symbol, Timeframe Timeframe, LearnerArchitecture Architecture, int SuccessfulRunCount);

    private sealed record SourceModelRecord(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture Architecture,
        DateTime TrainedAt,
        byte[] ModelBytes,
        int SuccessfulRunCount);

    private sealed record SourceCandidate(
        long ModelId,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture Architecture,
        DateTime TrainedAt,
        int SuccessfulRunCount,
        ModelSnapshot Snapshot,
        string CompatibilityKey);

    private sealed record InitializerCluster(
        Timeframe Timeframe,
        LearnerArchitecture Architecture,
        string CompatibilityKey,
        IReadOnlyList<SourceCandidate> Sources);

    public MLAverageWeightInitWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLAverageWeightInitWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Builds a cross-symbol bagged-logistic average-weight initializer from compatible active production models so cold-start training can warm-start from a vetted meta-initializer.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        var currentDelay = TimeSpan.FromSeconds(DefaultPollSeconds);

        try
        {
            try
            {
                var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                long cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    currentDelay = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.SourceModelsEvaluated);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.MLAverageWeightInitCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "{Worker}: sourcesEvaluated={Sources}, clustersEvaluated={Clusters}, initializersWritten={Written}, initializersSkipped={Skipped}.",
                            WorkerName,
                            result.SourceModelsEvaluated,
                            result.ClustersEvaluated,
                            result.InitializersWritten,
                            result.InitializersSkipped);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName,
                            _consecutiveFailures);
                    }

                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "ml_average_weight_init_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(CalculateDelay(currentDelay, _consecutiveFailures), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<MLAverageWeightInitCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (!settings.Enabled)
        {
            _metrics?.MLAverageWeightInitCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return MLAverageWeightInitCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLAverageWeightInitLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate average-weight-init cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLAverageWeightInitLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLAverageWeightInitCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLAverageWeightInitCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLAverageWeightInitLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    return await RunCycleCoreAsync(writeContext, db, settings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }

        await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
        try
        {
            return await RunCycleCoreAsync(writeContext, db, settings, ct);
        }
        finally
        {
            WorkerBulkhead.MLMonitoring.Release();
        }
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return baseInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(DefaultPollSeconds)
                : baseInterval;
        }

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<MLAverageWeightInitCycleResult> RunCycleCoreAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        MLAverageWeightInitWorkerSettings settings,
        CancellationToken ct)
    {
        var sourceRunCounts = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .Where(run => !run.IsDeleted
                       && run.Status == RunStatus.Completed
                       && run.MLModelId != null
                       && run.Symbol != InitializerSymbol
                       && !run.IsMamlRun
                       && !run.IsDistillationRun
                       && !run.IsPretrainingRun
                       && run.LearnerArchitecture == LearnerArchitecture.BaggedLogistic)
            .GroupBy(run => new { run.Symbol, run.Timeframe, run.LearnerArchitecture })
            .Select(group => new SourceRunCount(
                group.Key.Symbol,
                group.Key.Timeframe,
                group.Key.LearnerArchitecture,
                group.Count()))
            .ToListAsync(ct);

        var qualifiedContexts = sourceRunCounts
            .Where(count => count.SuccessfulRunCount >= settings.MinRunsPerSourceContext)
            .ToDictionary(
                count => BuildContextKey(count.Symbol, count.Timeframe, count.Architecture),
                count => count.SuccessfulRunCount,
                StringComparer.Ordinal);

        if (qualifiedContexts.Count == 0)
        {
            _metrics?.MLAverageWeightInitCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_qualified_sources"));
            return MLAverageWeightInitCycleResult.Skipped(settings, "no_qualified_sources");
        }

        var sourceModelRecords = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(model => model.IsActive
                         && !model.IsDeleted
                         && model.Symbol != InitializerSymbol
                         && model.ModelBytes != null
                         && model.RegimeScope == null
                         && !model.IsMamlInitializer
                         && !model.IsMetaLearner
                         && !model.IsSoupModel
                         && !model.IsDistilled
                         && !model.IsSuppressed
                         && !model.IsFallbackChampion
                         && model.LearnerArchitecture == LearnerArchitecture.BaggedLogistic)
            .OrderBy(model => model.Timeframe)
            .ThenBy(model => model.Symbol)
            .Select(model => new SourceModelRecord(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.LearnerArchitecture,
                model.TrainedAt,
                model.ModelBytes!,
                0))
            .ToListAsync(ct);

        var candidates = new List<SourceCandidate>(sourceModelRecords.Count);
        int skippedSources = 0;

        foreach (var model in sourceModelRecords)
        {
            string contextKey = BuildContextKey(model.Symbol, model.Timeframe, model.Architecture);
            if (!qualifiedContexts.TryGetValue(contextKey, out int successfulRunCount))
            {
                _metrics?.MLAverageWeightInitInitializersSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "unqualified_source_context"));
                skippedSources++;
                continue;
            }

            _metrics?.MLAverageWeightInitSourceModelsEvaluated.Add(1);

            if (!TryCreateSourceCandidate(model with { SuccessfulRunCount = successfulRunCount }, out var candidate))
            {
                _metrics?.MLAverageWeightInitInitializersSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "invalid_source_snapshot"));
                skippedSources++;
                continue;
            }

            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
        {
            _metrics?.MLAverageWeightInitCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_compatible_sources"));
            return MLAverageWeightInitCycleResult.Skipped(settings, "no_compatible_sources");
        }

        var clusters = candidates
            .GroupBy(candidate => new { candidate.Timeframe, candidate.Architecture })
            .Select(group =>
                group.GroupBy(candidate => candidate.CompatibilityKey)
                    .OrderByDescending(cluster => cluster.Count())
                    .ThenByDescending(cluster => cluster.Max(candidate => candidate.TrainedAt))
                    .Select(cluster => new InitializerCluster(
                        group.Key.Timeframe,
                        group.Key.Architecture,
                        cluster.Key,
                        cluster.OrderBy(candidate => candidate.Symbol, StringComparer.OrdinalIgnoreCase).ToList()))
                    .First())
            .ToList();

        if (clusters.Count == 0)
        {
            _metrics?.MLAverageWeightInitCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_initializer_clusters"));
            return MLAverageWeightInitCycleResult.Skipped(settings, "no_initializer_clusters");
        }

        int initializersWritten = 0;
        int initializersSkipped = 0;
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var cluster in clusters)
        {
            if (cluster.Sources.Count < settings.MinSourceModelsPerInitializer)
            {
                initializersSkipped++;
                _metrics?.MLAverageWeightInitInitializersSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "insufficient_source_models"));
                continue;
            }

            var initializerSnapshot = BuildInitializerSnapshot(cluster.Sources, nowUtc);
            var sourceFingerprint = ComputeSourceFingerprint(cluster.Sources, cluster.CompatibilityKey);
            var modelVersion = BuildModelVersion(cluster.Architecture, cluster.Timeframe, sourceFingerprint);

            var activeInitializer = await db.Set<MLModel>()
                .Where(model => model.Symbol == InitializerSymbol
                             && model.Timeframe == cluster.Timeframe
                             && model.LearnerArchitecture == cluster.Architecture
                             && model.IsMamlInitializer
                             && model.IsActive
                             && !model.IsDeleted)
                .OrderByDescending(model => model.TrainedAt)
                .FirstOrDefaultAsync(ct);

            if (activeInitializer?.DatasetHash == sourceFingerprint)
            {
                initializersSkipped++;
                _metrics?.MLAverageWeightInitInitializersSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "unchanged"));
                continue;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var previousInitializers = await db.Set<MLModel>()
                .Where(model => model.Symbol == InitializerSymbol
                             && model.Timeframe == cluster.Timeframe
                             && model.LearnerArchitecture == cluster.Architecture
                             && model.IsMamlInitializer
                             && model.IsActive
                             && !model.IsDeleted)
                .ToListAsync(ct);

            foreach (var previous in previousInitializers)
            {
                previous.IsActive = false;
                previous.Status = MLModelStatus.Superseded;
                previous.IsMamlInitializer = true;
            }

            var initializerModel = new MLModel
            {
                Symbol = InitializerSymbol,
                Timeframe = cluster.Timeframe,
                LearnerArchitecture = cluster.Architecture,
                ModelVersion = modelVersion,
                Status = MLModelStatus.Active,
                IsActive = true,
                IsMamlInitializer = true,
                ModelBytes = JsonSerializer.SerializeToUtf8Bytes(initializerSnapshot),
                TrainedAt = nowUtc,
                ActivatedAt = nowUtc,
                TrainingSamples = cluster.Sources.Count,
                DatasetHash = sourceFingerprint,
            };

            db.Set<MLModel>().Add(initializerModel);
            await writeContext.SaveChangesAsync(ct);

            db.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol = InitializerSymbol,
                Timeframe = cluster.Timeframe,
                TriggerType = TriggerType.Scheduled,
                Status = RunStatus.Completed,
                FromDate = cluster.Sources.Min(source => source.TrainedAt),
                ToDate = nowUtc,
                TotalSamples = cluster.Sources.Count,
                MLModelId = initializerModel.Id,
                StartedAt = nowUtc,
                PickedUpAt = nowUtc,
                CompletedAt = nowUtc,
                LearnerArchitecture = cluster.Architecture,
                IsMamlRun = true,
                MamlInnerSteps = 0,
                HyperparamConfigJson = JsonSerializer.Serialize(new
                {
                    triggeredBy = WorkerName,
                    useForColdStart = ColdStartConfigKey,
                    compatibilityKey = cluster.CompatibilityKey,
                    sourceFingerprint,
                    sourceModelCount = cluster.Sources.Count,
                    sourceModelIds = cluster.Sources.Select(source => source.ModelId).ToArray(),
                    sourceSymbols = cluster.Sources.Select(source => source.Symbol).ToArray()
                })
            });

            await writeContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            initializersWritten++;
            _metrics?.MLAverageWeightInitInitializersWritten.Add(1);
            _metrics?.MLAverageWeightInitSourceModelsPerInitializer.Record(cluster.Sources.Count);

            _logger.LogInformation(
                "{Worker}: wrote average-weight initializer for {Architecture}/{Timeframe} from {Count} compatible source models (fingerprint={Fingerprint}).",
                WorkerName,
                cluster.Architecture,
                cluster.Timeframe,
                cluster.Sources.Count,
                sourceFingerprint[..SourceFingerprintPrefixLength]);
        }

        return new MLAverageWeightInitCycleResult(
            settings,
            SkippedReason: null,
            SourceModelsEvaluated: candidates.Count,
            ClustersEvaluated: clusters.Count,
            InitializersWritten: initializersWritten,
            InitializersSkipped: initializersSkipped + skippedSources);
    }

    private async Task<MLAverageWeightInitWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled,
            CK_PollSecs,
            CK_MinRunsPerSourceContext,
            CK_MinSourceModelsPerInitializer,
            CK_LockTimeoutSeconds
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        return new MLAverageWeightInitWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds),
                    DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            MinRunsPerSourceContext: ClampInt(GetInt(values, CK_MinRunsPerSourceContext, DefaultMinRunsPerSourceContext),
                DefaultMinRunsPerSourceContext, MinMinRunsPerSourceContext, MaxMinRunsPerSourceContext),
            MinSourceModelsPerInitializer: ClampInt(GetInt(values, CK_MinSourceModelsPerInitializer, DefaultMinSourceModelsPerInitializer),
                DefaultMinSourceModelsPerInitializer, MinMinSourceModelsPerInitializer, MaxMinSourceModelsPerInitializer),
            LockTimeoutSeconds: ClampIntAllowingZero(GetInt(values, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds),
                DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds));
    }

    private static bool TryCreateSourceCandidate(SourceModelRecord model, out SourceCandidate candidate)
    {
        candidate = null!;

        ModelSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
        }
        catch
        {
            return false;
        }

        if (snapshot is null)
            return false;

        if (!WarmStartableModelTypes.Contains(snapshot.Type, StringComparer.OrdinalIgnoreCase))
            return false;

        if (snapshot.Weights is not { Length: > 0 }
            || snapshot.Biases is not { Length: > 0 }
            || snapshot.Weights.Length != snapshot.Biases.Length
            || snapshot.BaseLearnersK <= 0
            || snapshot.BaseLearnersK != snapshot.Weights.Length)
        {
            return false;
        }

        if (snapshot.Means.Length == 0
            || snapshot.Stds.Length == 0
            || snapshot.Means.Length != snapshot.Stds.Length)
        {
            return false;
        }

        int weightWidth = snapshot.Weights[0]?.Length ?? 0;
        if (weightWidth <= 0)
            return false;

        if (snapshot.Weights.Any(row => row is null || row.Length != weightWidth))
            return false;

        if (!AreFinite(snapshot.Weights)
            || !AreFinite(snapshot.Biases)
            || !AreFinite(snapshot.Means)
            || !AreFinite(snapshot.Stds)
            || !AreFinite(snapshot.FeatureImportanceScores)
            || !AreFinite(snapshot.FeatureImportance)
            || !AreFinite(snapshot.MetaWeights)
            || !AreFinite(snapshot.LearnerAccuracyWeights))
        {
            return false;
        }

        var compatibilityPayload = JsonSerializer.Serialize(new
        {
            type = snapshot.Type,
            featureSchemaVersion = snapshot.ResolveFeatureSchemaVersion(),
            expectedInputFeatures = snapshot.ResolveExpectedInputFeatures(),
            features = snapshot.Features,
            featureSchemaFingerprint = snapshot.FeatureSchemaFingerprint,
            preprocessingFingerprint = snapshot.PreprocessingFingerprint,
            rawFeatureIndices = snapshot.RawFeatureIndices,
            featurePipelineTransforms = snapshot.FeaturePipelineTransforms,
            featurePipelineDescriptors = snapshot.FeaturePipelineDescriptors,
            activeFeatureMask = snapshot.ActiveFeatureMask,
            featureSubsetIndices = snapshot.FeatureSubsetIndices,
            baseLearnersK = snapshot.BaseLearnersK,
            weightRows = snapshot.Weights.Length,
            weightCols = snapshot.Weights.Select(row => row.Length).ToArray(),
            biasesLength = snapshot.Biases.Length,
            meansLength = snapshot.Means.Length,
            stdsLength = snapshot.Stds.Length
        });

        candidate = new SourceCandidate(
            model.Id,
            model.Symbol,
            model.Timeframe,
            model.Architecture,
            model.TrainedAt,
            model.SuccessfulRunCount,
            snapshot,
            ComputeSha256Hex(compatibilityPayload));
        return true;
    }

    private static ModelSnapshot BuildInitializerSnapshot(
        IReadOnlyList<SourceCandidate> sources,
        DateTime nowUtc)
    {
        var reference = DeepClone(sources[0].Snapshot);
        int sourceCount = sources.Count;
        int learnerCount = reference.Weights.Length;
        int featureCount = reference.Weights[0].Length;

        var averagedWeights = new double[learnerCount][];
        for (int learnerIndex = 0; learnerIndex < learnerCount; learnerIndex++)
            averagedWeights[learnerIndex] = new double[featureCount];

        var averagedBiases = new double[reference.Biases.Length];
        var averagedMeans = new float[reference.Means.Length];
        var averagedStds = new float[reference.Stds.Length];

        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            var snapshot = sources[sourceIndex].Snapshot;

            for (int learnerIndex = 0; learnerIndex < learnerCount; learnerIndex++)
            {
                for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
                    averagedWeights[learnerIndex][featureIndex] += snapshot.Weights[learnerIndex][featureIndex] / sourceCount;

                averagedBiases[learnerIndex] += snapshot.Biases[learnerIndex] / sourceCount;
            }

            for (int featureIndex = 0; featureIndex < averagedMeans.Length; featureIndex++)
            {
                averagedMeans[featureIndex] += snapshot.Means[featureIndex] / sourceCount;
                averagedStds[featureIndex] += snapshot.Stds[featureIndex] / sourceCount;
            }
        }

        reference.Version = $"avgwi-{ComputeSourceFingerprint(sources, sources[0].CompatibilityKey)[..SourceFingerprintPrefixLength]}";
        reference.TrainedOn = nowUtc;
        reference.TrainSamples = sourceCount;
        reference.TestSamples = 0;
        reference.CalSamples = 0;
        reference.SelectionSamples = 0;
        reference.EmbargoSamples = 0;
        reference.TrainSamplesAtLastCalibration = 0;
        reference.ParentModelId = 0;
        reference.GenerationNumber = 0;
        reference.Weights = averagedWeights;
        reference.Biases = averagedBiases;
        reference.Means = averagedMeans;
        reference.Stds = Array.ConvertAll(averagedStds, std => std <= 0f ? 1e-6f : std);
        reference.FeatureImportanceScores = AverageDoubleArrays(
            sources.Select(source => source.Snapshot.FeatureImportanceScores).ToList(),
            reference.FeatureImportanceScores.Length);
        reference.FeatureImportance = AverageFloatArrays(
            sources.Select(source => source.Snapshot.FeatureImportance).ToList(),
            reference.FeatureImportance.Length);
        reference.MetaWeights = AverageDoubleArrays(
            sources.Select(source => source.Snapshot.MetaWeights).ToList(),
            reference.MetaWeights.Length);
        reference.MetaBias = sources.Average(source => source.Snapshot.MetaBias);
        reference.LearnerAccuracyWeights = AverageDoubleArrays(
            sources.Select(source => source.Snapshot.LearnerAccuracyWeights).ToList(),
            reference.LearnerAccuracyWeights.Length);

        return reference;
    }

    private static float[] AverageFloatArrays(IReadOnlyList<float[]> arrays, int expectedLength)
    {
        if (expectedLength == 0 || arrays.Count == 0)
            return [];

        if (arrays.Any(array => array.Length != expectedLength))
            return [];

        var result = new float[expectedLength];
        int count = arrays.Count;

        foreach (var array in arrays)
        {
            for (int index = 0; index < expectedLength; index++)
                result[index] += array[index] / count;
        }

        return result;
    }

    private static double[] AverageDoubleArrays(IReadOnlyList<double[]> arrays, int expectedLength)
    {
        if (expectedLength == 0 || arrays.Count == 0)
            return [];

        if (arrays.Any(array => array.Length != expectedLength))
            return [];

        var result = new double[expectedLength];
        int count = arrays.Count;

        foreach (var array in arrays)
        {
            for (int index = 0; index < expectedLength; index++)
                result[index] += array[index] / count;
        }

        return result;
    }

    private static ModelSnapshot DeepClone(ModelSnapshot snapshot)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        return JsonSerializer.Deserialize<ModelSnapshot>(bytes) ?? new ModelSnapshot();
    }

    private static string BuildContextKey(string symbol, Timeframe timeframe, LearnerArchitecture architecture)
        => $"{symbol}|{timeframe}|{architecture}";

    private static string BuildModelVersion(
        LearnerArchitecture architecture,
        Timeframe timeframe,
        string sourceFingerprint)
        => $"avgwi-{architecture.ToString().ToLowerInvariant()}-{timeframe.ToString().ToLowerInvariant()}-{sourceFingerprint[..SourceFingerprintPrefixLength]}";

    private static string ComputeSourceFingerprint(
        IReadOnlyList<SourceCandidate> sources,
        string compatibilityKey)
    {
        var payload = JsonSerializer.Serialize(new
        {
            compatibilityKey,
            models = sources
                .OrderBy(source => source.ModelId)
                .Select(source => new
                {
                    source.ModelId,
                    source.Symbol,
                    source.Timeframe,
                    source.Architecture,
                    source.TrainedAt,
                    source.SuccessfulRunCount
                })
        });

        return ComputeSha256Hex(payload);
    }

    private static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool AreFinite(double[][] values)
    {
        foreach (var row in values)
        {
            if (row is null || !AreFinite(row))
                return false;
        }

        return true;
    }

    private static bool AreFinite(double[] values)
    {
        foreach (var value in values)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return false;
        }

        return true;
    }

    private static bool AreFinite(float[] values)
    {
        foreach (var value in values)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return false;
        }

        return true;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsedBool))
            return parsedBool;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;

        return defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        return values.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int ClampInt(int value, int fallback, int min, int max)
    {
        if (value <= 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static int ClampIntAllowingZero(int value, int fallback, int min, int max)
    {
        if (value < 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }
}

internal sealed record MLAverageWeightInitWorkerSettings(
    bool Enabled,
    TimeSpan PollInterval,
    int MinRunsPerSourceContext,
    int MinSourceModelsPerInitializer,
    int LockTimeoutSeconds);

internal sealed record MLAverageWeightInitCycleResult(
    MLAverageWeightInitWorkerSettings Settings,
    string? SkippedReason,
    int SourceModelsEvaluated,
    int ClustersEvaluated,
    int InitializersWritten,
    int InitializersSkipped)
{
    public static MLAverageWeightInitCycleResult Skipped(
        MLAverageWeightInitWorkerSettings settings,
        string reason)
        => new(
            settings,
            reason,
            SourceModelsEvaluated: 0,
            ClustersEvaluated: 0,
            InitializersWritten: 0,
            InitializersSkipped: 0);
}
