using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.EventBus.Events;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Retries failed integration-event publications from the outbox and performs a single
/// bounded safety replay for stale <see cref="EventStateEnum.Published"/> rows to reduce
/// the publish-vs-broker-ack uncertainty window without creating unbounded duplicates.
/// </summary>
public sealed class IntegrationEventRetryWorker : BackgroundService
{
    internal const string WorkerName = nameof(IntegrationEventRetryWorker);

    private const string CK_PollIntervalSeconds = "IntegrationEventRetry:PollIntervalSeconds";
    private const string CK_StuckThresholdSeconds = "IntegrationEventRetry:StuckThresholdSeconds";
    private const string CK_StalePublishedThresholdSeconds = "IntegrationEventRetry:StalePublishedThresholdSeconds";
    private const string CK_MaxRetries = "IntegrationEventRetry:MaxRetries";
    private const string CK_BatchSize = "IntegrationEventRetry:BatchSize";
    private const string DistributedLockKey = "workers:integration-event-retry:cycle";

    private const int DefaultPollIntervalSeconds = 30;
    private const int MinPollIntervalSeconds = 5;
    private const int MaxPollIntervalSeconds = 3600;

    private const int DefaultStuckThresholdSeconds = 120;
    private const int MinStuckThresholdSeconds = 30;
    private const int MaxStuckThresholdSeconds = 86_400;

    private const int DefaultStalePublishedThresholdSeconds = 30;
    private const int MinStalePublishedThresholdSeconds = 10;
    private const int MaxStalePublishedThresholdSeconds = 86_400;

    private const int DefaultMaxRetries = 5;
    private const int MinMaxRetries = 1;
    private const int MaxMaxRetries = 20;

    private const int DefaultBatchSize = 50;
    private const int MinBatchSize = 1;
    private const int MaxBatchSize = 500;

    // A Published row already has TimesSent=1 from the original save-and-publish path.
    // We allow exactly one additional safety replay attempt; after that the row is aged
    // out of stale replay selection even if it remains Published.
    private const int MaxStalePublishedTimesSentExclusive = 2;

    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly IDegradationModeManager _degradationManager;
    private readonly ILogger<IntegrationEventRetryWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;
    private readonly Dictionary<string, Type> _eventTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _eventTypesGate = new();

    private bool _eventTypesInitialized;
    private bool _missingDistributedLockWarningEmitted;
    private int _consecutiveFailures;

    public IntegrationEventRetryWorker(
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IDegradationModeManager degradationManager,
        ILogger<IntegrationEventRetryWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _eventBus = eventBus;
        _degradationManager = degradationManager;
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
            "Retries failed integration-event publications, dead-letters unrecoverable outbox rows, and performs a single bounded stale-published safety replay.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.BacklogDepth);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.EventRetryCycleDurationMs.Record(durationMs);

                    if (result.BacklogDepth > 0)
                        _metrics?.EventRetryBacklogDepth.Record(result.BacklogDepth);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else if (result.RetriedCount > 0 || result.StaleRepublishedCount > 0 || result.DeadLetteredCount > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: retryableCandidates={Retryable}, staleCandidates={Stale}, retried={Retried}, staleRepublished={StaleRepublished}, deadLettered={DeadLettered}, exhausted={Exhausted}, staleSkipped={StaleSkipped}.",
                            WorkerName,
                            result.RetryableCandidateCount,
                            result.StaleCandidateCount,
                            result.RetriedCount,
                            result.StaleRepublishedCount,
                            result.DeadLetteredCount,
                            result.ExhaustedCount,
                            result.StaleSkippedCount);
                    }
                    else
                    {
                        _logger.LogDebug("{Worker}: no retryable integration events found.", WorkerName);
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
                        new KeyValuePair<string, object?>("reason", "integration_event_retry_cycle"));
                    _logger.LogError(ex, "{Worker}: unexpected error in retry loop.", WorkerName);
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

    internal async Task<IntegrationEventRetryCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settingsDb = scope.ServiceProvider.GetService<IWriteApplicationDbContext>()?.GetDbContext();
        var settings = await LoadSettingsAsync(settingsDb, ct);

