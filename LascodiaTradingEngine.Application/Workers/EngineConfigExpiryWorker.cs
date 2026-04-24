using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically retires ephemeral <see cref="EngineConfig"/> rows whose value is an expired
/// timestamp, and prunes stale <c>MLMetrics:*</c> blocks whose <c>:LastUpdated</c> marker
/// is older than the supported freshness window.
///
/// <para>
/// The worker intentionally targets only explicit expiry-managed keys such as
/// <c>MLCooldown:{Symbol}:{Tf}:ExpiresAt</c> and
/// <c>MLDrift:{Symbol}:{Tf}:AdwinDriftDetected</c>. Other timestamp-valued config rows
/// (for example <c>*:DetectedAt</c> and <c>*:LastChecked</c>) are operational state and
/// must not be deleted just because they refer to a past moment in time.
/// </para>
/// </summary>
public sealed class EngineConfigExpiryWorker : BackgroundService
{
    internal const string WorkerName = nameof(EngineConfigExpiryWorker);

    private const string CK_PollSecs = "EngineConfig:ExpiryPollIntervalSeconds";
    private const string DistributedLockKey = "workers:engine-config-expiry:cycle";
    private const string MetricsPrefix = "MLMetrics:";
    private const string MetricsLastUpdatedSuffix = ":LastUpdated";
    private const string ExpirySuffix = ":ExpiresAt";
    private const string AdwinDriftPrefix = "MLDrift:";
    private const string AdwinDriftSuffix = ":AdwinDriftDetected";

