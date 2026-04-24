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
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Backfills the historical feature store for candles whose stored base feature vector
/// is missing, stale, or structurally invalid under the current schema.
///
/// <para>
/// Unlike the active-pair pre-computation worker, this worker scans historical candles
/// oldest-first across the full corpus. It excludes permanently ineligible early bars
/// that do not yet have enough lookback context, preventing the historical backlog from
/// stalling forever on the first <see cref="MLFeatureHelper.LookbackWindow"/> bars of
/// each symbol/timeframe.
/// </para>
/// </summary>
public sealed class FeatureStoreBackfillWorker : BackgroundService
{
    internal const string WorkerName = nameof(FeatureStoreBackfillWorker);

    private const string DistributedLockKey = "workers:feature-store-backfill:cycle";

    private const int DefaultPollIntervalSeconds = 3600;
    private const int MinPollIntervalSeconds = 5;
    private const int MaxPollIntervalSeconds = 24 * 60 * 60;

    private const int DefaultScanPageSize = 500;
    private const int MinScanPageSize = 1;
    private const int MaxScanPageSize = 5000;

    private const int DefaultMaxCandlesPerRun = 10_000;
    private const int MinMaxCandlesPerRun = 1;
    private const int MaxMaxCandlesPerRun = 100_000;

    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly string[] BaseFeatureNames = MLFeatureHelper.ResolveFeatureNames(MLFeatureHelper.FeatureCount);
    private static readonly string SerializedBaseFeatureNamesJson = JsonSerializer.Serialize(BaseFeatureNames);
    private static readonly int ExpectedFeatureByteLength = MLFeatureHelper.FeatureCount * sizeof(double);

