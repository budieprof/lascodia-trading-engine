using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors ML feature freshness by measuring lag-1 autocorrelation for each model feature.
/// </summary>
/// <remarks>
/// The worker prefers persisted raw prediction feature vectors, which match the exact
/// deployed model schema including CPC and interaction features. It falls back to recent
/// candle-derived V1 feature vectors for legacy models without raw prediction feature logs.
/// A feature is stale when it is constant/near-constant or when consecutive observations are
/// too strongly autocorrelated. One active row is maintained per <c>(MLModelId, FeatureName)</c>.
/// </remarks>
public sealed class MLFeatureStalenessWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLFeatureStalenessWorker);

    private const string DistributedLockKey = "ml:feature-staleness:cycle";
    private const string RawPredictionMethod = "RawPredictionFeatureLag1";
    private const string CandleFallbackMethod = "CandleFeatureLag1Fallback";

    private const string CK_Enabled = "MLFeatureStaleness:Enabled";
    private const string CK_InitialDelaySeconds = "MLFeatureStaleness:InitialDelaySeconds";
    private const string CK_PollSecs = "MLFeatureStaleness:PollIntervalSeconds";
    private const string CK_MinSamples = "MLFeatureStaleness:MinSamples";
    private const string CK_MaxRowsPerModel = "MLFeatureStaleness:MaxRowsPerModel";
    private const string CK_MaxCandlesPerModel = "MLFeatureStaleness:MaxCandlesPerModel";
    private const string CK_MaxFeatures = "MLFeatureStaleness:MaxFeatures";
    private const string CK_MaxModelsPerCycle = "MLFeatureStaleness:MaxModelsPerCycle";
    private const string CK_AbsAutocorrThreshold = "MLFeatureStaleness:AbsAutocorrThreshold";
    private const string CK_ConstantVarianceEpsilon = "MLFeatureStaleness:ConstantVarianceEpsilon";
    private const string CK_MaxStaleFeatureFraction = "MLFeatureStaleness:MaxStaleFeatureFraction";
    private const string CK_RetentionDays = "MLFeatureStaleness:RetentionDays";
    private const string CK_LockTimeoutSecs = "MLFeatureStaleness:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLFeatureStaleness:DbCommandTimeoutSeconds";

    private const int DefaultInitialDelaySeconds = 0;
    private const int DefaultPollSeconds = 7 * 24 * 60 * 60;
    private const int DefaultMinSamples = 50;
    private const int DefaultMaxRowsPerModel = 1_000;
    private const int DefaultMaxCandlesPerModel = 300;
    private const int DefaultMaxFeatures = MLFeatureHelper.FeatureCountV7;
    private const int DefaultMaxModelsPerCycle = 256;
    private const int DefaultRetentionDays = 90;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultDbCommandTimeoutSeconds = 30;
    private const int MaxFeatureNameLength = 100;
    private const double DefaultAbsAutocorrThreshold = 0.95;
    private const double DefaultConstantVarianceEpsilon = 1.0e-9;
    private const double DefaultMaxStaleFeatureFraction = 0.25;

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySeconds,
        CK_PollSecs,
        CK_MinSamples,
        CK_MaxRowsPerModel,
        CK_MaxCandlesPerModel,
        CK_MaxFeatures,
        CK_MaxModelsPerCycle,
        CK_AbsAutocorrThreshold,
        CK_ConstantVarianceEpsilon,
        CK_MaxStaleFeatureFraction,
        CK_RetentionDays,
        CK_LockTimeoutSecs,
        CK_DbCommandTimeoutSeconds
    ];

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureStalenessWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLFeatureStalenessOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLFeatureStalenessWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureStalenessWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLFeatureStalenessOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLFeatureStalenessOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Ranks stale, highly repetitive model features from raw prediction vectors.",
            TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds)));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds));

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                               + TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(_options.InitialDelaySeconds));
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                    _metrics?.MLFeatureStalenessTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var started = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Config.PollInterval;

                        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateModelCount);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            elapsedMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLFeatureStalenessCycleDurationMs.Record(elapsedMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, skipped={Skipped}, failed={Failed}, logsWritten={LogsWritten}, staleFeatures={StaleFeatures}, rowsPruned={RowsPruned}.",
                                WorkerName,
                                result.CandidateModelCount,
                                result.EvaluatedModelCount,
                                result.SkippedModelCount,
                                result.FailedModelCount,
                                result.LogRowsWritten,
                                result.StaleFeatureCount,
                                result.RowsPruned);
                        }

                        var previousFailures = ConsecutiveCycleFailures;
                        if (previousFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, previousFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName,
                                previousFailures);
                        }

                        ConsecutiveCycleFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveCycleFailuresField);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_feature_staleness_cycle"));
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                var delay = ConsecutiveCycleFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveCycleFailures)
                    : WakeInterval;
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<FeatureStalenessCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var config = await LoadConfigAsync(readCtx, _options, ct);

        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            return FeatureStalenessCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLFeatureStalenessLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate active rows are possible in multi-instance deployments.",
                    WorkerName);
            }
        }
        else
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(config.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLFeatureStalenessLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return FeatureStalenessCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLFeatureStalenessLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task<FeatureStalenessCycleResult> RunFeatureStalenessAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
            return FeatureStalenessCycleResult.Skipped(config, "disabled");

        return await RunCycleCoreAsync(readCtx, writeCtx, config, ct);
    }

    private async Task<FeatureStalenessCycleResult> RunCycleCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var models = await LoadActiveModelsAsync(readCtx, config.MaxModelsPerCycle, ct);
        if (models.Truncated)
            RecordCycleSkipped("model_limit");

        _healthMonitor?.RecordBacklogDepth(WorkerName, models.Items.Count);

        int evaluated = 0;
        int skipped = 0;
        int failed = 0;
        int logsWritten = 0;
        int staleFeatures = 0;
        int rowsPruned = 0;

        foreach (var model in models.Items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await ProcessModelAsync(
                    readCtx,
                    writeCtx,
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    model.ModelBytes,
                    config,
                    ct);

                if (result.Evaluated)
                {
                    evaluated++;
                    logsWritten += result.LogRowsWritten;
                    staleFeatures += result.StaleFeatureCount;
                    rowsPruned += result.RowsPruned;
                    RecordModelMetrics(model, result);
                }
                else
                {
                    skipped++;
                    _metrics?.MLFeatureStalenessModelsSkipped.Add(
                        1,
                        Tag("reason", result.State),
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe.ToString()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                skipped++;
                writeCtx.ChangeTracker.Clear();
                _metrics?.MLFeatureStalenessModelsSkipped.Add(
                    1,
                    Tag("reason", "model_error"),
                    Tag("symbol", model.Symbol),
                    Tag("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed model {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }
        }

        var expiredRowsPruned = await PruneExpiredRowsAsync(writeCtx, config, ct);
        rowsPruned += expiredRowsPruned;

        _metrics?.MLFeatureStalenessModelsEvaluated.Add(evaluated);
        if (skipped > 0)
            _metrics?.MLFeatureStalenessModelsSkipped.Add(skipped, Tag("reason", "cycle_total"));
        if (logsWritten > 0)
            _metrics?.MLFeatureStalenessLogsWritten.Add(logsWritten);
        if (staleFeatures > 0)
            _metrics?.MLFeatureStalenessStaleFeatures.Add(staleFeatures);
        if (expiredRowsPruned > 0)
            _metrics?.MLFeatureStalenessRowsPruned.Add(expiredRowsPruned, Tag("reason", "expired"));

        _logger.LogInformation(
            "{Worker} cycle complete: evaluated={Evaluated}, skipped={Skipped}, failed={Failed}, models={Total}, truncated={Truncated}.",
            WorkerName,
            evaluated,
            skipped,
            failed,
            models.Items.Count,
            models.Truncated);

        return new FeatureStalenessCycleResult(
            Config: config,
            CandidateModelCount: models.Items.Count,
            EvaluatedModelCount: evaluated,
            SkippedModelCount: skipped,
            FailedModelCount: failed,
            LogRowsWritten: logsWritten,
            StaleFeatureCount: staleFeatures,
            RowsPruned: rowsPruned,
            Truncated: models.Truncated,
            SkippedReason: null);
    }

    private static async Task<(List<ModelProjection> Items, bool Truncated)> LoadActiveModelsAsync(
        DbContext db,
        int maxModels,
        CancellationToken ct)
    {
        var candidates = db.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && m.ModelBytes != null
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion));

        var rows = await candidates
            .Where(m => !candidates.Any(other =>
                other.Symbol == m.Symbol
                && other.Timeframe == m.Timeframe
                && (other.TrainedAt > m.TrainedAt
                    || (other.TrainedAt == m.TrainedAt && other.Id > m.Id))))
            .OrderBy(m => m.Symbol)
            .ThenBy(m => m.Timeframe)
            .Take(maxModels + 1)
            .Select(m => new ModelProjection(m.Id, m.Symbol, m.Timeframe, m.ModelBytes!))
            .ToListAsync(ct);

        var truncated = rows.Count > maxModels;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);

        return (rows, truncated);
    }

    private async Task<FeatureStalenessModelResult> ProcessModelAsync(
        DbContext readCtx,
        DbContext writeCtx,
        long modelId,
        string symbol,
        Timeframe timeframe,
        byte[] modelBytes,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var snapshot = TryDeserializeSnapshot(modelBytes, modelId);
        if (snapshot is null)
            return FeatureStalenessModelResult.Skipped("invalid_snapshot");

        int resolvedFeatureCount = snapshot.ResolveExpectedInputFeatures();
        if (resolvedFeatureCount < 1 || resolvedFeatureCount > MLFeatureHelper.MaxAllowedFeatureCount)
            return FeatureStalenessModelResult.Skipped("invalid_feature_count");

        int featureLimit = Math.Clamp(config.MaxFeatures, 1, resolvedFeatureCount);
        var featureNames = ResolveFeatureNames(snapshot, resolvedFeatureCount);

        var rows = await LoadRawPredictionRowsAsync(
            readCtx,
            modelId,
            resolvedFeatureCount,
            featureLimit,
            config,
            ct);
        string method = RawPredictionMethod;

        if (rows.Count < config.MinSamples)
        {
            rows = await LoadCandleFallbackRowsAsync(
                readCtx,
                symbol,
                timeframe,
                resolvedFeatureCount,
                featureLimit,
                config,
                ct);
            method = CandleFallbackMethod;
        }

        if (rows.Count < config.MinSamples)
        {
            _logger.LogDebug(
                "{Worker}: model {ModelId} has usable rows {Rows}/{Min}; featureCount={FeatureCount}.",
                WorkerName,
                modelId,
                rows.Count,
                config.MinSamples,
                resolvedFeatureCount);
            return FeatureStalenessModelResult.Skipped("insufficient_samples", rows.Count, featureLimit, method);
        }

        var scores = ScoreFeatures(rows, featureLimit, config).ToList();
        ApplyStaleCap(scores, config.MaxStaleFeatureFraction);

        await using var tx = await writeCtx.Database.BeginTransactionAsync(ct);
        var existingRows = await writeCtx.Set<MLFeatureStalenessLog>()
            .Where(l => l.MLModelId == modelId && !l.IsDeleted)
            .ToListAsync(ct);

        var groupedExisting = existingRows
            .GroupBy(l => l.FeatureName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var currentNames = featureNames.Take(featureLimit).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rowsPruned = 0;

        foreach (var staleRow in existingRows.Where(row => !currentNames.Contains(row.FeatureName)))
        {
            staleRow.IsDeleted = true;
            rowsPruned++;
        }

        int staleCount = 0;
        foreach (var score in scores)
        {
            string featureName = featureNames[score.FeatureIndex];
            if (score.IsStale)
                staleCount++;

            if (groupedExisting.TryGetValue(featureName, out var matches) && matches.Count > 0)
            {
                var existing = matches[0];
                existing.Symbol = symbol;
                existing.Timeframe = timeframe;
                existing.Lag1Autocorr = score.Lag1Autocorr;
                existing.IsStale = score.IsStale;
                existing.ComputedAt = nowUtc;
                existing.IsDeleted = false;

                foreach (var duplicate in matches.Skip(1))
                {
                    duplicate.IsDeleted = true;
                    rowsPruned++;
                }
            }
            else
            {
                writeCtx.Set<MLFeatureStalenessLog>().Add(new MLFeatureStalenessLog
                {
                    MLModelId = modelId,
                    Symbol = symbol,
                    Timeframe = timeframe,
                    FeatureName = featureName,
                    Lag1Autocorr = score.Lag1Autocorr,
                    IsStale = score.IsStale,
                    ComputedAt = nowUtc
                });
            }
        }

        await writeCtx.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (rowsPruned > 0)
            _metrics?.MLFeatureStalenessRowsPruned.Add(rowsPruned, Tag("reason", "model_scope"));

        _logger.LogInformation(
            "{Worker}: {Symbol}/{Timeframe} model={ModelId} method={Method} rows={Rows}, stale={Stale}/{Features}, threshold={Threshold:F3}.",
            WorkerName,
            symbol,
            timeframe,
            modelId,
            method,
            rows.Count,
            staleCount,
            featureLimit,
            config.AbsAutocorrThreshold);

        return new FeatureStalenessModelResult(
            Evaluated: true,
            State: "evaluated",
            RowsUsed: rows.Count,
            FeatureCount: featureLimit,
            StaleFeatureCount: staleCount,
            LogRowsWritten: scores.Count,
            RowsPruned: rowsPruned,
            Method: method,
            Lag1Autocorrelations: scores.Select(score => score.Lag1Autocorr).ToArray());
    }

    private static async Task<List<double[]>> LoadRawPredictionRowsAsync(
        DbContext readCtx,
        long modelId,
        int resolvedFeatureCount,
        int featureLimit,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var jsonRows = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == modelId
                     && !l.IsDeleted
                     && l.RawFeaturesJson != null
                     && l.RawFeaturesJson != string.Empty)
            .OrderByDescending(l => l.PredictedAt)
            .ThenByDescending(l => l.Id)
            .Take(config.MaxRowsPerModel)
            .Select(l => l.RawFeaturesJson!)
            .ToListAsync(ct);

        var rows = new List<double[]>(jsonRows.Count);
        foreach (string json in jsonRows)
        {
            double[]? values;
            try
            {
                values = JsonSerializer.Deserialize<double[]>(json, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (values is null || values.Length < resolvedFeatureCount)
                continue;

            var row = new double[featureLimit];
            bool finite = true;
            for (int i = 0; i < featureLimit; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    finite = false;
                    break;
                }

                row[i] = values[i];
            }

            if (finite)
                rows.Add(row);
        }

        rows.Reverse();
        return rows;
    }

    private static async Task<List<double[]>> LoadCandleFallbackRowsAsync(
        DbContext readCtx,
        string symbol,
        Timeframe timeframe,
        int resolvedFeatureCount,
        int featureLimit,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        var candles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == timeframe && !c.IsDeleted)
            .OrderByDescending(c => c.Timestamp)
            .Take(config.MaxCandlesPerModel)
            .ToListAsync(ct);

        candles.Reverse();
        if (candles.Count < MLFeatureHelper.LookbackWindow + 2)
            return [];

        var samples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (samples.Count == 0 || samples[0].Features.Length < resolvedFeatureCount)
            return [];

        var rows = new List<double[]>(samples.Count);
        foreach (var sample in samples)
        {
            if (sample.Features.Length < resolvedFeatureCount)
                continue;

            var row = new double[featureLimit];
            bool finite = true;
            for (int i = 0; i < featureLimit; i++)
            {
                double value = sample.Features[i];
                if (!double.IsFinite(value))
                {
                    finite = false;
                    break;
                }

                row[i] = value;
            }

            if (finite)
                rows.Add(row);
        }

        return rows;
    }

    internal static IEnumerable<FeatureStalenessScore> ScoreFeatures(
        IReadOnlyList<double[]> rows,
        int featureCount,
        FeatureStalenessConfig config)
    {
        if (rows.Count == 0 || featureCount <= 0)
            yield break;

        var safeFeatureCount = Math.Min(featureCount, rows.Min(row => row.Length));
        for (int featureIndex = 0; featureIndex < safeFeatureCount; featureIndex++)
        {
            var values = new double[rows.Count];
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                values[rowIndex] = rows[rowIndex][featureIndex];

            var autocorr = ComputeLag1Autocorr(values, config.ConstantVarianceEpsilon);
            bool stale = autocorr.IsDegenerate || Math.Abs(autocorr.Correlation) >= config.AbsAutocorrThreshold;
            yield return new FeatureStalenessScore(
                featureIndex,
                autocorr.Correlation,
                stale,
                autocorr.IsDegenerate);
        }
    }

    internal static Lag1AutocorrResult ComputeLag1Autocorr(
        IReadOnlyList<double> values,
        double constantVarianceEpsilon = DefaultConstantVarianceEpsilon)
    {
        if (values.Count < 3)
            return new Lag1AutocorrResult(0.0, IsDegenerate: true);

        double mean = values.Average();
        double variance = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            double delta = values[i] - mean;
            variance += delta * delta;
        }

        variance /= values.Count;
        if (variance <= constantVarianceEpsilon)
            return new Lag1AutocorrResult(1.0, IsDegenerate: true);

        int pairCount = values.Count - 1;
        double mean0 = 0.0, mean1 = 0.0;
        for (int i = 0; i < pairCount; i++)
        {
            mean0 += values[i];
            mean1 += values[i + 1];
        }

        mean0 /= pairCount;
        mean1 /= pairCount;

        double covariance = 0.0, variance0 = 0.0, variance1 = 0.0;
        for (int i = 0; i < pairCount; i++)
        {
            double d0 = values[i] - mean0;
            double d1 = values[i + 1] - mean1;
            covariance += d0 * d1;
            variance0 += d0 * d0;
            variance1 += d1 * d1;
        }

        double denominator = Math.Sqrt(variance0) * Math.Sqrt(variance1);
        if (denominator <= constantVarianceEpsilon)
            return new Lag1AutocorrResult(1.0, IsDegenerate: true);

        return new Lag1AutocorrResult(Math.Clamp(covariance / denominator, -1.0, 1.0), IsDegenerate: false);
    }

    internal static void ApplyStaleCap(List<FeatureStalenessScore> scores, double maxStaleFeatureFraction)
    {
        if (scores.Count == 0)
            return;

        int maxStale = (int)Math.Floor(scores.Count * Math.Clamp(maxStaleFeatureFraction, 0.0, 1.0));
        if (maxStaleFeatureFraction > 0 && maxStale == 0)
            maxStale = 1;

        var allowed = scores
            .Where(s => s.IsStale)
            .OrderByDescending(s => s.IsDegenerate)
            .ThenByDescending(s => Math.Abs(s.Lag1Autocorr))
            .Take(maxStale)
            .Select(s => s.FeatureIndex)
            .ToHashSet();

        for (int i = 0; i < scores.Count; i++)
        {
            var score = scores[i];
            if (score.IsStale && !allowed.Contains(score.FeatureIndex))
                scores[i] = score with { IsStale = false };
        }
    }

    private async Task<int> PruneExpiredRowsAsync(
        DbContext writeCtx,
        FeatureStalenessConfig config,
        CancellationToken ct)
    {
        if (config.RetentionDays <= 0)
            return 0;

        var retentionCutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-config.RetentionDays);
        var expiredRows = await writeCtx.Set<MLFeatureStalenessLog>()
            .Where(l => l.ComputedAt < retentionCutoff && !l.IsDeleted)
            .ToListAsync(ct);
        foreach (var row in expiredRows)
            row.IsDeleted = true;

        if (expiredRows.Count > 0)
        {
            await writeCtx.SaveChangesAsync(ct);
            _logger.LogInformation(
                "{Worker}: soft-pruned {Count} staleness logs older than {Days} days.",
                WorkerName,
                expiredRows.Count,
                config.RetentionDays);
        }

        return expiredRows.Count;
    }

    private static string[] ResolveFeatureNames(ModelSnapshot snapshot, int featureCount)
    {
        var names = snapshot.Features.Length >= featureCount
            ? snapshot.Features.Take(featureCount)
            : MLFeatureHelper.ResolveFeatureNames(featureCount);

        return EnsureUniqueFeatureNames(names, featureCount);
    }

    private static string[] EnsureUniqueFeatureNames(IEnumerable<string> names, int featureCount)
    {
        var result = new string[featureCount];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = names.ToArray();

        for (int i = 0; i < featureCount; i++)
        {
            var baseName = i < source.Length
                ? NormalizeFeatureName(source[i], i)
                : NormalizeFeatureName(null, i);
            var candidate = baseName;
            var suffix = 2;

            while (!seen.Add(candidate))
            {
                var suffixText = "_" + suffix.ToString(CultureInfo.InvariantCulture);
                candidate = TrimFeatureName(baseName, MaxFeatureNameLength - suffixText.Length) + suffixText;
                suffix++;
            }

            result[i] = candidate;
        }

        return result;
    }

    private static string NormalizeFeatureName(string? value, int index)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            trimmed = "Feature" + index.ToString(CultureInfo.InvariantCulture);

        return TrimFeatureName(trimmed, MaxFeatureNameLength);
    }

    private static string TrimFeatureName(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..Math.Max(1, maxLength)];

    private ModelSnapshot? TryDeserializeSnapshot(byte[] modelBytes, long modelId)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelSnapshot>(modelBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to deserialize snapshot for model {ModelId}.",
                WorkerName,
                modelId);
            return null;
        }
    }

    internal static async Task<FeatureStalenessConfig> LoadConfigAsync(
        DbContext ctx,
        MLFeatureStalenessOptions options,
        CancellationToken ct)
    {
        var rows = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => ConfigKeys.Contains(c.Key) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Key, c.Value, c.LastUpdatedAt })
            .ToListAsync(ct);

        var values = rows
            .Where(c => c.Value is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.LastUpdatedAt).ThenBy(c => c.Id).Last().Value!,
                StringComparer.Ordinal);

        var minSamples = NormalizeMinSamples(GetConfig(values, CK_MinSamples, options.MinSamples));
        var maxRows = Math.Max(
            minSamples,
            NormalizeMaxRowsPerModel(GetConfig(values, CK_MaxRowsPerModel, options.MaxRowsPerModel)));
        var minFallbackCandles = minSamples + MLFeatureHelper.LookbackWindow + 1;
        var maxCandles = Math.Max(
            minFallbackCandles,
            NormalizeMaxCandlesPerModel(GetConfig(values, CK_MaxCandlesPerModel, options.MaxCandlesPerModel)));
        var pollSeconds = NormalizePollSeconds(GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));

        return new FeatureStalenessConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySeconds, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollSeconds: pollSeconds,
            MinSamples: minSamples,
            MaxRowsPerModel: maxRows,
            MaxCandlesPerModel: maxCandles,
            MaxFeatures: NormalizeMaxFeatures(GetConfig(values, CK_MaxFeatures, options.MaxFeatures)),
            MaxModelsPerCycle: NormalizeMaxModelsPerCycle(
                GetConfig(values, CK_MaxModelsPerCycle, options.MaxModelsPerCycle)),
            AbsAutocorrThreshold: NormalizeAbsAutocorrThreshold(
                GetConfig(values, CK_AbsAutocorrThreshold, options.AbsAutocorrThreshold)),
            ConstantVarianceEpsilon: NormalizeConstantVarianceEpsilon(
                GetConfig(values, CK_ConstantVarianceEpsilon, options.ConstantVarianceEpsilon)),
            MaxStaleFeatureFraction: NormalizeMaxStaleFeatureFraction(
                GetConfig(values, CK_MaxStaleFeatureFraction, options.MaxStaleFeatureFraction)),
            RetentionDays: NormalizeRetentionDays(GetConfig(values, CK_RetentionDays, options.RetentionDays)),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSecs, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSeconds, options.DbCommandTimeoutSeconds)));
    }

    private static T GetConfig<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
            return defaultValue;

        return TryConvertConfig(raw, out T parsed)
            ? parsed
            : defaultValue;
    }

    private static bool TryConvertConfig<T>(string value, out T result)
    {
        object? parsed = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        var normalized = value.Trim();

        if (targetType == typeof(string))
        {
            parsed = value;
        }
        else if (targetType == typeof(int)
                 && int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
        }
        else if (targetType == typeof(double)
                 && double.TryParse(normalized, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            parsed = doubleValue;
        }
        else if (targetType == typeof(bool)
                 && TryParseBool(normalized, out var boolValue))
        {
            parsed = boolValue;
        }

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }

    internal static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : DefaultInitialDelaySeconds;

    internal static int NormalizePollSeconds(int value)
        => value is >= 60 and <= 604_800 ? value : DefaultPollSeconds;

    internal static int NormalizeMinSamples(int value)
        => value is >= 20 and <= 100_000 ? value : DefaultMinSamples;

    internal static int NormalizeMaxRowsPerModel(int value)
        => value is >= 50 and <= 100_000 ? value : DefaultMaxRowsPerModel;

    internal static int NormalizeMaxCandlesPerModel(int value)
        => value is >= MLFeatureHelper.LookbackWindow + 2 and <= 100_000 ? value : DefaultMaxCandlesPerModel;

    internal static int NormalizeMaxFeatures(int value)
        => value is >= 1 and <= MLFeatureHelper.MaxAllowedFeatureCount ? value : DefaultMaxFeatures;

    internal static int NormalizeMaxModelsPerCycle(int value)
        => value is >= 1 and <= 100_000 ? value : DefaultMaxModelsPerCycle;

    internal static double NormalizeAbsAutocorrThreshold(double value)
        => double.IsFinite(value) && value is >= 0.50 and <= 0.9999 ? value : DefaultAbsAutocorrThreshold;

    internal static double NormalizeConstantVarianceEpsilon(double value)
        => double.IsFinite(value) && value is >= 1.0e-12 and <= 1.0 ? value : DefaultConstantVarianceEpsilon;

    internal static double NormalizeMaxStaleFeatureFraction(double value)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0 ? value : DefaultMaxStaleFeatureFraction;

    internal static int NormalizeRetentionDays(int value)
        => value is >= 0 and <= 3_650 ? value : DefaultRetentionDays;

    internal static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : DefaultLockTimeoutSeconds;

    internal static int NormalizeDbCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : DefaultDbCommandTimeoutSeconds;

    private static bool TryParseBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        return false;
    }

    private void RecordModelMetrics(ModelProjection model, FeatureStalenessModelResult result)
    {
        _metrics?.MLFeatureStalenessUsableRows.Record(
            result.RowsUsed,
            Tag("symbol", model.Symbol),
            Tag("timeframe", model.Timeframe.ToString()),
            Tag("method", result.Method));
        _metrics?.MLFeatureStalenessStaleFeatureFraction.Record(
            result.FeatureCount > 0 ? (double)result.StaleFeatureCount / result.FeatureCount : 0.0,
            Tag("symbol", model.Symbol),
            Tag("timeframe", model.Timeframe.ToString()),
            Tag("method", result.Method));

        foreach (var lag1 in result.Lag1Autocorrelations)
        {
            _metrics?.MLFeatureStalenessLag1Autocorr.Record(
                lag1,
                Tag("symbol", model.Symbol),
                Tag("timeframe", model.Timeframe.ToString()),
                Tag("method", result.Method));
        }
    }

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLFeatureStalenessCyclesSkipped.Add(1, Tag("reason", reason));

    private static KeyValuePair<string, object?> Tag(string key, object? value)
        => new(key, value);

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException)
        {
            // Some providers do not expose relational command timeout configuration.
        }
    }

    internal readonly record struct Lag1AutocorrResult(double Correlation, bool IsDegenerate);

    internal readonly record struct FeatureStalenessScore(
        int FeatureIndex,
        double Lag1Autocorr,
        bool IsStale,
        bool IsDegenerate);

    internal sealed record FeatureStalenessConfig(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollSeconds,
        int MinSamples,
        int MaxRowsPerModel,
        int MaxCandlesPerModel,
        int MaxFeatures,
        int MaxModelsPerCycle,
        double AbsAutocorrThreshold,
        double ConstantVarianceEpsilon,
        double MaxStaleFeatureFraction,
        int RetentionDays,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds);

    internal sealed record FeatureStalenessCycleResult(
        FeatureStalenessConfig Config,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int SkippedModelCount,
        int FailedModelCount,
        int LogRowsWritten,
        int StaleFeatureCount,
        int RowsPruned,
        bool Truncated,
        string? SkippedReason)
    {
        public static FeatureStalenessCycleResult Skipped(FeatureStalenessConfig config, string reason)
            => new(config, 0, 0, 0, 0, 0, 0, 0, false, reason);
    }

    private sealed record ModelProjection(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        byte[] ModelBytes);

    private sealed record FeatureStalenessModelResult(
        bool Evaluated,
        string State,
        int RowsUsed,
        int FeatureCount,
        int StaleFeatureCount,
        int LogRowsWritten,
        int RowsPruned,
        string Method,
        IReadOnlyList<double> Lag1Autocorrelations)
    {
        public static FeatureStalenessModelResult Skipped(
            string reason,
            int rowsUsed = 0,
            int featureCount = 0,
            string method = RawPredictionMethod)
            => new(
                Evaluated: false,
                State: reason,
                RowsUsed: rowsUsed,
                FeatureCount: featureCount,
                StaleFeatureCount: 0,
                LogRowsWritten: 0,
                RowsPruned: 0,
                Method: method,
                Lag1Autocorrelations: []);
    }
}
