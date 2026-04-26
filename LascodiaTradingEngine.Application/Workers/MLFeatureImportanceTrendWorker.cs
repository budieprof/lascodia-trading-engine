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
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects features whose model importance decays monotonically across recent generations.
/// </summary>
public sealed class MLFeatureImportanceTrendWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLFeatureImportanceTrendWorker);

    private const string DistributedLockKey = "workers:ml-feature-importance-trend:cycle";
    private const string AlertDeduplicationPrefix = "MLFeatureImpTrend:";

    private const string CK_Enabled = "MLFeatureImpTrend:Enabled";
    private const string CK_InitialDelaySeconds = "MLFeatureImpTrend:InitialDelaySeconds";
    private const string CK_PollSecs = "MLFeatureImpTrend:PollIntervalSeconds";
    private const string CK_Generations = "MLFeatureImpTrend:GenerationsToCheck";
    private const string CK_MinGenerations = "MLFeatureImpTrend:MinGenerations";
    private const string CK_DecayThreshold = "MLFeatureImpTrend:ImportanceDecayThreshold";
    private const string CK_MonotonicTolerance = "MLFeatureImpTrend:MonotonicTolerance";
    private const string CK_MinRelativeDrop = "MLFeatureImpTrend:MinRelativeDrop";
    private const string CK_MaxPairsPerCycle = "MLFeatureImpTrend:MaxPairsPerCycle";
    private const string CK_MaxFeaturesInAlert = "MLFeatureImpTrend:MaxFeaturesInAlert";
    private const string CK_LockTimeoutSeconds = "MLFeatureImpTrend:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLFeatureImpTrend:DbCommandTimeoutSeconds";
    private const string CK_AlertDestination = "MLFeatureImpTrend:AlertDestination";

    private const int DefaultPollSeconds = 86_400;
    private const int DefaultGenerations = 4;
    private const int DefaultMinGenerations = 3;
    private const int DefaultMaxPairsPerCycle = 1_000;
    private const int DefaultMaxFeaturesInAlert = 20;
    private const int DefaultLockTimeoutSeconds = 0;
    private const int DefaultDbCommandTimeoutSeconds = 30;
    private const double DefaultDecayThreshold = 0.005;
    private const double DefaultMonotonicTolerance = 0.0;
    private const double DefaultMinRelativeDrop = 0.50;
    private const string DefaultAlertDestination = "ml-ops";
    private const int AlertConditionMaxLength = 1_000;

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySeconds,
        CK_PollSecs,
        CK_Generations,
        CK_MinGenerations,
        CK_DecayThreshold,
        CK_MonotonicTolerance,
        CK_MinRelativeDrop,
        CK_MaxPairsPerCycle,
        CK_MaxFeaturesInAlert,
        CK_LockTimeoutSeconds,
        CK_DbCommandTimeoutSeconds,
        CK_AlertDestination,
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
    private readonly ILogger<MLFeatureImportanceTrendWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLFeatureImportanceTrendOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLFeatureImportanceTrendWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLFeatureImportanceTrendWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLFeatureImportanceTrendOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLFeatureImportanceTrendOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Monitors monotone decay in named feature-importance trajectories across ML model generations.",
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
                    _metrics?.MLFeatureImportanceTrendTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

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
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.DyingFeatureCount);
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            elapsedMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLFeatureImportanceTrendCycleDurationMs.Record(elapsedMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "{Worker}: pairs={Pairs}, evaluated={Evaluated}, skipped={Skipped}, dyingFeatures={DyingFeatures}, alertsUpserted={AlertsUpserted}, alertsResolved={AlertsResolved}.",
                                WorkerName,
                                result.CandidatePairCount,
                                result.EvaluatedPairCount,
                                result.SkippedPairCount,
                                result.DyingFeatureCount,
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
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_feature_importance_trend_cycle"));
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

    internal async Task<FeatureImportanceTrendCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
        {
            RecordCycleSkipped("disabled");
            return FeatureImportanceTrendCycleResult.Skipped(config, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLFeatureImportanceTrendLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate trend cycles are possible in multi-instance deployments.",
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
                _metrics?.MLFeatureImportanceTrendLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return FeatureImportanceTrendCycleResult.Skipped(config, "lock_busy");
            }

            _metrics?.MLFeatureImportanceTrendLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunTrendAsync(readCtx, writeCtx, config, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task<FeatureImportanceTrendCycleResult> RunTrendAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var config = await LoadConfigAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, config.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, config.DbCommandTimeoutSeconds);

        if (!config.Enabled)
            return FeatureImportanceTrendCycleResult.Skipped(config, "disabled");

        return await RunTrendAsync(readCtx, writeCtx, config, ct);
    }

    private async Task<FeatureImportanceTrendCycleResult> RunTrendAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureImportanceTrendConfig config,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var configSpecs = new List<EngineConfigUpsertSpec>();

        var pairs = await LoadActivePairsAsync(readCtx, config.MaxPairsPerCycle, ct);
        if (pairs.Truncated)
            RecordCycleSkipped("pair_limit");

        var activeDedupKeys = pairs.Items
            .Select(pair => BuildDeduplicationKey(pair.Symbol, pair.Timeframe))
            .ToHashSet(StringComparer.Ordinal);

        int evaluated = 0;
        int skipped = 0;
        int dyingFeatures = 0;
        int alertsUpserted = 0;
        int alertsResolved = 0;
        int invalidSnapshots = 0;

        foreach (var pair in pairs.Items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var pairResult = await EvaluatePairAsync(
                    readCtx,
                    writeCtx,
                    config,
                    pair.Symbol,
                    pair.Timeframe,
                    nowUtc,
                    configSpecs,
                    ct);

                if (pairResult.Evaluated)
                    evaluated++;
                else
                    skipped++;

                dyingFeatures += pairResult.DyingFeatureCount;
                invalidSnapshots += pairResult.InvalidSnapshotCount;
                if (pairResult.AlertUpserted)
                    alertsUpserted++;
                if (pairResult.AlertResolved)
                    alertsResolved++;

                _metrics?.MLFeatureImportanceTrendGenerationsChecked.Record(
                    pairResult.ValidGenerationCount,
                    Tag("symbol", pair.Symbol),
                    Tag("timeframe", pair.Timeframe.ToString()),
                    Tag("state", pairResult.EvaluationState));
            }
            catch (Exception ex)
            {
                skipped++;
                _metrics?.MLFeatureImportanceTrendPairsSkipped.Add(
                    1,
                    Tag("reason", "pair_error"),
                    Tag("symbol", pair.Symbol),
                    Tag("timeframe", pair.Timeframe.ToString()));
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to evaluate feature-importance trend for {Symbol}/{Timeframe}.",
                    WorkerName,
                    pair.Symbol,
                    pair.Timeframe);
            }
        }

        if (pairs.Truncated)
        {
            _logger.LogDebug(
                "{Worker}: skipped inactive feature-importance alert cleanup because the active pair set was truncated at {MaxPairs}.",
                WorkerName,
                config.MaxPairsPerCycle);
        }
        else
        {
            alertsResolved += await ResolveInactivePairAlertsAsync(writeCtx, activeDedupKeys, nowUtc, ct);
        }

        if (configSpecs.Count > 0)
            await EngineConfigUpsert.BatchUpsertAsync(writeCtx, configSpecs, ct);

        await writeCtx.SaveChangesAsync(ct);

        _metrics?.MLFeatureImportanceTrendPairsEvaluated.Add(evaluated);
        if (skipped > 0)
            _metrics?.MLFeatureImportanceTrendPairsSkipped.Add(skipped, Tag("reason", "cycle_total"));
        if (dyingFeatures > 0)
            _metrics?.MLFeatureImportanceTrendDyingFeatures.Add(dyingFeatures);
        if (invalidSnapshots > 0)
            _metrics?.MLFeatureImportanceTrendInvalidSnapshots.Add(invalidSnapshots);
        RecordAlertTransitions(alertsUpserted, alertsResolved);

        return new FeatureImportanceTrendCycleResult(
            Config: config,
            CandidatePairCount: pairs.Items.Count,
            EvaluatedPairCount: evaluated,
            SkippedPairCount: skipped,
            DyingFeatureCount: dyingFeatures,
            AlertsUpserted: alertsUpserted,
            AlertsResolved: alertsResolved,
            InvalidSnapshotCount: invalidSnapshots,
            ConfigRowsWritten: configSpecs.Count,
            SkippedReason: null);
    }

    private static async Task<(List<ActivePair> Items, bool Truncated)> LoadActivePairsAsync(
        DbContext db,
        int maxPairs,
        CancellationToken ct)
    {
        var rows = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && m.RegimeScope == null
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer)
            .Select(m => new { m.Symbol, m.Timeframe })
            .Distinct()
            .OrderBy(pair => pair.Symbol)
            .ThenBy(pair => pair.Timeframe)
            .Take(maxPairs + 1)
            .ToListAsync(ct);

        var truncated = rows.Count > maxPairs;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);

        return (rows.Select(row => new ActivePair(row.Symbol, row.Timeframe)).ToList(), truncated);
    }

    private async Task<PairTrendResult> EvaluatePairAsync(
        DbContext readCtx,
        DbContext writeCtx,
        FeatureImportanceTrendConfig config,
        string symbol,
        Timeframe timeframe,
        DateTime nowUtc,
        List<EngineConfigUpsertSpec> configSpecs,
        CancellationToken ct)
    {
        var generations = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.Symbol == symbol
                        && m.Timeframe == timeframe
                        && m.RegimeScope == null
                        && m.ModelBytes != null
                        && !m.IsDeleted
                        && (m.Status == MLModelStatus.Active
                            || m.Status == MLModelStatus.Superseded
                            || m.IsFallbackChampion))
            .OrderByDescending(m => m.TrainedAt)
            .Take(config.GenerationsToCheck)
            .Select(m => new ModelGeneration(m.Id, m.TrainedAt, m.ModelBytes!))
            .ToListAsync(ct);

        generations = generations.OrderBy(m => m.TrainedAt).ToList();

        var validGenerations = new List<GenerationImportance>(generations.Count);
        int invalidSnapshots = 0;
        foreach (var generation in generations)
        {
            var importance = TryExtractImportance(generation, symbol, timeframe, out var invalidValues);
            if (importance.Count == 0)
            {
                invalidSnapshots++;
                continue;
            }

            invalidSnapshots += invalidValues;
            validGenerations.Add(new GenerationImportance(
                generation.ModelId,
                generation.TrainedAt,
                importance));
        }

        PairTrendAnalysis analysis;
        if (validGenerations.Count < config.MinGenerations)
        {
            analysis = PairTrendAnalysis.Skipped(
                "insufficient_valid_generations",
                generations.Count,
                validGenerations.Count,
                invalidSnapshots);
        }
        else
        {
            analysis = AnalyzeFeatureTrajectories(config, validGenerations, invalidSnapshots);
        }

        AddPairConfig(configSpecs, symbol, timeframe, analysis, nowUtc);

        var dedupKey = BuildDeduplicationKey(symbol, timeframe);
        bool alertUpserted = false;
        bool alertResolved;
        if (analysis.DyingFeatures.Count > 0)
        {
            await UpsertAlertAsync(writeCtx, config, symbol, timeframe, dedupKey, analysis, nowUtc, ct);
            alertUpserted = true;
            alertResolved = false;

            _logger.LogWarning(
                "{Worker}: {Symbol}/{Timeframe} has {Count} dying feature(s): {Features}.",
                WorkerName,
                symbol,
                timeframe,
                analysis.DyingFeatures.Count,
                string.Join(", ", analysis.DyingFeatures
                    .Take(config.MaxFeaturesInAlert)
                    .Select(f => $"{f.Name}({f.LatestImportance:F4})")));
        }
        else
        {
            alertResolved = await ResolveAlertAsync(writeCtx, symbol, dedupKey, nowUtc, ct);
            _logger.LogDebug(
                "{Worker}: {Symbol}/{Timeframe} state={State}, generations={Generations}, commonFeatures={CommonFeatures}.",
                WorkerName,
                symbol,
                timeframe,
                analysis.EvaluationState,
                analysis.ValidGenerationCount,
                analysis.CommonFeatureCount);
        }

        return new PairTrendResult(
            Evaluated: analysis.Evaluated,
            EvaluationState: analysis.EvaluationState,
            ValidGenerationCount: analysis.ValidGenerationCount,
            DyingFeatureCount: analysis.DyingFeatures.Count,
            InvalidSnapshotCount: invalidSnapshots,
            AlertUpserted: alertUpserted,
            AlertResolved: alertResolved);
    }

    private IReadOnlyDictionary<string, double> TryExtractImportance(
        ModelGeneration generation,
        string symbol,
        Timeframe timeframe,
        out int invalidValueCount)
    {
        invalidValueCount = 0;
        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(generation.ModelBytes, SnapshotJsonOptions);
            if (snapshot is null)
                return new Dictionary<string, double>(StringComparer.Ordinal);

            var extraction = ModelSnapshotFeatureImportanceExtractor.Extract(snapshot);
            invalidValueCount = extraction.InvalidValueCount;
            return extraction.Importance;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) has an unreadable model snapshot.",
                WorkerName,
                generation.ModelId,
                symbol,
                timeframe);
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    private static PairTrendAnalysis AnalyzeFeatureTrajectories(
        FeatureImportanceTrendConfig config,
        IReadOnlyList<GenerationImportance> generations,
        int invalidSnapshotCount)
    {
        var commonFeatures = new HashSet<string>(generations[0].Importance.Keys, StringComparer.Ordinal);
        for (int i = 1; i < generations.Count; i++)
            commonFeatures.IntersectWith(generations[i].Importance.Keys);

        if (commonFeatures.Count == 0)
        {
            return PairTrendAnalysis.Skipped(
                "no_common_features",
                generations.Count,
                generations.Count,
                invalidSnapshotCount);
        }

        var dying = new List<DyingFeature>();
        foreach (var feature in commonFeatures)
        {
            var values = generations.Select(g => g.Importance[feature]).ToArray();
            if (!values.All(double.IsFinite))
                continue;

            double first = values[0];
            double latest = values[^1];
            if (first <= config.ImportanceDecayThreshold || latest > config.ImportanceDecayThreshold)
                continue;

            if (!IsMonotonicallyDecreasing(values, config.MonotonicTolerance))
                continue;

            double relativeDrop = first <= 0.0 ? 0.0 : (first - latest) / first;
            if (relativeDrop < config.MinRelativeDrop)
                continue;

            dying.Add(new DyingFeature(feature, first, latest, relativeDrop, values));
        }

        return new PairTrendAnalysis(
            Evaluated: true,
            EvaluationState: dying.Count > 0 ? "dying_features" : "healthy",
            GenerationCount: generations.Count,
            ValidGenerationCount: generations.Count,
            CommonFeatureCount: commonFeatures.Count,
            InvalidSnapshotCount: invalidSnapshotCount,
            ModelIds: generations.Select(g => g.ModelId).ToArray(),
            DyingFeatures: dying
                .OrderByDescending(f => f.RelativeDrop)
                .ThenBy(f => f.LatestImportance)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool IsMonotonicallyDecreasing(IReadOnlyList<double> values, double tolerance)
    {
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] >= values[i - 1] - tolerance)
                return false;
        }

        return true;
    }

    private static void AddPairConfig(
        List<EngineConfigUpsertSpec> specs,
        string symbol,
        Timeframe timeframe,
        PairTrendAnalysis analysis,
        DateTime checkedAtUtc)
    {
        var prefix = $"{AlertDeduplicationPrefix}{symbol}:{timeframe}";
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:EvaluationState",
            analysis.EvaluationState,
            ConfigDataType.String,
            "Current feature-importance trend evaluation state for this symbol/timeframe."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:DyingFeatureCount",
            analysis.DyingFeatures.Count.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Number of named features with monotone importance decay."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:CommonFeatureCount",
            analysis.CommonFeatureCount.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Number of feature names common to all valid generations."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:ValidGenerationCount",
            analysis.ValidGenerationCount.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Number of valid model generations used by the trend check."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:InvalidSnapshotCount",
            analysis.InvalidSnapshotCount.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Number of corrupt or unusable model snapshots skipped by the trend check."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:LastCheckedAt",
            checkedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ConfigDataType.String,
            "UTC timestamp when feature-importance trend was last evaluated."));
        specs.Add(new EngineConfigUpsertSpec(
            $"{prefix}:DyingFeaturesJson",
            JsonSerializer.Serialize(
                analysis.DyingFeatures.Select(f => new
                {
                    f.Name,
                    f.FirstImportance,
                    f.LatestImportance,
                    f.RelativeDrop,
                }),
                PayloadJsonOptions),
            ConfigDataType.Json,
            "JSON list of features whose importance is monotonically decaying."));
    }

    private async Task UpsertAlertAsync(
        DbContext writeCtx,
        FeatureImportanceTrendConfig config,
        string symbol,
        Timeframe timeframe,
        string dedupKey,
        PairTrendAnalysis analysis,
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
            reason = "feature_importance_monotone_decay",
            symbol,
            timeframe = timeframe.ToString(),
            alertDestination = config.AlertDestination,
            generationsChecked = analysis.ValidGenerationCount,
            commonFeatureCount = analysis.CommonFeatureCount,
            modelIds = analysis.ModelIds,
            thresholds = new
            {
                latestImportanceMax = config.ImportanceDecayThreshold,
                config.MinRelativeDrop,
                config.MonotonicTolerance,
            },
            dyingFeatures = analysis.DyingFeatures
                .Take(config.MaxFeaturesInAlert)
                .Select(f => new
                {
                    name = f.Name,
                    firstImportance = f.FirstImportance,
                    latestImportance = f.LatestImportance,
                    relativeDrop = f.RelativeDrop,
                }),
        }, PayloadJsonOptions);

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = symbol;
        alert.ConditionJson = Truncate(payload, AlertConditionMaxLength);
        alert.Severity = analysis.DyingFeatures.Count >= Math.Max(3, config.MaxFeaturesInAlert / 2)
            ? AlertSeverity.High
            : AlertSeverity.Medium;
        alert.CooldownSeconds = config.AlertCooldownSeconds;
        alert.IsActive = true;
        alert.IsDeleted = false;
        alert.AutoResolvedAt = null;

        await Task.CompletedTask;
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
                                && a.ConditionJson.Contains("feature_importance_monotone_decay"))))
            .ToListAsync(ct);

        foreach (var alert in alerts)
        {
            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }

        return alerts.Count > 0;
    }

    private async Task<int> ResolveInactivePairAlertsAsync(
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

    internal static async Task<FeatureImportanceTrendConfig> LoadConfigAsync(
        DbContext ctx,
        MLFeatureImportanceTrendOptions options,
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

        var generations = NormalizeGenerations(GetConfig(values, CK_Generations, options.GenerationsToCheck));
        var minGenerations = NormalizeMinGenerations(
            GetConfig(values, CK_MinGenerations, options.MinGenerations),
            generations);
        var pollSeconds = NormalizePollSeconds(GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));

        return new FeatureImportanceTrendConfig(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySeconds, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollSeconds: pollSeconds,
            GenerationsToCheck: generations,
            MinGenerations: minGenerations,
            ImportanceDecayThreshold: NormalizeDecayThreshold(
                GetConfig(values, CK_DecayThreshold, options.ImportanceDecayThreshold)),
            MonotonicTolerance: NormalizeMonotonicTolerance(
                GetConfig(values, CK_MonotonicTolerance, options.MonotonicTolerance)),
            MinRelativeDrop: NormalizeMinRelativeDrop(GetConfig(values, CK_MinRelativeDrop, options.MinRelativeDrop)),
            MaxPairsPerCycle: NormalizeMaxPairsPerCycle(GetConfig(values, CK_MaxPairsPerCycle, options.MaxPairsPerCycle)),
            MaxFeaturesInAlert: NormalizeMaxFeaturesInAlert(
                GetConfig(values, CK_MaxFeaturesInAlert, options.MaxFeaturesInAlert)),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSeconds, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSeconds, options.DbCommandTimeoutSeconds)),
            AlertDestination: NormalizeDestination(GetConfig(values, CK_AlertDestination, options.AlertDestination)),
            AlertCooldownSeconds: NormalizeAlertCooldownSeconds(
                GetConfig(values, AlertCooldownDefaults.CK_MLMonitoring, options.AlertCooldownSeconds)));
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
        => value is >= 0 and <= 86_400 ? value : 0;

    internal static int NormalizePollSeconds(int value)
        => value is >= 60 and <= 604_800 ? value : DefaultPollSeconds;

    internal static int NormalizeGenerations(int value)
        => value is >= 2 and <= 64 ? value : DefaultGenerations;

    internal static int NormalizeMinGenerations(int value, int generationsToCheck)
    {
        if (value is < 2 or > 64)
            return Math.Min(DefaultMinGenerations, generationsToCheck);

        return Math.Min(value, generationsToCheck);
    }

    internal static double NormalizeDecayThreshold(double value)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0 ? value : DefaultDecayThreshold;

    internal static double NormalizeMonotonicTolerance(double value)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0 ? value : DefaultMonotonicTolerance;

    internal static double NormalizeMinRelativeDrop(double value)
        => double.IsFinite(value) && value is >= 0.0 and <= 1.0 ? value : DefaultMinRelativeDrop;

    internal static int NormalizeMaxPairsPerCycle(int value)
        => value is >= 1 and <= 100_000 ? value : DefaultMaxPairsPerCycle;

    internal static int NormalizeMaxFeaturesInAlert(int value)
        => value is >= 1 and <= 100 ? value : DefaultMaxFeaturesInAlert;

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
        => _metrics?.MLFeatureImportanceTrendCyclesSkipped.Add(1, Tag("reason", reason));

    private void RecordAlertTransitions(int upserted, int resolved)
    {
        if (upserted > 0)
            _metrics?.MLFeatureImportanceTrendAlertTransitions.Add(upserted, Tag("transition", "upserted"));
        if (resolved > 0)
            _metrics?.MLFeatureImportanceTrendAlertTransitions.Add(resolved, Tag("transition", "resolved"));
    }

    private static string BuildDeduplicationKey(string symbol, Timeframe timeframe)
        => $"{AlertDeduplicationPrefix}{symbol}:{timeframe}";

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

    internal sealed record FeatureImportanceTrendConfig(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollSeconds,
        int GenerationsToCheck,
        int MinGenerations,
        double ImportanceDecayThreshold,
        double MonotonicTolerance,
        double MinRelativeDrop,
        int MaxPairsPerCycle,
        int MaxFeaturesInAlert,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds,
        string AlertDestination,
        int AlertCooldownSeconds);

    internal sealed record FeatureImportanceTrendCycleResult(
        FeatureImportanceTrendConfig Config,
        int CandidatePairCount,
        int EvaluatedPairCount,
        int SkippedPairCount,
        int DyingFeatureCount,
        int AlertsUpserted,
        int AlertsResolved,
        int InvalidSnapshotCount,
        int ConfigRowsWritten,
        string? SkippedReason)
    {
        public static FeatureImportanceTrendCycleResult Skipped(FeatureImportanceTrendConfig config, string reason)
            => new(config, 0, 0, 0, 0, 0, 0, 0, 0, reason);
    }

    private sealed record ActivePair(string Symbol, Timeframe Timeframe);

    private sealed record ModelGeneration(long ModelId, DateTime TrainedAt, byte[] ModelBytes);

    private sealed record GenerationImportance(
        long ModelId,
        DateTime TrainedAt,
        IReadOnlyDictionary<string, double> Importance);

    private sealed record PairTrendAnalysis(
        bool Evaluated,
        string EvaluationState,
        int GenerationCount,
        int ValidGenerationCount,
        int CommonFeatureCount,
        int InvalidSnapshotCount,
        long[] ModelIds,
        IReadOnlyList<DyingFeature> DyingFeatures)
    {
        public static PairTrendAnalysis Skipped(
            string reason,
            int generationCount,
            int validGenerationCount,
            int invalidSnapshotCount)
            => new(
                Evaluated: false,
                EvaluationState: reason,
                GenerationCount: generationCount,
                ValidGenerationCount: validGenerationCount,
                CommonFeatureCount: 0,
                InvalidSnapshotCount: invalidSnapshotCount,
                ModelIds: [],
                DyingFeatures: []);
    }

    private sealed record PairTrendResult(
        bool Evaluated,
        string EvaluationState,
        int ValidGenerationCount,
        int DyingFeatureCount,
        int InvalidSnapshotCount,
        bool AlertUpserted,
        bool AlertResolved);

    private sealed record DyingFeature(
        string Name,
        double FirstImportance,
        double LatestImportance,
        double RelativeDrop,
        double[] Values);
}
