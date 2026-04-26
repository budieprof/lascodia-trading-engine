using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects structural feature-importance rank shifts between active ML champions and their
/// most recent superseded predecessors.
/// </summary>
public sealed class MLFeatureRankShiftWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLFeatureRankShiftWorker);

    private const string DistributedLockKey = "workers:ml-feature-rank-shift:cycle";
    private const string AlertDeduplicationPrefix = "MLFeatureRankShift:";
    private const string AlertReason = "feature_rank_shift";

    private const string CK_Enabled = "MLFeatureRankShift:Enabled";
    private const string CK_InitialDelaySeconds = "MLFeatureRankShift:InitialDelaySeconds";
    private const string CK_PollSecs = "MLFeatureRankShift:PollIntervalSeconds";
    private const string CK_TopN = "MLFeatureRankShift:TopFeatures";
    private const string CK_MinUnionFeatures = "MLFeatureRankShift:MinUnionFeatures";
    private const string CK_Threshold = "MLFeatureRankShift:RankCorrelationThreshold";
    private const string CK_Lookback = "MLFeatureRankShift:LookbackDays";
    private const string CK_MaxModelsPerCycle = "MLFeatureRankShift:MaxModelsPerCycle";
    private const string CK_MaxDivergingFeaturesInAlert = "MLFeatureRankShift:MaxDivergingFeaturesInAlert";
    private const string CK_LockTimeoutSeconds = "MLFeatureRankShift:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLFeatureRankShift:DbCommandTimeoutSeconds";
    private const string CK_AlertCooldownSeconds = "MLFeatureRankShift:AlertCooldownSeconds";
    private const string CK_AlertDest = "MLFeatureRankShift:AlertDestination";

    private const int DefaultInitialDelaySeconds = 0;
    private const int DefaultPollSeconds = 3_600;
    private const int DefaultTopFeatures = 10;
    private const int DefaultMinUnionFeatures = 3;
    private const int DefaultLookbackDays = 7;
    private const int DefaultMaxModelsPerCycle = 1_000;
    private const int DefaultMaxDivergingFeaturesInAlert = 5;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultDbCommandTimeoutSeconds = 30;
    private const double DefaultRankCorrelationThreshold = 0.50;
    private const string DefaultAlertDestination = "ml-ops";
    private const int AlertConditionMaxLength = 1_500;

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySeconds,
        CK_PollSecs,
        CK_TopN,
        CK_MinUnionFeatures,
        CK_Threshold,
        CK_Lookback,
        CK_MaxModelsPerCycle,
        CK_MaxDivergingFeaturesInAlert,
        CK_LockTimeoutSeconds,
        CK_DbCommandTimeoutSeconds,
        CK_AlertCooldownSeconds,
        CK_AlertDest,
        AlertCooldownDefaults.CK_MLMonitoring
    ];

    private static readonly JsonSerializerOptions SnapshotJsonOptions =
        new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PayloadJsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLFeatureRankShiftWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLFeatureRankShiftOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLFeatureRankShiftWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureRankShiftWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLFeatureRankShiftOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLFeatureRankShiftOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Compares feature-importance rank order between active ML champions and previous champions.",
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
                    _metrics?.MLFeatureRankShiftTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

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
                        _metrics?.MLFeatureRankShiftCycleDurationMs.Record(elapsedMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, skipped={Skipped}, rankShifts={RankShifts}, alertsUpserted={AlertsUpserted}, alertsResolved={AlertsResolved}.",
                                WorkerName,
                                result.CandidateModelCount,
                                result.EvaluatedModelCount,
                                result.SkippedModelCount,
                                result.RankShiftCount,
                                result.AlertsUpserted,
                                result.AlertsResolved);
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
                            new KeyValuePair<string, object?>("reason", "ml_feature_rank_shift_cycle"));
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

    internal async Task<FeatureRankShiftCycleResult> RunCycleAsync(CancellationToken ct)
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
            return FeatureRankShiftCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLFeatureRankShiftLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate rank-shift alerts are possible in multi-instance deployments.",
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
                _metrics?.MLFeatureRankShiftLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return FeatureRankShiftCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLFeatureRankShiftLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunRankShiftAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task<FeatureRankShiftCycleResult> RunRankShiftAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
            return FeatureRankShiftCycleResult.Skipped(config, "disabled");

        return await RunRankShiftAsync(readCtx, writeCtx, config, ct);
    }

    private async Task<FeatureRankShiftCycleResult> RunRankShiftAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureRankShiftConfig config,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var champions = await LoadActiveChampionsAsync(readCtx, config.MaxModelsPerCycle, ct);
        if (champions.Truncated)
            RecordCycleSkipped("model_limit");

        var activeDedupKeys = champions.Items
            .Select(model => BuildDeduplicationKey(model.Symbol, model.Timeframe))
            .ToHashSet(StringComparer.Ordinal);

        var configSpecs = new List<EngineConfigUpsertSpec>();
        int evaluated = 0;
        int skipped = 0;
        int rankShifts = 0;
        int alertsUpserted = 0;
        int alertsResolved = 0;
        int invalidSnapshots = 0;

        foreach (var champion in champions.Items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await EvaluateChampionAsync(
                    readCtx,
                    writeCtx,
                    config,
                    champion,
                    nowUtc,
                    configSpecs,
                    ct);

                if (result.Evaluated)
                    evaluated++;
                else
                    skipped++;

                if (result.RankShiftDetected)
                    rankShifts++;
                if (result.AlertUpserted)
                    alertsUpserted++;
                if (result.AlertResolved)
                    alertsResolved++;
                invalidSnapshots += result.InvalidSnapshotCount;

                if (result.Evaluated)
                {
                    _metrics?.MLFeatureRankShiftRankCorrelation.Record(
                        result.Correlation,
                        Tag("symbol", champion.Symbol),
                        Tag("timeframe", champion.Timeframe.ToString()));
                    _metrics?.MLFeatureRankShiftUnionFeatures.Record(
                        result.UnionFeatureCount,
                        Tag("symbol", champion.Symbol),
                        Tag("timeframe", champion.Timeframe.ToString()));
                }
                else
                {
                    _metrics?.MLFeatureRankShiftModelsSkipped.Add(
                        1,
                        Tag("reason", result.State),
                        Tag("symbol", champion.Symbol),
                        Tag("timeframe", champion.Timeframe.ToString()));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                _metrics?.MLFeatureRankShiftModelsSkipped.Add(
                    1,
                    Tag("reason", "model_error"),
                    Tag("symbol", champion.Symbol),
                    Tag("timeframe", champion.Timeframe.ToString()));
                _logger.LogWarning(
                    ex,
                    "{Worker}: rank-shift check failed for champion {ModelId} ({Symbol}/{Timeframe}); continuing.",
                    WorkerName,
                    champion.Id,
                    champion.Symbol,
                    champion.Timeframe);
            }
        }

        if (champions.Truncated)
        {
            _logger.LogDebug(
                "{Worker}: skipped inactive rank-shift alert cleanup because active champion scan was truncated at {MaxModels}.",
                WorkerName,
                config.MaxModelsPerCycle);
        }
        else
        {
            alertsResolved += await ResolveInactiveChampionAlertsAsync(writeCtx, activeDedupKeys, nowUtc, ct);
        }

        if (configSpecs.Count > 0)
            await EngineConfigUpsert.BatchUpsertAsync(writeCtx, configSpecs, ct);

        await writeCtx.SaveChangesAsync(ct);

        _metrics?.MLFeatureRankShiftModelsEvaluated.Add(evaluated);
        if (skipped > 0)
            _metrics?.MLFeatureRankShiftModelsSkipped.Add(skipped, Tag("reason", "cycle_total"));
        if (rankShifts > 0)
            _metrics?.MLFeatureRankShiftShiftsDetected.Add(rankShifts);
        if (invalidSnapshots > 0)
            _metrics?.MLFeatureRankShiftInvalidSnapshots.Add(invalidSnapshots);
        RecordAlertTransitions(alertsUpserted, alertsResolved);

        return new FeatureRankShiftCycleResult(
            Config: config,
            CandidateModelCount: champions.Items.Count,
            EvaluatedModelCount: evaluated,
            SkippedModelCount: skipped,
            RankShiftCount: rankShifts,
            AlertsUpserted: alertsUpserted,
            AlertsResolved: alertsResolved,
            InvalidSnapshotCount: invalidSnapshots,
            ConfigRowsWritten: configSpecs.Count,
            SkippedReason: null);
    }

    private static async Task<(List<ModelProjection> Items, bool Truncated)> LoadActiveChampionsAsync(
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
                        && m.RegimeScope == null
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
            .Select(m => new ModelProjection(m.Id, m.Symbol, m.Timeframe, m.TrainedAt, m.ModelBytes!))
            .ToListAsync(ct);

        var truncated = rows.Count > maxModels;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);

        return (rows, truncated);
    }

    private async Task<FeatureRankShiftModelResult> EvaluateChampionAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureRankShiftConfig config,
        ModelProjection champion,
        DateTime nowUtc,
        List<EngineConfigUpsertSpec> configSpecs,
        CancellationToken ct)
    {
        var dedupKey = BuildDeduplicationKey(champion.Symbol, champion.Timeframe);
        var championImportance = TryExtractImportance(champion.ModelBytes, champion.Id, champion.Symbol, champion.Timeframe);
        if (championImportance is null)
        {
            AddStateConfig(configSpecs, champion, predecessor: null, FeatureRankShiftAnalysis.Skipped("invalid_champion_snapshot"), 1, nowUtc);
            return FeatureRankShiftModelResult.Skipped("invalid_champion_snapshot", invalidSnapshotCount: 1);
        }

        var since = nowUtc.AddDays(-config.LookbackDays);
        var predecessor = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.Symbol == champion.Symbol
                        && m.Timeframe == champion.Timeframe
                        && m.RegimeScope == null
                        && m.Status == MLModelStatus.Superseded
                        && !m.IsDeleted
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer
                        && m.ModelBytes != null
                        && m.Id != champion.Id
                        && m.TrainedAt < champion.TrainedAt
                        && m.TrainedAt >= since)
            .OrderByDescending(m => m.TrainedAt)
            .ThenByDescending(m => m.Id)
            .Select(m => new ModelProjection(m.Id, m.Symbol, m.Timeframe, m.TrainedAt, m.ModelBytes!))
            .FirstOrDefaultAsync(ct);

        if (predecessor is null)
        {
            AddStateConfig(configSpecs, champion, predecessor: null, FeatureRankShiftAnalysis.Skipped("no_predecessor"), 0, nowUtc);
            var resolved = await ResolveAlertAsync(writeCtx, champion.Symbol, dedupKey, nowUtc, ct);
            return FeatureRankShiftModelResult.Skipped("no_predecessor", alertResolved: resolved);
        }

        var predecessorImportance = TryExtractImportance(predecessor.ModelBytes, predecessor.Id, predecessor.Symbol, predecessor.Timeframe);
        if (predecessorImportance is null)
        {
            AddStateConfig(configSpecs, champion, predecessor, FeatureRankShiftAnalysis.Skipped("invalid_predecessor_snapshot"), 1, nowUtc);
            return FeatureRankShiftModelResult.Skipped("invalid_predecessor_snapshot", invalidSnapshotCount: 1);
        }

        var invalidValueCount = championImportance.InvalidValueCount + predecessorImportance.InvalidValueCount;
        var analysis = AnalyzeRankShift(config, championImportance.Importance, predecessorImportance.Importance);
        AddStateConfig(configSpecs, champion, predecessor, analysis, invalidValueCount, nowUtc);

        if (!analysis.Evaluated)
            return FeatureRankShiftModelResult.Skipped(analysis.State, invalidSnapshotCount: invalidValueCount);

        bool alertUpserted;
        bool alertResolved;
        if (analysis.Correlation < config.RankCorrelationThreshold)
        {
            await UpsertAlertAsync(
                writeCtx,
                config,
                champion,
                predecessor,
                dedupKey,
                analysis,
                nowUtc,
                ct);
            alertUpserted = true;
            alertResolved = false;

            _logger.LogWarning(
                "{Worker}: {Symbol}/{Timeframe} rank shift detected; champion={ChampionId}, predecessor={PredecessorId}, spearman={Correlation:F3}, threshold={Threshold:F3}.",
                WorkerName,
                champion.Symbol,
                champion.Timeframe,
                champion.Id,
                predecessor.Id,
                analysis.Correlation,
                config.RankCorrelationThreshold);
        }
        else
        {
            alertUpserted = false;
            alertResolved = await ResolveAlertAsync(writeCtx, champion.Symbol, dedupKey, nowUtc, ct);
            _logger.LogDebug(
                "{Worker}: {Symbol}/{Timeframe} rank order stable; champion={ChampionId}, predecessor={PredecessorId}, spearman={Correlation:F3}.",
                WorkerName,
                champion.Symbol,
                champion.Timeframe,
                champion.Id,
                predecessor.Id,
                analysis.Correlation);
        }

        return new FeatureRankShiftModelResult(
            Evaluated: true,
            State: analysis.State,
            Correlation: analysis.Correlation,
            UnionFeatureCount: analysis.UnionFeatureCount,
            RankShiftDetected: alertUpserted,
            AlertUpserted: alertUpserted,
            AlertResolved: alertResolved,
            InvalidSnapshotCount: invalidValueCount);
    }

    private ModelImportance? TryExtractImportance(
        byte[] modelBytes,
        long modelId,
        string symbol,
        Timeframe timeframe)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(modelBytes, SnapshotJsonOptions);
            if (snapshot is null)
                return null;

            var extraction = ModelSnapshotFeatureImportanceExtractor.Extract(snapshot);
            if (extraction.Importance.Count == 0)
            {
                _logger.LogDebug(
                    "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) has no extractable feature importance.",
                    WorkerName,
                    modelId,
                    symbol,
                    timeframe);
                return null;
            }

            return new ModelImportance(extraction.Importance, extraction.InvalidValueCount);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) has an unreadable model snapshot.",
                WorkerName,
                modelId,
                symbol,
                timeframe);
            return null;
        }
    }

    internal static FeatureRankShiftAnalysis AnalyzeRankShift(
        FeatureRankShiftConfig config,
        IReadOnlyDictionary<string, double> championImportance,
        IReadOnlyDictionary<string, double> predecessorImportance)
    {
        var championTop = TopFeatureNames(championImportance, config.TopFeatures);
        var predecessorTop = TopFeatureNames(predecessorImportance, config.TopFeatures);
        var union = championTop
            .Concat(predecessorTop)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(feature => feature, StringComparer.Ordinal)
            .ToArray();

        if (union.Length < config.MinUnionFeatures)
            return FeatureRankShiftAnalysis.Skipped("insufficient_union_features", union.Length);

        var championScores = union
            .Select(feature => championImportance.TryGetValue(feature, out var value) ? value : 0.0)
            .ToArray();
        var predecessorScores = union
            .Select(feature => predecessorImportance.TryGetValue(feature, out var value) ? value : 0.0)
            .ToArray();

        var championRanks = RankDescending(championScores);
        var predecessorRanks = RankDescending(predecessorScores);
        var correlation = SpearmanRankFromRanks(championRanks, predecessorRanks);

        var diverging = union
            .Select((feature, index) =>
            {
                var importanceDelta = championScores[index] - predecessorScores[index];
                return new DivergingFeature(
                    feature,
                    championScores[index],
                    predecessorScores[index],
                    championRanks[index],
                    predecessorRanks[index],
                    Math.Abs(championRanks[index] - predecessorRanks[index]),
                    importanceDelta);
            })
            .OrderByDescending(feature => feature.RankDelta)
            .ThenByDescending(feature => Math.Abs(feature.ImportanceDelta))
            .ThenBy(feature => feature.Name, StringComparer.Ordinal)
            .ToArray();

        return new FeatureRankShiftAnalysis(
            Evaluated: true,
            State: correlation < config.RankCorrelationThreshold ? "rank_shift" : "healthy",
            Correlation: correlation,
            UnionFeatureCount: union.Length,
            DivergingFeatures: diverging);
    }

    private static IReadOnlyList<string> TopFeatureNames(
        IReadOnlyDictionary<string, double> importance,
        int topN)
        => importance
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topN)
            .Select(kv => kv.Key)
            .ToArray();

    internal static double SpearmanRank(double[] x, double[] y)
    {
        if (x.Length != y.Length)
            throw new ArgumentException("Spearman arrays must have equal length.", nameof(y));

        if (x.Length < 2)
            return 1.0;

        return SpearmanRankFromRanks(RankDescending(x), RankDescending(y));
    }

    private static double SpearmanRankFromRanks(IReadOnlyList<double> xRanks, IReadOnlyList<double> yRanks)
    {
        var n = xRanks.Count;
        if (n != yRanks.Count)
            throw new ArgumentException("Spearman rank arrays must have equal length.", nameof(yRanks));

        if (n < 2)
            return 1.0;

        var xMean = xRanks.Average();
        var yMean = yRanks.Average();
        double covariance = 0.0;
        double xVariance = 0.0;
        double yVariance = 0.0;

        for (var i = 0; i < n; i++)
        {
            var xCentered = xRanks[i] - xMean;
            var yCentered = yRanks[i] - yMean;
            covariance += xCentered * yCentered;
            xVariance += xCentered * xCentered;
            yVariance += yCentered * yCentered;
        }

        var denominator = Math.Sqrt(xVariance * yVariance);
        if (denominator <= 1e-12)
            return RanksEquivalent(xRanks, yRanks) ? 1.0 : 0.0;

        return Math.Clamp(covariance / denominator, -1.0, 1.0);
    }

    private static double[] RankDescending(IReadOnlyList<double> values)
    {
        var ordered = values
            .Select((value, index) => new RankedValue(index, value))
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Index)
            .ToArray();

        var ranks = new double[values.Count];
        var start = 0;
        while (start < ordered.Length)
        {
            var end = start + 1;
            while (end < ordered.Length && AlmostEqual(ordered[end].Value, ordered[start].Value))
                end++;

            var averageRank = ((start + 1) + end) / 2.0;
            for (var i = start; i < end; i++)
                ranks[ordered[i].Index] = averageRank;

            start = end;
        }

        return ranks;
    }

    private static bool AlmostEqual(double left, double right)
        => Math.Abs(left - right) <= 1e-12;

    private static bool RanksEquivalent(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        for (var i = 0; i < left.Count; i++)
        {
            if (!AlmostEqual(left[i], right[i]))
                return false;
        }

        return true;
    }

    private static void AddStateConfig(
        List<EngineConfigUpsertSpec> specs,
        ModelProjection champion,
        ModelProjection? predecessor,
        FeatureRankShiftAnalysis analysis,
        int invalidSnapshotCount,
        DateTime checkedAtUtc)
    {
        var prefix = BuildDeduplicationKey(champion.Symbol, champion.Timeframe);
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:EvaluationState",
            analysis.State,
            ConfigDataType.String,
            "Current feature-rank-shift evaluation state for this symbol/timeframe."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:SpearmanCorrelation",
            FormatNullableDouble(analysis.Evaluated ? analysis.Correlation : null),
            ConfigDataType.Decimal,
            "Spearman rank correlation between the active champion and previous champion."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:UnionFeatureCount",
            analysis.UnionFeatureCount.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Number of top-ranked feature names compared."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:ChampionModelId",
            champion.Id.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Active champion model ID used by the rank-shift check."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:PredecessorModelId",
            predecessor?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ConfigDataType.String,
            "Superseded predecessor model ID used by the rank-shift check."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:InvalidSnapshotCount",
            invalidSnapshotCount.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Number of unreadable snapshots or invalid importance values encountered."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:DivergingFeaturesJson",
            JsonSerializer.Serialize(
                analysis.DivergingFeatures.Select(feature => new
                {
                    feature.Name,
                    feature.ChampionImportance,
                    feature.PredecessorImportance,
                    feature.ChampionRank,
                    feature.PredecessorRank,
                    feature.RankDelta,
                }),
                PayloadJsonOptions),
            ConfigDataType.Json,
            "JSON list of features with the largest rank movement."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:LastCheckedAt",
            checkedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ConfigDataType.String,
            "UTC timestamp when feature-rank-shift was last evaluated."));
    }

    private async Task UpsertAlertAsync(
        DbContext writeCtx,
        FeatureRankShiftConfig config,
        ModelProjection champion,
        ModelProjection predecessor,
        string dedupKey,
        FeatureRankShiftAnalysis analysis,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await writeCtx.Set<Alert>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == dedupKey && !a.IsDeleted, ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = dedupKey,
            };
            writeCtx.Set<Alert>().Add(alert);
        }

        var payload = JsonSerializer.Serialize(new
        {
            reason = AlertReason,
            symbol = champion.Symbol,
            timeframe = champion.Timeframe.ToString(),
            alertDestination = config.AlertDestination,
            championModelId = champion.Id,
            predecessorModelId = predecessor.Id,
            spearmanCorrelation = analysis.Correlation,
            threshold = config.RankCorrelationThreshold,
            unionFeatureCount = analysis.UnionFeatureCount,
            topDivergingFeatures = analysis.DivergingFeatures
                .Take(config.MaxDivergingFeaturesInAlert)
                .Select(feature => new
                {
                    feature.Name,
                    feature.ChampionImportance,
                    feature.PredecessorImportance,
                    feature.ChampionRank,
                    feature.PredecessorRank,
                    feature.RankDelta,
                }),
            observedAtUtc = nowUtc,
        }, PayloadJsonOptions);

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = champion.Symbol;
        alert.ConditionJson = Truncate(payload, AlertConditionMaxLength);
        alert.Severity = analysis.Correlation < config.RankCorrelationThreshold / 2.0
            ? AlertSeverity.High
            : AlertSeverity.Medium;
        alert.CooldownSeconds = config.AlertCooldownSeconds;
        alert.IsActive = true;
        alert.IsDeleted = false;
        alert.AutoResolvedAt = null;
    }

    private static async Task<bool> ResolveAlertAsync(
        DbContext writeCtx,
        string symbol,
        string dedupKey,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alerts = await writeCtx.Set<Alert>()
            .Where(a => a.IsActive
                        && !a.IsDeleted
                        && a.AlertType == AlertType.MLModelDegraded
                        && a.Symbol == symbol
                        && (a.DeduplicationKey == dedupKey
                            || (a.DeduplicationKey == null
                                && a.ConditionJson.Contains(AlertReason))))
            .ToListAsync(ct);

        foreach (var alert in alerts)
        {
            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }

        return alerts.Count > 0;
    }

    private static async Task<int> ResolveInactiveChampionAlertsAsync(
        DbContext writeCtx,
        IReadOnlySet<string> activeDedupKeys,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeAlerts = await writeCtx.Set<Alert>()
            .Where(a => a.IsActive
                        && !a.IsDeleted
                        && a.DeduplicationKey != null
                        && a.DeduplicationKey.StartsWith(AlertDeduplicationPrefix))
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in activeAlerts)
        {
            if (alert.DeduplicationKey is null || activeDedupKeys.Contains(alert.DeduplicationKey))
                continue;

            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
            resolved++;
        }

        return resolved;
    }

    internal static async Task<FeatureRankShiftConfig> LoadConfigAsync(
        DbContext ctx,
        MLFeatureRankShiftOptions options,
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

        var topFeatures = NormalizeTopFeatures(GetConfig(values, CK_TopN, options.TopFeatures));
        var pollSeconds = NormalizePollSeconds(GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));
        var alertCooldownDefault = GetConfig(
            values,
            AlertCooldownDefaults.CK_MLMonitoring,
            options.AlertCooldownSeconds);

        return new FeatureRankShiftConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySeconds, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollSeconds: pollSeconds,
            TopFeatures: topFeatures,
            MinUnionFeatures: NormalizeMinUnionFeatures(
                GetConfig(values, CK_MinUnionFeatures, options.MinUnionFeatures),
                topFeatures),
            RankCorrelationThreshold: NormalizeRankCorrelationThreshold(
                GetConfig(values, CK_Threshold, options.RankCorrelationThreshold)),
            LookbackDays: NormalizeLookbackDays(GetConfig(values, CK_Lookback, options.LookbackDays)),
            MaxModelsPerCycle: NormalizeMaxModelsPerCycle(
                GetConfig(values, CK_MaxModelsPerCycle, options.MaxModelsPerCycle)),
            MaxDivergingFeaturesInAlert: NormalizeMaxDivergingFeaturesInAlert(
                GetConfig(values, CK_MaxDivergingFeaturesInAlert, options.MaxDivergingFeaturesInAlert)),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSeconds, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSeconds, options.DbCommandTimeoutSeconds)),
            AlertCooldownSeconds: NormalizeAlertCooldownSeconds(
                GetConfig(values, CK_AlertCooldownSeconds, alertCooldownDefault)),
            AlertDestination: NormalizeDestination(GetConfig(values, CK_AlertDest, options.AlertDestination)));
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

    internal static int NormalizeTopFeatures(int value)
        => value is >= 3 and <= 1_000 ? value : DefaultTopFeatures;

    internal static int NormalizeMinUnionFeatures(int value, int topFeatures)
    {
        var maxUnion = Math.Max(DefaultMinUnionFeatures, topFeatures * 2);
        return value >= DefaultMinUnionFeatures && value <= maxUnion
            ? value
            : Math.Min(DefaultMinUnionFeatures, maxUnion);
    }

    internal static double NormalizeRankCorrelationThreshold(double value)
        => double.IsFinite(value) && value is >= -1.0 and <= 1.0 ? value : DefaultRankCorrelationThreshold;

    internal static int NormalizeLookbackDays(int value)
        => value is >= 1 and <= 3_650 ? value : DefaultLookbackDays;

    internal static int NormalizeMaxModelsPerCycle(int value)
        => value is >= 1 and <= 100_000 ? value : DefaultMaxModelsPerCycle;

    internal static int NormalizeMaxDivergingFeaturesInAlert(int value)
        => value is >= 1 and <= 100 ? value : DefaultMaxDivergingFeaturesInAlert;

    internal static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : DefaultLockTimeoutSeconds;

    internal static int NormalizeDbCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : DefaultDbCommandTimeoutSeconds;

    internal static int NormalizeAlertCooldownSeconds(int value)
        => value is >= 1 and <= 604_800 ? value : AlertCooldownDefaults.Default_MLMonitoring;

    internal static string NormalizeDestination(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return DefaultAlertDestination;

        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

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

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLFeatureRankShiftCyclesSkipped.Add(1, Tag("reason", reason));

    private void RecordAlertTransitions(int upserted, int resolved)
    {
        if (upserted > 0)
            _metrics?.MLFeatureRankShiftAlertTransitions.Add(upserted, Tag("transition", "upserted"));
        if (resolved > 0)
            _metrics?.MLFeatureRankShiftAlertTransitions.Add(resolved, Tag("transition", "resolved"));
    }

    private static string BuildDeduplicationKey(string symbol, Timeframe timeframe)
        => $"{AlertDeduplicationPrefix}{symbol}:{timeframe}";

    private static string FormatNullableDouble(double? value)
        => value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("G17", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

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

    internal sealed record FeatureRankShiftConfig(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollSeconds,
        int TopFeatures,
        int MinUnionFeatures,
        double RankCorrelationThreshold,
        int LookbackDays,
        int MaxModelsPerCycle,
        int MaxDivergingFeaturesInAlert,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds,
        int AlertCooldownSeconds,
        string AlertDestination);

    internal sealed record FeatureRankShiftCycleResult(
        FeatureRankShiftConfig Config,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int SkippedModelCount,
        int RankShiftCount,
        int AlertsUpserted,
        int AlertsResolved,
        int InvalidSnapshotCount,
        int ConfigRowsWritten,
        string? SkippedReason)
    {
        public static FeatureRankShiftCycleResult Skipped(FeatureRankShiftConfig config, string reason)
            => new(config, 0, 0, 0, 0, 0, 0, 0, 0, reason);
    }

    internal sealed record FeatureRankShiftAnalysis(
        bool Evaluated,
        string State,
        double Correlation,
        int UnionFeatureCount,
        IReadOnlyList<DivergingFeature> DivergingFeatures)
    {
        public static FeatureRankShiftAnalysis Skipped(string reason, int unionFeatureCount = 0)
            => new(false, reason, 0.0, unionFeatureCount, []);
    }

    private sealed record ModelProjection(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        DateTime TrainedAt,
        byte[] ModelBytes);

    private sealed record ModelImportance(
        IReadOnlyDictionary<string, double> Importance,
        int InvalidValueCount);

    private sealed record FeatureRankShiftModelResult(
        bool Evaluated,
        string State,
        double Correlation,
        int UnionFeatureCount,
        bool RankShiftDetected,
        bool AlertUpserted,
        bool AlertResolved,
        int InvalidSnapshotCount)
    {
        public static FeatureRankShiftModelResult Skipped(
            string reason,
            bool alertResolved = false,
            int invalidSnapshotCount = 0)
            => new(
                Evaluated: false,
                State: reason,
                Correlation: 0.0,
                UnionFeatureCount: 0,
                RankShiftDetected: false,
                AlertUpserted: false,
                AlertResolved: alertResolved,
                InvalidSnapshotCount: invalidSnapshotCount);
    }

    internal sealed record DivergingFeature(
        string Name,
        double ChampionImportance,
        double PredecessorImportance,
        double ChampionRank,
        double PredecessorRank,
        double RankDelta,
        double ImportanceDelta);

    private readonly record struct RankedValue(int Index, double Value);
}
