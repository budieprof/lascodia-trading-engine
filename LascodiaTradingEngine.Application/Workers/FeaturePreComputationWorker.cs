using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Pre-computes and refreshes the base 33-element feature-store vectors for active
/// strategy pairs so live scoring can reuse a ready V1 feature block instead of
/// rebuilding it on the hot path.
///
/// <para>
/// The worker is schema-aware and catch-up aware: it refreshes the most recent
/// missing or stale bars per active pair, not just the newest candle, and it uses
/// the same point-in-time COT normalization semantics as ML training.
/// </para>
/// </summary>
public sealed class FeaturePreComputationWorker : BackgroundService
{
    internal const string WorkerName = nameof(FeaturePreComputationWorker);

    private const string CK_PollSecs = "FeaturePreComputation:PollIntervalSeconds";
    private const string CK_CatchUpBarsPerPair = "FeaturePreComputation:CatchUpBarsPerPair";
    private const string DistributedLockKey = "workers:feature-pre-computation:cycle";

    private const int DefaultPollIntervalSeconds = 60;
    private const int MinPollIntervalSeconds = 5;
    private const int MaxPollIntervalSeconds = 3600;

    private const int DefaultCatchUpBarsPerPair = 8;
    private const int MinCatchUpBarsPerPair = 1;
    private const int MaxCatchUpBarsPerPair = 256;

    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly string[] BaseFeatureNames = MLFeatureHelper.ResolveFeatureNames(MLFeatureHelper.FeatureCount);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FeaturePreComputationWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public FeaturePreComputationWorker(
        ILogger<FeaturePreComputationWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
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
            "Pre-computes schema-aware V1 feature-store vectors for recent active bars, catching up missed candles and refreshing stale schema rows with point-in-time COT semantics.",
            TimeSpan.FromSeconds(DefaultPollIntervalSeconds));