        if (_distributedLock is null)
        {
            _metrics?.EventRetryLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate retry cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
            {
                _metrics?.EventRetryLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.EventRetryCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return IntegrationEventRetryCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.EventRetryLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogReader>();
                var deadLetterSink = scope.ServiceProvider.GetRequiredService<IDeadLetterSink>();
                return await RunCycleCoreAsync(eventLog, deadLetterSink, settings, ct);
            }
        }

        var unlockedEventLog = scope.ServiceProvider.GetRequiredService<IEventLogReader>();
        var unlockedDeadLetterSink = scope.ServiceProvider.GetRequiredService<IDeadLetterSink>();
        return await RunCycleCoreAsync(unlockedEventLog, unlockedDeadLetterSink, settings, ct);
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

    private async Task<IntegrationEventRetryCycleResult> RunCycleCoreAsync(
        IEventLogReader eventLog,
        IDeadLetterSink deadLetterSink,
        IntegrationEventRetrySettings settings,
        CancellationToken ct)
    {
        if (_degradationManager.CurrentMode == DegradationMode.EventBusDegraded)
        {
            _metrics?.EventRetryCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "event_bus_degraded"));
            return IntegrationEventRetryCycleResult.Skipped(settings, "event_bus_degraded");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var retryableEntries = await eventLog.GetRetryableEventsAsync(
            nowUtc - settings.StuckThreshold,
            settings.BatchSize,
            ct);
        var stalePublishedEntries = await eventLog.GetStalePublishedEventsAsync(
            nowUtc - settings.StalePublishedThreshold,
            MaxStalePublishedTimesSentExclusive,
            settings.BatchSize,
            ct);

        int backlogDepth = retryableEntries.Count + stalePublishedEntries.Count;
        var retryResult = await ProcessRetryableEventsAsync(eventLog, deadLetterSink, retryableEntries, settings, ct);
        var staleResult = await ProcessStalePublishedEventsAsync(eventLog, stalePublishedEntries, ct);