    private const int DefaultPollIntervalSeconds = 21600; // 6 hours
    private const int MinPollIntervalSeconds = 60;
    private const int MaxPollIntervalSeconds = 24 * 60 * 60;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MetricsStaleThreshold = TimeSpan.FromHours(1);
    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EngineConfigExpiryWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public EngineConfigExpiryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EngineConfigExpiryWorker> logger,
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
            "Soft-deletes explicit expiry-managed EngineConfig rows and prunes stale MLMetrics blocks without touching non-expiry timestamp state.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, 0);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.EngineConfigExpiryCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else if (result.ExpiredEntryCount > 0 || result.StaleMetricsEntryCount > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: cleaned {Expired} expired config row(s) and pruned {MetricsEntries} stale MLMetrics row(s) across {MetricsBlocks} block(s).",
                            WorkerName,
                            result.ExpiredEntryCount,
                            result.StaleMetricsEntryCount,
                            result.StaleMetricsBlockCount);
                    }
                    else
                    {
                        _logger.LogDebug("{Worker}: no managed expiry rows or stale MLMetrics blocks found.", WorkerName);
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
                        new KeyValuePair<string, object?>("reason", "engine_config_expiry_cycle"));
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

    internal async Task<EngineConfigExpiryCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (_distributedLock is null)
        {
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate expiry sweeps are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
                return EngineConfigExpiryCycleResult.Skipped(settings, "lock_busy");

            await using (cycleLock)
            {
                return await RunCycleCoreAsync(db, settings, ct);
            }
        }

        return await RunCycleCoreAsync(db, settings, ct);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<EngineConfigExpiryCycleResult> RunCycleCoreAsync(
        DbContext db,
        EngineConfigExpirySettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        int expiredEntryCount = await CleanExpiredEntriesAsync(db, nowUtc, ct);
        var metricsCleanup = await CleanStaleMetricsBlocksAsync(db, nowUtc, ct);

        if (expiredEntryCount > 0)
            _metrics?.EngineConfigExpiredEntries.Add(expiredEntryCount);

        if (metricsCleanup.BlockCount > 0)
            _metrics?.EngineConfigStaleMetricsBlocksPruned.Add(metricsCleanup.BlockCount);

        if (metricsCleanup.EntryCount > 0)
            _metrics?.EngineConfigStaleMetricsEntriesPruned.Add(metricsCleanup.EntryCount);

        return new EngineConfigExpiryCycleResult(
            settings,
            ExpiredEntryCount: expiredEntryCount,
            StaleMetricsBlockCount: metricsCleanup.BlockCount,
            StaleMetricsEntryCount: metricsCleanup.EntryCount,
            SkippedReason: null);
    }

    private async Task<EngineConfigExpirySettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        int configuredPollSeconds = await GetConfigAsync(db, CK_PollSecs, DefaultPollIntervalSeconds, ct);
        int pollSeconds = Clamp(configuredPollSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds);

        if (configuredPollSeconds != pollSeconds)
        {
            _logger.LogDebug(
                "{Worker}: clamped invalid poll interval {Configured}s to {Effective}s.",
                WorkerName,
                configuredPollSeconds,
                pollSeconds);
        }

        return new EngineConfigExpirySettings(TimeSpan.FromSeconds(pollSeconds));
    }

    private async Task<int> CleanExpiredEntriesAsync(
        DbContext db,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var candidates = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted
                     && c.Value != null
                     && c.Value != ""
                     && (c.Key.EndsWith(ExpirySuffix)
                      || (c.Key.StartsWith(AdwinDriftPrefix) && c.Key.EndsWith(AdwinDriftSuffix))))
            .Select(c => new { c.Id, c.Key, c.Value })
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return 0;

        var expiredIds = new List<long>();

        foreach (var entry in candidates)
        {
            if (!TryParseTimestamp(entry.Value!, out var parsedUtc))
                continue;

            if (parsedUtc > nowUtc)
                continue;

            expiredIds.Add(entry.Id);
            _logger.LogDebug(
                "{Worker}: expiring managed key '{Key}' (value={Value}, parsed={Parsed:u}).",
                WorkerName,
                entry.Key,
                entry.Value,
                parsedUtc);
        }

        if (expiredIds.Count == 0)
            return 0;

        return await db.Set<EngineConfig>()
            .Where(c => expiredIds.Contains(c.Id) && !c.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.IsDeleted, true)
                .SetProperty(c => c.LastUpdatedAt, nowUtc), ct);
    }

    private async Task<MetricsCleanupResult> CleanStaleMetricsBlocksAsync(
        DbContext db,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var lastUpdatedEntries = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted
                     && c.Value != null
                     && c.Value != ""
                     && c.Key.StartsWith(MetricsPrefix)
                     && c.Key.EndsWith(MetricsLastUpdatedSuffix))
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        if (lastUpdatedEntries.Count == 0)
            return MetricsCleanupResult.Empty;

        var stalePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in lastUpdatedEntries)
        {
            if (!TryParseTimestamp(entry.Value!, out var parsedUtc))
                continue;

            if (nowUtc - parsedUtc <= MetricsStaleThreshold)
                continue;

            var prefix = ExtractMetricsPrefix(entry.Key);
            if (prefix is null)
                continue;

            stalePrefixes.Add(prefix);
            _logger.LogDebug(
                "{Worker}: MLMetrics block '{Prefix}' is stale (last updated {LastUpdated:u}, age={Age}).",
                WorkerName,
                prefix,
                parsedUtc,
                nowUtc - parsedUtc);
        }

        if (stalePrefixes.Count == 0)
            return MetricsCleanupResult.Empty;

        int totalCleaned = 0;
        foreach (var prefix in stalePrefixes)
        {
            ct.ThrowIfCancellationRequested();

            int cleaned = await db.Set<EngineConfig>()
                .Where(c => !c.IsDeleted && c.Key.StartsWith(prefix))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.IsDeleted, true)
                    .SetProperty(c => c.LastUpdatedAt, nowUtc), ct);

            totalCleaned += cleaned;
        }

        return new MetricsCleanupResult(
            BlockCount: stalePrefixes.Count,
            EntryCount: totalCleaned);
    }

    private static string? ExtractMetricsPrefix(string key)
    {
        int suffixIndex = key.LastIndexOf(MetricsLastUpdatedSuffix, StringComparison.Ordinal);
        if (suffixIndex <= 0)
            return null;

        return key[..suffixIndex] + ":";
    }

    private static bool TryParseTimestamp(string rawValue, out DateTime utcValue)
    {
        if (DateTime.TryParse(
                rawValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            utcValue = NormalizeUtc(parsed);
            return true;
        }

        utcValue = default;
        return false;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static async Task<int> GetConfigAsync(
        DbContext db,
        string key,
        int defaultValue,
        CancellationToken ct)
    {
        var entry = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null)
            return defaultValue;

        return int.TryParse(entry.Value, out var parsed) ? parsed : defaultValue;
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    internal readonly record struct EngineConfigExpiryCycleResult(
        EngineConfigExpirySettings Settings,
        int ExpiredEntryCount,
        int StaleMetricsBlockCount,
        int StaleMetricsEntryCount,
        string? SkippedReason)
    {
        public static EngineConfigExpiryCycleResult Skipped(
            EngineConfigExpirySettings settings,
            string reason)
            => new(
                settings,
                ExpiredEntryCount: 0,
                StaleMetricsBlockCount: 0,
                StaleMetricsEntryCount: 0,
                SkippedReason: reason);
    }

    internal readonly record struct EngineConfigExpirySettings(TimeSpan PollInterval);

    private readonly record struct MetricsCleanupResult(int BlockCount, int EntryCount)
    {
        public static readonly MetricsCleanupResult Empty = new(0, 0);
    }
}