        var currentPollInterval = TimeSpan.FromSeconds(DefaultPollIntervalSeconds);

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
                    currentPollInterval = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.PendingVectorCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.FeaturePrecomputeCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else if (result.VectorCount > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: activePairs={Pairs}, evaluatedPairs={Evaluated}, vectorsWritten={Vectors}, lineageWrites={Lineages}, freshPairs={Fresh}, insufficientHistoryPairs={Insufficient}, errorPairs={Errors}.",
                            WorkerName,
                            result.ActivePairCount,
                            result.EvaluatedPairCount,
                            result.VectorCount,
                            result.LineageWriteCount,
                            result.FreshPairCount,
                            result.InsufficientHistoryPairCount,
                            result.ErrorPairCount);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "{Worker}: activePairs={Pairs}, evaluatedPairs={Evaluated}, all recent feature vectors already current.",
                            WorkerName,
                            result.ActivePairCount,
                            result.EvaluatedPairCount);
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
                        new KeyValuePair<string, object?>("reason", "feature_precompute_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(
                        CalculateDelay(currentPollInterval, _consecutiveFailures),
                        _timeProvider,
                        stoppingToken);
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

    internal async Task<FeaturePreComputationCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var featureStore = scope.ServiceProvider.GetRequiredService<IFeatureStore>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (_distributedLock is null)
        {
            _metrics?.FeaturePrecomputeLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate pre-computation cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
            {
                _metrics?.FeaturePrecomputeLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                return FeaturePreComputationCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.FeaturePrecomputeLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                return await RunCycleCoreAsync(db, featureStore, settings, ct);
            }
        }

        return await RunCycleCoreAsync(db, featureStore, settings, ct);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(DefaultPollIntervalSeconds)
                : baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<FeaturePreComputationCycleResult> RunCycleCoreAsync(
        DbContext db,
        IFeatureStore featureStore,
        FeaturePreComputationSettings settings,
        CancellationToken ct)
    {
        var activePairs = await LoadActivePairsAsync(db, ct);
        if (activePairs.Count == 0)
            return FeaturePreComputationCycleResult.Empty(settings);

        int evaluatedPairs = 0;
        int pendingVectors = 0;
        int vectorsWritten = 0;
        int lineageWrites = 0;
        int insufficientHistoryPairs = 0;
        int freshPairs = 0;
        int errorPairs = 0;
        var cotLookupCache = new Dictionary<string, CotFeatureLookupSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in activePairs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await ProcessPairAsync(
                    db,
                    featureStore,
                    pair,
                    settings,
                    cotLookupCache,
                    ct);

                if (outcome.InsufficientHistory)
                {
                    insufficientHistoryPairs++;
                    continue;
                }

                if (!outcome.Evaluated)
                    continue;

                evaluatedPairs++;
                pendingVectors += outcome.PendingVectorCount;
                vectorsWritten += outcome.VectorCount;
                lineageWrites += outcome.LineageWriteCount;

                if (outcome.VectorCount == 0)
                    freshPairs++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorPairs++;
                _metrics?.FeaturePrecomputePairsSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "pair_error"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to pre-compute features for {Symbol}/{Timeframe}.",
                    WorkerName,
                    pair.Symbol,
                    pair.Timeframe);
            }
        }

        if (evaluatedPairs > 0)
            _metrics?.FeaturePrecomputePairsEvaluated.Add(evaluatedPairs);

        if (vectorsWritten > 0)
            _metrics?.FeaturePrecomputeVectorsWritten.Add(vectorsWritten);

        if (lineageWrites > 0)
            _metrics?.FeaturePrecomputeLineageWrites.Add(lineageWrites);

        if (insufficientHistoryPairs > 0)
        {
            _metrics?.FeaturePrecomputePairsSkipped.Add(
                insufficientHistoryPairs,
                new KeyValuePair<string, object?>("reason", "insufficient_history"));
        }

        if (freshPairs > 0)
        {
            _metrics?.FeaturePrecomputePairsSkipped.Add(
                freshPairs,
                new KeyValuePair<string, object?>("reason", "already_fresh"));
        }

        return new FeaturePreComputationCycleResult(
            settings,
            ActivePairCount: activePairs.Count,
            EvaluatedPairCount: evaluatedPairs,
            PendingVectorCount: pendingVectors,
            VectorCount: vectorsWritten,
            LineageWriteCount: lineageWrites,
            InsufficientHistoryPairCount: insufficientHistoryPairs,
            FreshPairCount: freshPairs,
            ErrorPairCount: errorPairs,
            SkippedReason: null);
    }

    private async Task<FeaturePreComputationPairOutcome> ProcessPairAsync(
        DbContext db,
        IFeatureStore featureStore,
        ActivePairInfo pair,
        FeaturePreComputationSettings settings,
        Dictionary<string, CotFeatureLookupSnapshot> cotLookupCache,
        CancellationToken ct)
    {
        int requiredCandles = MLFeatureHelper.LookbackWindow + settings.CatchUpBarsPerPair;

        var candles = await db.Set<Candle>()
            .AsNoTracking()
            .Where(candle =>
                !candle.IsDeleted &&
                candle.IsClosed &&
                candle.Symbol == pair.Symbol &&
                candle.Timeframe == pair.Timeframe)
            .OrderByDescending(candle => candle.Timestamp)
            .Take(requiredCandles)
            .OrderBy(candle => candle.Timestamp)
            .ToListAsync(ct);

        if (candles.Count < MLFeatureHelper.LookbackWindow + 1)
            return FeaturePreComputationPairOutcome.ForInsufficientHistory();

        int firstEligibleIndex = MLFeatureHelper.LookbackWindow;
        int targetStartIndex = Math.Max(firstEligibleIndex, candles.Count - settings.CatchUpBarsPerPair);
        var targetCandleIds = candles
            .Skip(targetStartIndex)
            .Select(candle => candle.Id)
            .ToList();

        if (targetCandleIds.Count == 0)
            return FeaturePreComputationPairOutcome.ForFresh();

        var existingVectors = await db.Set<FeatureVector>()
            .IgnoreQueryFilters()
            .Where(feature => targetCandleIds.Contains(feature.CandleId))
            .OrderByDescending(feature => feature.ComputedAt)
            .ToListAsync(ct);
        var existingByCandleId = existingVectors
            .GroupBy(feature => feature.CandleId)
            .ToDictionary(group => group.Key, group => group.First());

        int earliestComputedIndex = int.MaxValue;
        int latestComputedIndex = -1;
        var vectorsToPersist = new List<StoredFeatureVector>();
        string currentSchemaHash = featureStore.CurrentSchemaHash;
        int currentSchemaVersion = featureStore.CurrentSchemaVersion;

        for (int index = targetStartIndex; index < candles.Count; index++)
        {
            var current = candles[index];
            if (existingByCandleId.TryGetValue(current.Id, out var existing) &&
                IsCurrentVector(existing, currentSchemaHash, currentSchemaVersion))
            {
                continue;
            }

            if (!cotLookupCache.TryGetValue(pair.Symbol, out var cotLookup))
            {
                cotLookup = await CotFeatureLookupSnapshot.LoadAsync(db, pair.Symbol, ct);
                cotLookupCache[pair.Symbol] = cotLookup;
            }

            var previous = candles[index - 1];
            var window = candles.GetRange(index - MLFeatureHelper.LookbackWindow, MLFeatureHelper.LookbackWindow);
            float[] floatFeatures = MLFeatureHelper.BuildFeatureVector(
                window,
                current,
                previous,
                cotLookup.Resolve(current.Timestamp));

            vectorsToPersist.Add(new StoredFeatureVector(
                current.Id,
                pair.Symbol,
                pair.Timeframe,
                current.Timestamp,
                Array.ConvertAll(floatFeatures, feature => (double)feature),
                currentSchemaVersion,
                BaseFeatureNames)
            {
                SchemaHash = currentSchemaHash
            });

            earliestComputedIndex = Math.Min(earliestComputedIndex, index);
            latestComputedIndex = Math.Max(latestComputedIndex, index);
        }

        if (vectorsToPersist.Count == 0)
            return FeaturePreComputationPairOutcome.ForFresh();

        await featureStore.PersistBatchAsync(vectorsToPersist, ct);
        _metrics?.FeaturePrecomputeCatchUpBars.Record(vectorsToPersist.Count);

        int lineageWrites = 0;
        if (earliestComputedIndex != int.MaxValue && latestComputedIndex >= earliestComputedIndex)
        {
            int oldestUsedIndex = earliestComputedIndex - MLFeatureHelper.LookbackWindow;
            await featureStore.RecordLineageAsync(
                pair.Symbol,
                pair.Timeframe,
                currentSchemaHash,
                candles[oldestUsedIndex].Timestamp,
                candles[latestComputedIndex].Timestamp,
                latestComputedIndex - oldestUsedIndex + 1,
                vectorsToPersist[0].Features.Length,
                ct);
            lineageWrites = 1;
        }

        return FeaturePreComputationPairOutcome.ForComputed(
            pendingVectorCount: vectorsToPersist.Count,
            vectorCount: vectorsToPersist.Count,
            lineageWriteCount: lineageWrites);
    }

    private async Task<FeaturePreComputationSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        int configuredPollSeconds = await GetIntAsync(db, CK_PollSecs, DefaultPollIntervalSeconds, ct);
        int configuredCatchUpBars = await GetIntAsync(db, CK_CatchUpBarsPerPair, DefaultCatchUpBarsPerPair, ct);

        int pollSeconds = Clamp(configuredPollSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);
        int catchUpBars = Clamp(configuredCatchUpBars, MinCatchUpBarsPerPair, MaxCatchUpBarsPerPair);

        LogNormalizedSetting(CK_PollSecs, configuredPollSeconds, pollSeconds);
        LogNormalizedSetting(CK_CatchUpBarsPerPair, configuredCatchUpBars, catchUpBars);

        return new FeaturePreComputationSettings(
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            CatchUpBarsPerPair: catchUpBars);
    }

    private async Task<List<ActivePairInfo>> LoadActivePairsAsync(DbContext db, CancellationToken ct)
    {
        var pairs = await db.Set<Strategy>()
            .AsNoTracking()
            .Where(strategy => !strategy.IsDeleted && strategy.Status == StrategyStatus.Active)
            .Select(strategy => new { strategy.Symbol, strategy.Timeframe })
            .Distinct()
            .OrderBy(pair => pair.Symbol)
            .ThenBy(pair => pair.Timeframe)
            .ToListAsync(ct);

        return pairs
            .Select(pair => new ActivePairInfo(pair.Symbol, pair.Timeframe))
            .ToList();
    }

    private void LogNormalizedSetting<T>(string key, T configuredValue, T effectiveValue)
        where T : IEquatable<T>
    {
        if (configuredValue.Equals(effectiveValue))
            return;

        _logger.LogDebug(
            "{Worker}: normalized config {Key} from {Configured} to {Effective}.",
            WorkerName,
            key,
            configuredValue,
            effectiveValue);
    }

    private static bool IsCurrentVector(
        FeatureVector existing,
        string currentSchemaHash,
        int currentSchemaVersion)
    {
        return !existing.IsDeleted &&
               existing.SchemaHash == currentSchemaHash &&
               existing.SchemaVersion == currentSchemaVersion &&
               existing.FeatureCount == MLFeatureHelper.FeatureCount;
    }

    private static async Task<int> GetIntAsync(DbContext db, string key, int defaultValue, CancellationToken ct)
    {
        var raw = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => !config.IsDeleted && config.Key == key)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private readonly record struct ActivePairInfo(string Symbol, Timeframe Timeframe);

    internal readonly record struct FeaturePreComputationSettings(
        TimeSpan PollInterval,
        int CatchUpBarsPerPair);

    internal readonly record struct FeaturePreComputationCycleResult(
        FeaturePreComputationSettings Settings,
        int ActivePairCount,
        int EvaluatedPairCount,
        int PendingVectorCount,
        int VectorCount,
        int LineageWriteCount,
        int InsufficientHistoryPairCount,
        int FreshPairCount,
        int ErrorPairCount,
        string? SkippedReason)
    {
        public static FeaturePreComputationCycleResult Empty(FeaturePreComputationSettings settings)
            => new(
                settings,
                ActivePairCount: 0,
                EvaluatedPairCount: 0,
                PendingVectorCount: 0,
                VectorCount: 0,
                LineageWriteCount: 0,
                InsufficientHistoryPairCount: 0,
                FreshPairCount: 0,
                ErrorPairCount: 0,
                SkippedReason: null);

        public static FeaturePreComputationCycleResult Skipped(
            FeaturePreComputationSettings settings,
            string reason)
            => new(
                settings,
                ActivePairCount: 0,
                EvaluatedPairCount: 0,
                PendingVectorCount: 0,
                VectorCount: 0,
                LineageWriteCount: 0,
                InsufficientHistoryPairCount: 0,
                FreshPairCount: 0,
                ErrorPairCount: 0,
                SkippedReason: reason);
    }

    private readonly record struct FeaturePreComputationPairOutcome(
        bool Evaluated,
        bool InsufficientHistory,
        int PendingVectorCount,
        int VectorCount,
        int LineageWriteCount)
    {
        public static FeaturePreComputationPairOutcome ForInsufficientHistory()
            => new(
                Evaluated: false,
                InsufficientHistory: true,
                PendingVectorCount: 0,
                VectorCount: 0,
                LineageWriteCount: 0);

        public static FeaturePreComputationPairOutcome ForFresh()
            => new(
                Evaluated: true,
                InsufficientHistory: false,
                PendingVectorCount: 0,
                VectorCount: 0,
                LineageWriteCount: 0);

        public static FeaturePreComputationPairOutcome ForComputed(
            int pendingVectorCount,
            int vectorCount,
            int lineageWriteCount)
            => new(
                Evaluated: true,
                InsufficientHistory: false,
                PendingVectorCount: pendingVectorCount,
                VectorCount: vectorCount,
                LineageWriteCount: lineageWriteCount);
    }
}