    private readonly ILogger<FeatureStoreBackfillWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FeatureStoreOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public FeatureStoreBackfillWorker(
        ILogger<FeatureStoreBackfillWorker> logger,
        IServiceScopeFactory scopeFactory,
        FeatureStoreOptions options,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options;
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
            "Backfills historical base feature vectors for eligible candles whose stored vectors are missing, stale, or corrupt under the current schema, while recording lineage for reproducibility.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.PendingCandleCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.FeatureStoreBackfillCycleDurationMs.Record(durationMs);

                    if (result.PendingCandleCount > 0)
                        _metrics?.FeatureStoreBackfillPendingCandles.Record(result.PendingCandleCount);

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
                            "{Worker}: pendingCandles={Pending}, scannedPages={Pages}, groups={Groups}, vectorsWritten={Vectors}, lineageWrites={Lineages}, insufficient={Insufficient}, errors={Errors}.",
                            WorkerName,
                            result.PendingCandleCount,
                            result.PageCount,
                            result.GroupCount,
                            result.VectorCount,
                            result.LineageWriteCount,
                            result.InsufficientHistoryCount,
                            result.ErrorCount);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "{Worker}: no historical candles require feature-store backfill under the current schema.");
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
                        new KeyValuePair<string, object?>("reason", "feature_store_backfill_cycle"));
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

    internal async Task<FeatureStoreBackfillCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var featureStore = scope.ServiceProvider.GetRequiredService<IFeatureStore>();
        var db = writeContext.GetDbContext();
        var settings = LoadSettings();

        if (_distributedLock is null)
        {
            _metrics?.FeatureStoreBackfillLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate historical backfill cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
            {
                _metrics?.FeatureStoreBackfillLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.FeatureStoreBackfillCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return FeatureStoreBackfillCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.FeatureStoreBackfillLockAttempts.Add(
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

    private async Task<FeatureStoreBackfillCycleResult> RunCycleCoreAsync(
        DbContext db,
        IFeatureStore featureStore,
        FeatureStoreBackfillSettings settings,
        CancellationToken ct)
    {
        string currentSchemaHash = featureStore.CurrentSchemaHash;
        int currentSchemaVersion = featureStore.CurrentSchemaVersion;

        int pendingCandles = 0;
        int vectorsWritten = 0;
        int lineageWrites = 0;
        int insufficientHistory = 0;
        int errorCount = 0;
        int groupCount = 0;
        int pageCount = 0;
        var cotLookupCache = new Dictionary<string, CotFeatureLookupSnapshot>(StringComparer.OrdinalIgnoreCase);
        DateTime? cursorTimestamp = null;
        long cursorId = 0;

        while (vectorsWritten < settings.MaxCandlesPerRun && !ct.IsCancellationRequested)
        {
            int remainingBudget = settings.MaxCandlesPerRun - vectorsWritten;
            var page = await LoadCandidatePageAsync(
                db,
                currentSchemaHash,
                currentSchemaVersion,
                cursorTimestamp,
                cursorId,
                Math.Min(settings.ScanPageSize, settings.MaxCandlesPerRun),
                ct);

            if (page.Count == 0)
                break;

            pageCount++;
            pendingCandles += page.Count;
            cursorTimestamp = page[^1].Timestamp;
            cursorId = page[^1].Id;

            var groups = page
                .GroupBy(candle => new ActivePairInfo(candle.Symbol, candle.Timeframe))
                .ToList();

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();

                int writeBudget = settings.MaxCandlesPerRun - vectorsWritten;
                if (writeBudget <= 0)
                    break;

                var outcome = await ProcessGroupAsync(
                    db,
                    featureStore,
                    group.Key,
                    group.OrderBy(candle => candle.Timestamp).ToList(),
                    currentSchemaHash,
                    currentSchemaVersion,
                    writeBudget,
                    cotLookupCache,
                    ct);

                groupCount++;
                vectorsWritten += outcome.VectorCount;
                lineageWrites += outcome.LineageWriteCount;
                insufficientHistory += outcome.InsufficientHistoryCount;
                errorCount += outcome.ErrorCount;
            }

            if (page.Count < settings.ScanPageSize || remainingBudget <= 0)
                break;
        }

        if (pendingCandles > 0)
            _metrics?.FeatureStoreBackfillCandlesEvaluated.Add(pendingCandles);

        if (vectorsWritten > 0)
            _metrics?.FeatureStoreBackfillVectorsWritten.Add(vectorsWritten);

        if (lineageWrites > 0)
            _metrics?.FeatureStoreBackfillLineageWrites.Add(lineageWrites);

        if (insufficientHistory > 0)
        {
            _metrics?.FeatureStoreBackfillCandlesSkipped.Add(
                insufficientHistory,
                new KeyValuePair<string, object?>("reason", "insufficient_history"));
        }

        if (errorCount > 0)
        {
            _metrics?.FeatureStoreBackfillCandlesSkipped.Add(
                errorCount,
                new KeyValuePair<string, object?>("reason", "compute_error"));
        }

        return new FeatureStoreBackfillCycleResult(
            settings,
            PendingCandleCount: pendingCandles,
            VectorCount: vectorsWritten,
            LineageWriteCount: lineageWrites,
            InsufficientHistoryCount: insufficientHistory,
            ErrorCount: errorCount,
            GroupCount: groupCount,
            PageCount: pageCount,
            SkippedReason: null);
    }

    private async Task<List<Candle>> LoadCandidatePageAsync(
        DbContext db,
        string currentSchemaHash,
        int currentSchemaVersion,
        DateTime? cursorTimestamp,
        long cursorId,
        int pageSize,
        CancellationToken ct)
    {
        var query = db.Set<Candle>()
            .AsNoTracking()
            .Where(candle =>
                candle.IsClosed &&
                !candle.IsDeleted &&
                db.Set<Candle>().Count(prior =>
                    prior.IsClosed &&
                    !prior.IsDeleted &&
                    prior.Symbol == candle.Symbol &&
                    prior.Timeframe == candle.Timeframe &&
                    prior.Timestamp < candle.Timestamp) >= MLFeatureHelper.LookbackWindow &&
                !db.Set<FeatureVector>().Any(feature =>
                    feature.CandleId == candle.Id &&
                    !feature.IsDeleted &&
                    feature.SchemaHash == currentSchemaHash &&
                    feature.SchemaVersion == currentSchemaVersion &&
                    feature.FeatureCount == MLFeatureHelper.FeatureCount &&
                    feature.FeatureNamesJson == SerializedBaseFeatureNamesJson &&
                    feature.Features.Length == ExpectedFeatureByteLength));

        if (cursorTimestamp is { } timestamp)
        {
            query = query.Where(candle =>
                candle.Timestamp > timestamp ||
                (candle.Timestamp == timestamp && candle.Id > cursorId));
        }

        return await query
            .OrderBy(candle => candle.Timestamp)
            .ThenBy(candle => candle.Id)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    private async Task<FeatureStoreBackfillGroupOutcome> ProcessGroupAsync(
        DbContext db,
        IFeatureStore featureStore,
        ActivePairInfo pair,
        List<Candle> targetCandles,
        string currentSchemaHash,
        int currentSchemaVersion,
        int writeBudget,
        Dictionary<string, CotFeatureLookupSnapshot> cotLookupCache,
        CancellationToken ct)
    {
        if (targetCandles.Count == 0 || writeBudget <= 0)
            return FeatureStoreBackfillGroupOutcome.Empty;

        DateTime earliestTimestamp = targetCandles[0].Timestamp;
        DateTime latestTimestamp = targetCandles[^1].Timestamp;

        var lookbackCandles = await db.Set<Candle>()
            .AsNoTracking()
            .Where(candle =>
                !candle.IsDeleted &&
                candle.IsClosed &&
                candle.Symbol == pair.Symbol &&
                candle.Timeframe == pair.Timeframe &&
                candle.Timestamp < earliestTimestamp)
            .OrderByDescending(candle => candle.Timestamp)
            .Take(MLFeatureHelper.LookbackWindow)
            .ToListAsync(ct);
        lookbackCandles.Reverse();

        var rangeCandles = await db.Set<Candle>()
            .AsNoTracking()
            .Where(candle =>
                !candle.IsDeleted &&
                candle.IsClosed &&
                candle.Symbol == pair.Symbol &&
                candle.Timeframe == pair.Timeframe &&
                candle.Timestamp >= earliestTimestamp &&
                candle.Timestamp <= latestTimestamp)
            .OrderBy(candle => candle.Timestamp)
            .ToListAsync(ct);

        var contextCandles = new List<Candle>(lookbackCandles.Count + rangeCandles.Count);
        contextCandles.AddRange(lookbackCandles);
        contextCandles.AddRange(rangeCandles);

        var candleIndex = new Dictionary<long, int>(contextCandles.Count);
        for (int index = 0; index < contextCandles.Count; index++)
            candleIndex[contextCandles[index].Id] = index;

        var cotLookupKey = GetCotLookupCacheKey(pair.Symbol);
        if (!cotLookupCache.TryGetValue(cotLookupKey, out var cotLookup))
        {
            cotLookup = await CotFeatureLookupSnapshot.LoadAsync(db, pair.Symbol, ct);
            cotLookupCache[cotLookupKey] = cotLookup;
        }

        var vectorsToPersist = new List<StoredFeatureVector>();
        int insufficientHistory = 0;
        int errorCount = 0;
        int earliestComputedIndex = int.MaxValue;
        int latestComputedIndex = -1;

        foreach (var candle in targetCandles)
        {
            if (vectorsToPersist.Count >= writeBudget)
                break;

            if (!candleIndex.TryGetValue(candle.Id, out int index) || index < MLFeatureHelper.LookbackWindow)
            {
                insufficientHistory++;
                continue;
            }

            try
            {
                var current = contextCandles[index];
                var previous = contextCandles[index - 1];
                var window = contextCandles.GetRange(index - MLFeatureHelper.LookbackWindow, MLFeatureHelper.LookbackWindow);
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
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to compute historical features for candle {CandleId} ({Symbol}/{Timeframe}).",
                    WorkerName,
                    candle.Id,
                    pair.Symbol,
                    pair.Timeframe);
            }
        }

        if (vectorsToPersist.Count == 0)
        {
            return new FeatureStoreBackfillGroupOutcome(
                VectorCount: 0,
                LineageWriteCount: 0,
                InsufficientHistoryCount: insufficientHistory,
                ErrorCount: errorCount);
        }

        await featureStore.PersistBatchAsync(vectorsToPersist, ct);

        int lineageWrites = 0;
        if (earliestComputedIndex != int.MaxValue && latestComputedIndex >= earliestComputedIndex)
        {
            int oldestUsedIndex = earliestComputedIndex - MLFeatureHelper.LookbackWindow;
            await featureStore.RecordLineageAsync(
                pair.Symbol,
                pair.Timeframe,
                currentSchemaHash,
                contextCandles[oldestUsedIndex].Timestamp,
                contextCandles[latestComputedIndex].Timestamp,
                latestComputedIndex - oldestUsedIndex + 1,
                vectorsToPersist[0].Features.Length,
                ct);
            lineageWrites = 1;
        }

        return new FeatureStoreBackfillGroupOutcome(
            VectorCount: vectorsToPersist.Count,
            LineageWriteCount: lineageWrites,
            InsufficientHistoryCount: insufficientHistory,
            ErrorCount: errorCount);
    }

    private FeatureStoreBackfillSettings LoadSettings()
    {
        int configuredPollSeconds = _options.BackfillPollIntervalSeconds;
        int configuredScanPageSize = _options.BackfillBatchSize;
        int configuredMaxCandlesPerRun = _options.MaxCandlesPerRun;

        int pollSeconds = Clamp(configuredPollSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);
        int scanPageSize = Clamp(configuredScanPageSize, MinScanPageSize, MaxScanPageSize);
        int maxCandlesPerRun = Clamp(configuredMaxCandlesPerRun, MinMaxCandlesPerRun, MaxMaxCandlesPerRun);

        if (scanPageSize > maxCandlesPerRun)
            scanPageSize = maxCandlesPerRun;

        LogNormalizedSetting("FeatureStore:BackfillPollIntervalSeconds", configuredPollSeconds, pollSeconds);
        LogNormalizedSetting("FeatureStore:BackfillBatchSize", configuredScanPageSize, scanPageSize);
        LogNormalizedSetting("FeatureStore:MaxCandlesPerRun", configuredMaxCandlesPerRun, maxCandlesPerRun);

        return new FeatureStoreBackfillSettings(
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            ScanPageSize: scanPageSize,
            MaxCandlesPerRun: maxCandlesPerRun);
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

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private static string GetCotLookupCacheKey(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return string.Empty;

        return symbol.Length >= 3
            ? symbol[..3].ToUpperInvariant()
            : symbol.ToUpperInvariant();
    }

    internal static bool IsCurrentVector(
        FeatureVector vector,
        string currentSchemaHash,
        int currentSchemaVersion)
    {
        return !vector.IsDeleted &&
               vector.SchemaHash == currentSchemaHash &&
               vector.SchemaVersion == currentSchemaVersion &&
               vector.FeatureCount == MLFeatureHelper.FeatureCount &&
               vector.Features.Length == ExpectedFeatureByteLength &&
               HasExpectedFeatureNames(vector.FeatureNamesJson);
    }

    private static bool HasExpectedFeatureNames(string featureNamesJson)
    {
        if (string.Equals(featureNamesJson, SerializedBaseFeatureNamesJson, StringComparison.Ordinal))
            return true;

        try
        {
            var featureNames = JsonSerializer.Deserialize<string[]>(featureNamesJson);
            return featureNames is not null &&
                   featureNames.Length == BaseFeatureNames.Length &&
                   featureNames.SequenceEqual(BaseFeatureNames, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct ActivePairInfo(string Symbol, Timeframe Timeframe);

    private readonly record struct FeatureStoreBackfillGroupOutcome(
        int VectorCount,
        int LineageWriteCount,
        int InsufficientHistoryCount,
        int ErrorCount)
    {
        public static FeatureStoreBackfillGroupOutcome Empty => new(0, 0, 0, 0);
    }
}

internal readonly record struct FeatureStoreBackfillSettings(
    TimeSpan PollInterval,
    int ScanPageSize,
    int MaxCandlesPerRun);

internal readonly record struct FeatureStoreBackfillCycleResult(
    FeatureStoreBackfillSettings Settings,
    int PendingCandleCount,
    int VectorCount,
    int LineageWriteCount,
    int InsufficientHistoryCount,
    int ErrorCount,
    int GroupCount,
    int PageCount,
    string? SkippedReason)
{
    public static FeatureStoreBackfillCycleResult Skipped(
        FeatureStoreBackfillSettings settings,
        string reason)
        => new(
            settings,
            PendingCandleCount: 0,
            VectorCount: 0,
            LineageWriteCount: 0,
            InsufficientHistoryCount: 0,
            ErrorCount: 0,
            GroupCount: 0,
            PageCount: 0,
            SkippedReason: reason);
}