        return new IntegrationEventRetryCycleResult(
            settings,
            BacklogDepth: backlogDepth,
            RetryableCandidateCount: retryableEntries.Count,
            StaleCandidateCount: stalePublishedEntries.Count,
            RetriedCount: retryResult.RetriedCount,
            DeadLetteredCount: retryResult.DeadLetteredCount,
            ExhaustedCount: retryResult.ExhaustedCount,
            StaleRepublishedCount: staleResult.RePublishedCount,
            StaleSkippedCount: staleResult.SkippedCount,
            SkippedReason: null);
    }

    private async Task<RetryBatchResult> ProcessRetryableEventsAsync(
        IEventLogReader eventLog,
        IDeadLetterSink deadLetterSink,
        IReadOnlyList<IntegrationEventLogEntry> retryableEntries,
        IntegrationEventRetrySettings settings,
        CancellationToken ct)
    {
        int retried = 0;
        int deadLettered = 0;
        int exhausted = 0;

        foreach (var entry in retryableEntries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.TimesSent >= settings.MaxRetries)
            {
                exhausted++;

                if (await TryDeadLetterAndMarkTerminalAsync(
                        eventLog,
                        deadLetterSink,
                        entry,
                        $"Recovered non-terminal retryable entry with exhausted attempts ({entry.TimesSent}/{settings.MaxRetries}).",
                        stackTrace: null,
                        attempts: entry.TimesSent,
                        deadLetterReason: "retry_exhausted_recovered"))
                {
                    deadLettered++;
                    _metrics?.EventRetryExhausted.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", "recovered_terminal"));
                }

                continue;
            }

            if (!TryResolveEventType(entry.EventTypeName, out var eventType))
            {
                if (await TryDeadLetterAndMarkTerminalAsync(
                        eventLog,
                        deadLetterSink,
                        entry,
                        $"Unknown integration event type '{entry.EventTypeName}'.",
                        stackTrace: null,
                        attempts: entry.TimesSent,
                        deadLetterReason: "unknown_type"))
                {
                    deadLettered++;
                }

                continue;
            }

            try
            {
                entry.DeserializeJsonContent(eventType);
            }
            catch (Exception ex)
            {
                if (await TryDeadLetterAndMarkTerminalAsync(
                        eventLog,
                        deadLetterSink,
                        entry,
                        $"Failed to deserialize integration event '{entry.EventTypeName}': {ex.Message}",
                        ex.ToString(),
                        entry.TimesSent,
                        "deserialize_failure"))
                {
                    deadLettered++;
                }

                continue;
            }

            if (entry.IntegrationEvent is null)
            {
                if (await TryDeadLetterAndMarkTerminalAsync(
                        eventLog,
                        deadLetterSink,
                        entry,
                        $"Deserialization returned null for integration event '{entry.EventTypeName}'.",
                        stackTrace: null,
                        attempts: entry.TimesSent,
                        deadLetterReason: "deserialize_null"))
                {
                    deadLettered++;
                }

                continue;
            }

            try
            {
                entry.State = EventStateEnum.InProgress;
                entry.TimesSent++;
                await eventLog.SaveChangesAsync(ct);

                _eventBus.Publish(entry.IntegrationEvent);

                entry.State = EventStateEnum.Published;
                await eventLog.SaveChangesAsync(CancellationToken.None);

                retried++;
                _metrics?.EventRetrySuccesses.Add(
                    1,
                    new KeyValuePair<string, object?>("path", "retry"));
                _logger.LogDebug(
                    "{Worker}: successfully re-published event {EventId} ({Type}) on attempt {Attempt}.",
                    WorkerName,
                    entry.EventId,
                    entry.EventTypeShortName,
                    entry.TimesSent);
            }
            catch (Exception ex)
            {
                entry.State = EventStateEnum.PublishedFailed;
                await eventLog.SaveChangesAsync(CancellationToken.None);

                if (entry.TimesSent >= settings.MaxRetries)
                {
                    exhausted++;

                    if (await TryDeadLetterAndMarkTerminalAsync(
                            eventLog,
                            deadLetterSink,
                            entry,
                            $"Exhausted after {entry.TimesSent} retries: {ex.Message}",
                            ex.ToString(),
                            entry.TimesSent,
                            "retry_exhausted"))
                    {
                        deadLettered++;
                        _metrics?.EventRetryExhausted.Add(
                            1,
                            new KeyValuePair<string, object?>("reason", "max_retries"));
                    }
                }
                else
                {
                    _logger.LogWarning(
                        ex,
                        "{Worker}: retry failed for event {EventId} ({Type}), attempt {Attempt}/{Max}.",
                        WorkerName,
                        entry.EventId,
                        entry.EventTypeShortName,
                        entry.TimesSent,
                        settings.MaxRetries);
                }
            }
        }

        return new RetryBatchResult(
            RetriedCount: retried,
            DeadLetteredCount: deadLettered,
            ExhaustedCount: exhausted);
    }

    private async Task<StaleReplayBatchResult> ProcessStalePublishedEventsAsync(
        IEventLogReader eventLog,
        IReadOnlyList<IntegrationEventLogEntry> stalePublishedEntries,
        CancellationToken ct)
    {
        int rePublished = 0;
        int skipped = 0;

        foreach (var entry in stalePublishedEntries)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryResolveEventType(entry.EventTypeName, out var eventType))
            {
                skipped++;
                await AgeOutStalePublishedEntryAsync(
                    eventLog,
                    entry,
                    $"unknown event type '{entry.EventTypeName}'");
                continue;
            }

            try
            {
                entry.DeserializeJsonContent(eventType);
            }
            catch (Exception ex)
            {
                skipped++;
                await AgeOutStalePublishedEntryAsync(
                    eventLog,
                    entry,
                    $"deserialization failure: {ex.Message}");
                continue;
            }

            if (entry.IntegrationEvent is null)
            {
                skipped++;
                await AgeOutStalePublishedEntryAsync(
                    eventLog,
                    entry,
                    "deserialization returned null");
                continue;
            }

            try
            {
                _eventBus.Publish(entry.IntegrationEvent);
                rePublished++;
                _metrics?.EventRetrySuccesses.Add(
                    1,
                    new KeyValuePair<string, object?>("path", "stale_published"));
                _logger.LogDebug(
                    "{Worker}: re-published stale Published event {EventId} ({Type}) as a bounded safety replay.",
                    WorkerName,
                    entry.EventId,
                    entry.EventTypeShortName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "{Worker}: stale Published event {EventId} ({Type}) safety replay failed; leaving state Published to avoid false demotion.",
                    WorkerName,
                    entry.EventId,
                    entry.EventTypeShortName);
            }
            finally
            {
                entry.TimesSent = Math.Max(entry.TimesSent + 1, MaxStalePublishedTimesSentExclusive);
                await eventLog.SaveChangesAsync(CancellationToken.None);
            }
        }

        return new StaleReplayBatchResult(
            RePublishedCount: rePublished,
            SkippedCount: skipped);
    }

    private async Task<bool> TryDeadLetterAndMarkTerminalAsync(
        IEventLogReader eventLog,
        IDeadLetterSink deadLetterSink,
        IntegrationEventLogEntry entry,
        string errorMessage,
        string? stackTrace,
        int attempts,
        string deadLetterReason)
    {
        try
        {
            await deadLetterSink.WriteAsync(
                handlerName: WorkerName,
                eventType: entry.EventTypeShortName ?? "Unknown",
                eventPayloadJson: entry.Content,
                errorMessage: errorMessage,
                stackTrace: stackTrace,
                attempts: attempts,
                ct: CancellationToken.None);

            entry.State = EventStateEnum.DeadLettered;
            await eventLog.SaveChangesAsync(CancellationToken.None);

            _metrics?.EventRetryDeadLettered.Add(
                1,
                new KeyValuePair<string, object?>("reason", deadLetterReason));
            return true;
        }
        catch (Exception deadLetterEx)
        {
            _logger.LogCritical(
                deadLetterEx,
                "{Worker}: failed to dead-letter terminal event {EventId} ({Type}); leaving row non-terminal for manual recovery.",
                WorkerName,
                entry.EventId,
                entry.EventTypeShortName);
            return false;
        }
    }

    private async Task AgeOutStalePublishedEntryAsync(
        IEventLogReader eventLog,
        IntegrationEventLogEntry entry,
        string reason)
    {
        entry.TimesSent = Math.Max(entry.TimesSent + 1, MaxStalePublishedTimesSentExclusive);
        await eventLog.SaveChangesAsync(CancellationToken.None);

        _logger.LogWarning(
            "{Worker}: skipping stale Published event {EventId} ({Type}); {Reason}. Event remains Published and is aged out of further stale safety replays.",
            WorkerName,
            entry.EventId,
            entry.EventTypeShortName,
            reason);
    }

    private bool TryResolveEventType(string eventTypeName, out Type eventType)
    {
        EnsureEventTypesLoaded();

        lock (_eventTypesGate)
        {
            if (_eventTypes.TryGetValue(eventTypeName, out eventType!))
                return true;
        }

        RefreshEventTypes();

        lock (_eventTypesGate)
        {
            return _eventTypes.TryGetValue(eventTypeName, out eventType!);
        }
    }

    private void EnsureEventTypesLoaded()
    {
        lock (_eventTypesGate)
        {
            if (_eventTypesInitialized)
                return;
        }

        RefreshEventTypes();
    }

    private void RefreshEventTypes()
    {
        var discovered = DiscoverIntegrationEventTypes();

        lock (_eventTypesGate)
        {
            foreach (var type in discovered)
            {
                if (!string.IsNullOrWhiteSpace(type.FullName))
                    _eventTypes[type.FullName] = type;
            }

            _eventTypesInitialized = true;
        }
    }

    private static IEnumerable<Type> DiscoverIntegrationEventTypes()
    {
        var baseType = typeof(IntegrationEvent);

        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(type => type is not null)!;
                }
            })
            .Where(type => type is not null && baseType.IsAssignableFrom(type) && !type.IsAbstract)!;
    }

    private async Task<IntegrationEventRetrySettings> LoadSettingsAsync(DbContext? db, CancellationToken ct)
    {
        int configuredPollSeconds = await GetIntAsync(db, CK_PollIntervalSeconds, DefaultPollIntervalSeconds, ct);
        int configuredStuckThresholdSeconds = await GetIntAsync(db, CK_StuckThresholdSeconds, DefaultStuckThresholdSeconds, ct);
        int configuredStaleThresholdSeconds = await GetIntAsync(db, CK_StalePublishedThresholdSeconds, DefaultStalePublishedThresholdSeconds, ct);
        int configuredMaxRetries = await GetIntAsync(db, CK_MaxRetries, DefaultMaxRetries, ct);
        int configuredBatchSize = await GetIntAsync(db, CK_BatchSize, DefaultBatchSize, ct);

        int pollSeconds = Clamp(configuredPollSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);
        int stuckThresholdSeconds = Clamp(configuredStuckThresholdSeconds, MinStuckThresholdSeconds, MaxStuckThresholdSeconds);
        int staleThresholdSeconds = Clamp(configuredStaleThresholdSeconds, MinStalePublishedThresholdSeconds, MaxStalePublishedThresholdSeconds);
        int maxRetries = Clamp(configuredMaxRetries, MinMaxRetries, MaxMaxRetries);
        int batchSize = Clamp(configuredBatchSize, MinBatchSize, MaxBatchSize);

        LogNormalizedSetting(CK_PollIntervalSeconds, configuredPollSeconds, pollSeconds);
        LogNormalizedSetting(CK_StuckThresholdSeconds, configuredStuckThresholdSeconds, stuckThresholdSeconds);
        LogNormalizedSetting(CK_StalePublishedThresholdSeconds, configuredStaleThresholdSeconds, staleThresholdSeconds);
        LogNormalizedSetting(CK_MaxRetries, configuredMaxRetries, maxRetries);
        LogNormalizedSetting(CK_BatchSize, configuredBatchSize, batchSize);

        return new IntegrationEventRetrySettings(
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            StuckThreshold: TimeSpan.FromSeconds(stuckThresholdSeconds),
            StalePublishedThreshold: TimeSpan.FromSeconds(staleThresholdSeconds),
            MaxRetries: maxRetries,
            BatchSize: batchSize);
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

    private static async Task<int> GetIntAsync(DbContext? db, string key, int defaultValue, CancellationToken ct)
    {
        if (db is null)
            return defaultValue;

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

    private readonly record struct RetryBatchResult(
        int RetriedCount,
        int DeadLetteredCount,
        int ExhaustedCount);

    private readonly record struct StaleReplayBatchResult(
        int RePublishedCount,
        int SkippedCount);

    internal readonly record struct IntegrationEventRetrySettings(
        TimeSpan PollInterval,
        TimeSpan StuckThreshold,
        TimeSpan StalePublishedThreshold,
        int MaxRetries,
        int BatchSize);

    internal readonly record struct IntegrationEventRetryCycleResult(
        IntegrationEventRetrySettings Settings,
        int BacklogDepth,
        int RetryableCandidateCount,
        int StaleCandidateCount,
        int RetriedCount,
        int DeadLetteredCount,
        int ExhaustedCount,
        int StaleRepublishedCount,
        int StaleSkippedCount,
        string? SkippedReason)
    {
        public static IntegrationEventRetryCycleResult Skipped(
            IntegrationEventRetrySettings settings,
            string reason)
            => new(
                settings,
                BacklogDepth: 0,
                RetryableCandidateCount: 0,
                StaleCandidateCount: 0,
                RetriedCount: 0,
                DeadLetteredCount: 0,
                ExhaustedCount: 0,
                StaleRepublishedCount: 0,
                StaleSkippedCount: 0,
                SkippedReason: reason);
    }
}
