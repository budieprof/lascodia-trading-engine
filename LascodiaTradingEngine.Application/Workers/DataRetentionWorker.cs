using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically enforces data retention policies for hot storage, including reclaiming
/// already-retired records, purging aged operational data, and trimming worker snapshots.
/// </summary>
public sealed class DataRetentionWorker : BackgroundService
{
    internal const string WorkerName = nameof(DataRetentionWorker);
    private const string DistributedLockKey = "workers:data-retention:cycle";

    private readonly ILogger<DataRetentionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DataRetentionOptions _options;
    private readonly IDistributedLock? _distributedLock;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private static readonly TimeSpan MinimumMaxBackoff = TimeSpan.FromHours(1);
    private const int MaxBackoffMultiplier = 8;
    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    internal sealed record DataRetentionCycleResult(
        IReadOnlyList<RetentionResult> Results,
        string? SkippedReason = null)
    {
        public int TotalPurged => Results.Sum(r => r.RowsPurged);
        public int PurgedEntityTypes => Results.Count(r => r.RowsPurged > 0);

        public static DataRetentionCycleResult Skipped(string reason)
            => new(Array.Empty<RetentionResult>(), reason);
    }

    public DataRetentionWorker(
        ILogger<DataRetentionWorker> logger,
        IServiceScopeFactory scopeFactory,
        DataRetentionOptions options,
        IDistributedLock? distributedLock = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);

        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
        _distributedLock = distributedLock;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{Worker} starting (poll interval: {Interval}s, batch size: {BatchSize}, lock timeout: {LockTimeout}s)",
            WorkerName,
            _options.PollIntervalSeconds,
            _options.BatchSize,
            _options.LockTimeoutSeconds);

        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Enforces hot-storage retention policies, reclaims retired rows, and trims stale operational data.",
            TimeSpan.FromSeconds(_options.PollIntervalSeconds));

        try
        {
            try
            {
                var initialDelay = GetInitialDelay();
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
                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;

                    _healthMonitor?.RecordBacklogDepth(WorkerName, 0);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else if (result.TotalPurged > 0)
                    {
                        foreach (var entry in result.Results.Where(r => r.RowsPurged > 0))
                        {
                            _metrics?.RetentionRowsDeleted.Add(
                                entry.RowsPurged,
                                new KeyValuePair<string, object?>("table", entry.EntityType));
                        }

                        _logger.LogInformation(
                            "{Worker}: purged {Total} records across {Types} entity types in {DurationMs}ms ({Breakdown}).",
                            WorkerName,
                            result.TotalPurged,
                            result.PurgedEntityTypes,
                            durationMs,
                            string.Join(", ", result.Results
                                .Where(r => r.RowsPurged > 0)
                                .Select(r => $"{r.EntityType}={r.RowsPurged}")));
                    }
                    else
                    {
                        _logger.LogDebug("{Worker}: cycle complete; no records eligible for purge.", WorkerName);
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
                        new KeyValuePair<string, object?>("reason", "retention_cycle"));
                    _logger.LogError(
                        ex,
                        "{Worker}: error during retention cycle (consecutive failures: {Failures}).",
                        WorkerName,
                        _consecutiveFailures);
                }

                var delay = GetNextDelay(_consecutiveFailures);

                try
                {
                    await Task.Delay(delay, _timeProvider, stoppingToken);
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

    internal async Task<DataRetentionCycleResult> RunCycleAsync(CancellationToken stoppingToken)
    {
        if (_distributedLock is null)
        {
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate retention sweeps are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(0, _options.LockTimeoutSeconds));
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, timeout, stoppingToken);
            if (cycleLock is null)
                return DataRetentionCycleResult.Skipped("lock_busy");

            await using (cycleLock)
            {
                await using var lockedScope = _scopeFactory.CreateAsyncScope();
                var lockedRetentionManager = lockedScope.ServiceProvider.GetRequiredService<IDataRetentionManager>();
                var lockedResults = await lockedRetentionManager.EnforceRetentionAsync(stoppingToken);
                return new DataRetentionCycleResult(lockedResults);
            }
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var retentionManager = scope.ServiceProvider.GetRequiredService<IDataRetentionManager>();
        var results = await retentionManager.EnforceRetentionAsync(stoppingToken);
        return new DataRetentionCycleResult(results);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = baseInterval.TotalSeconds * Math.Pow(2, cappedExponent);
        var maxDelaySeconds = Math.Max(
            MinimumMaxBackoff.TotalSeconds,
            baseInterval.TotalSeconds * MaxBackoffMultiplier);

        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, maxDelaySeconds));
    }

    internal TimeSpan GetInitialDelay()
        => TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds));

    internal TimeSpan GetPollIntervalWithJitter()
    {
        int jitterSeconds = Math.Clamp(_options.PollJitterSeconds, 0, 24 * 60 * 60);
        var baseInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        return jitterSeconds == 0
            ? baseInterval
            : baseInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, jitterSeconds + 1));
    }

    internal TimeSpan GetNextDelay(int consecutiveFailures)
    {
        var baseDelay = GetPollIntervalWithJitter();
        return CalculateDelay(baseDelay, consecutiveFailures);
    }
}
