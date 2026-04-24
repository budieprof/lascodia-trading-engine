using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that periodically hard-deletes retained <see cref="DeadLetterEvent"/>
/// records older than a configurable retention period.
///
/// <para>
/// <b>Configuration (via EngineConfig table):</b>
/// <list type="bullet">
///   <item><description>
///     <c>DeadLetter:CleanupIntervalHours</c> — how often the cleanup runs (default 24 hours).
///   </description></item>
///   <item><description>
///     <c>DeadLetter:RetentionDays</c> — resolved or soft-deleted records older than this
///     many days are hard-deleted (default 30).
///   </description></item>
///   <item><description>
///     <c>DeadLetter:CleanupBatchSize</c> — maximum number of rows deleted per server-side
///     batch (default 1000).
///   </description></item>
///   <item><description>
///     <c>DeadLetter:CleanupLockTimeoutSeconds</c> — distributed-lock acquisition timeout
///     when running in multi-instance deployments (default 5 seconds).
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// Unresolved dead letters are intentionally preserved even after they age past the retention
/// window so operators do not lose incidents that still require investigation or replay.
/// Resolved and soft-deleted rows are deleted in bounded <c>ExecuteDeleteAsync</c> batches
/// without loading entities into memory.
/// </para>
/// </summary>
public sealed class DeadLetterCleanupWorker : BackgroundService
{
    internal const string WorkerName = nameof(DeadLetterCleanupWorker);

    private const string DistributedLockKey = "workers:dead-letter-cleanup:cycle";
    private const string CK_IntervalHours = "DeadLetter:CleanupIntervalHours";
    private const string CK_RetentionDays = "DeadLetter:RetentionDays";
    private const string CK_BatchSize = "DeadLetter:CleanupBatchSize";
    private const string CK_LockTimeoutSeconds = "DeadLetter:CleanupLockTimeoutSeconds";

    private const int DefaultIntervalHours = 24;
    private const int DefaultRetentionDays = 30;
    private const int DefaultBatchSize = 1000;
    private const int DefaultLockTimeoutSeconds = 5;

    private const int MinIntervalHours = 1;
    private const int MaxIntervalHours = 24 * 30;
    private const int MinRetentionDays = 1;
    private const int MaxRetentionDays = 3650;
    private const int MinBatchSize = 1;
    private const int MaxBatchSize = 10_000;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 60;
    internal const int MaxBatchesPerCycle = 256;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeadLetterCleanupWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public DeadLetterCleanupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DeadLetterCleanupWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Purges resolved or soft-deleted dead letters after retention while preserving unresolved incidents for investigation.",
            TimeSpan.FromHours(DefaultIntervalHours));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var config = DeadLetterCleanupConfig.Default;
                var cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    config = result.Config;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(
                        WorkerName,
                        (int)Math.Min(
                            int.MaxValue,
                            result.ExpiredUnresolvedCount + result.RemainingEligibleDeletionCount));
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
                    else
                    {
                        if (result.TotalDeleted > 0)
                        {
                            _logger.LogInformation(
                                "{Worker}: purged {Deleted} retained dead-letter row(s) older than {Days} day(s) in {Batches} batch(es) (resolved={ResolvedDeleted}, softDeleted={SoftDeletedDeleted}, remainingEligible={RemainingEligible}).",
                                WorkerName,
                                result.TotalDeleted,
                                result.Config.RetentionDays,
                                result.BatchesProcessed,
                                result.DeletedResolvedCount,
                                result.DeletedSoftDeletedCount,
                                result.RemainingEligibleDeletionCount);
                        }
                        else if (result.ExpiredUnresolvedCount == 0 && result.RemainingEligibleDeletionCount == 0)
                        {
                            _logger.LogDebug(
                                "{Worker}: no retained dead-letter rows older than {Days} day(s) to purge.",
                                WorkerName,
                                result.Config.RetentionDays);
                        }

                        if (result.RemainingEligibleDeletionCount > 0)
                        {
                            _logger.LogWarning(
                                "{Worker}: cycle ended with {Count} retained dead-letter row(s) still eligible for purge; the bounded sweep will continue next cycle{LimitSuffix}.",
                                WorkerName,
                                result.RemainingEligibleDeletionCount,
                                result.HitBatchLimit ? " after reaching the per-cycle batch cap" : string.Empty);
                        }

                        if (result.ExpiredUnresolvedCount > 0)
                        {
                            _logger.LogWarning(
                                "{Worker}: preserved {Count} unresolved dead-letter row(s) older than {Days} day(s); manual investigation or replay is still required.",
                                WorkerName,
                                result.ExpiredUnresolvedCount,
                                result.Config.RetentionDays);
                        }
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
                        new KeyValuePair<string, object?>("reason", "dead_letter_cleanup"));
                    _logger.LogError(
                        ex,
                        "{Worker}: cleanup cycle failed (consecutive failures: {Failures}).",
                        WorkerName,
                        _consecutiveFailures);
                }

                try
                {
                    await Task.Delay(
                        CalculateDelay(config.PollInterval, _consecutiveFailures),
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

    internal async Task<DeadLetterCleanupCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var config = await LoadConfigAsync(readCtx, ct);

        if (_distributedLock is null)
        {
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate cleanup sweeps are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var timeout = TimeSpan.FromSeconds(config.LockTimeoutSeconds);
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, timeout, ct);
            if (cycleLock is null)
                return DeadLetterCleanupCycleResult.Skipped(config, "lock_busy");

            await using (cycleLock)
            {
                return await RunCycleAsync(scope, config, ct);
            }
        }

        return await RunCycleAsync(scope, config, ct);
    }

    internal async Task<DeadLetterCleanupCycleResult> RunCycleAsync(
        IServiceScope scope,
        DeadLetterCleanupConfig config,
        CancellationToken ct)
    {
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-config.RetentionDays);

        long expiredUnresolvedCount = await readCtx.Set<DeadLetterEvent>()
            .IgnoreQueryFilters()
            .LongCountAsync(d => !d.IsDeleted && !d.IsResolved && d.DeadLetteredAt < cutoff, ct);

        long totalDeleted = 0;
        long deletedResolvedCount = 0;
        long deletedSoftDeletedCount = 0;
        int batchesProcessed = 0;

        for (int i = 0; i < MaxBatchesPerCycle; i++)
        {
            ct.ThrowIfCancellationRequested();

            var batchRows = await writeCtx.Set<DeadLetterEvent>()
                .IgnoreQueryFilters()
                .Where(d => d.DeadLetteredAt < cutoff && (d.IsResolved || d.IsDeleted))
                .OrderBy(d => d.DeadLetteredAt)
                .ThenBy(d => d.Id)
                .Select(d => new
                {
                    d.Id,
                    d.IsDeleted
                })
                .Take(config.BatchSize)
                .ToListAsync(ct);

            if (batchRows.Count == 0)
                break;

            var batchIds = batchRows.Select(r => r.Id).ToList();
            int deletedThisPass = await writeCtx.Set<DeadLetterEvent>()
                .IgnoreQueryFilters()
                .Where(d => batchIds.Contains(d.Id))
                .ExecuteDeleteAsync(ct);

            if (deletedThisPass == 0)
                break;

            HashSet<long>? deletedIds = null;
            if (deletedThisPass != batchRows.Count)
            {
                var remainingIds = await writeCtx.Set<DeadLetterEvent>()
                    .IgnoreQueryFilters()
                    .Where(d => batchIds.Contains(d.Id))
                    .Select(d => d.Id)
                    .ToHashSetAsync(ct);

                deletedIds = batchRows
                    .Select(r => r.Id)
                    .Where(id => !remainingIds.Contains(id))
                    .ToHashSet();

                deletedThisPass = deletedIds.Count;
            }

            int deletedSoftDeletedThisPass = deletedIds is null
                ? batchRows.Count(r => r.IsDeleted)
                : batchRows.Count(r => r.IsDeleted && deletedIds.Contains(r.Id));

            int deletedResolvedThisPass = deletedIds is null
                ? batchRows.Count - deletedSoftDeletedThisPass
                : batchRows.Count(r => !r.IsDeleted && deletedIds.Contains(r.Id));

            if (deletedThisPass == 0)
                break;

            totalDeleted += deletedThisPass;
            deletedResolvedCount += deletedResolvedThisPass;
            deletedSoftDeletedCount += deletedSoftDeletedThisPass;
            batchesProcessed++;
            if (deletedResolvedThisPass > 0)
            {
                _metrics?.RetentionRowsDeleted.Add(
                    deletedResolvedThisPass,
                    new KeyValuePair<string, object?>("table", nameof(DeadLetterEvent)),
                    new KeyValuePair<string, object?>("category", "resolved"));
            }

            if (deletedSoftDeletedThisPass > 0)
            {
                _metrics?.RetentionRowsDeleted.Add(
                    deletedSoftDeletedThisPass,
                    new KeyValuePair<string, object?>("table", nameof(DeadLetterEvent)),
                    new KeyValuePair<string, object?>("category", "soft_deleted"));
            }

            if (batchIds.Count < config.BatchSize)
                break;
        }

        long remainingEligibleDeletionCount = await readCtx.Set<DeadLetterEvent>()
            .IgnoreQueryFilters()
            .LongCountAsync(d => d.DeadLetteredAt < cutoff && (d.IsResolved || d.IsDeleted), ct);

        return new DeadLetterCleanupCycleResult(
            config,
            totalDeleted,
            deletedResolvedCount,
            deletedSoftDeletedCount,
            expiredUnresolvedCount,
            remainingEligibleDeletionCount,
            batchesProcessed,
            HitBatchLimit: remainingEligibleDeletionCount > 0 && batchesProcessed >= MaxBatchesPerCycle,
            SkippedReason: null);
    }

    internal async Task<DeadLetterCleanupConfig> LoadConfigAsync(
        DbContext ctx,
        CancellationToken ct)
    {
        int intervalHours = await ReadIntConfigAsync(ctx, CK_IntervalHours, DefaultIntervalHours, ct);
        int retentionDays = await ReadIntConfigAsync(ctx, CK_RetentionDays, DefaultRetentionDays, ct);
        int batchSize = await ReadIntConfigAsync(ctx, CK_BatchSize, DefaultBatchSize, ct);
        int lockTimeoutSeconds = await ReadIntConfigAsync(ctx, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds, ct);

        return NormalizeConfig(intervalHours, retentionDays, batchSize, lockTimeoutSeconds);
    }

    internal static DeadLetterCleanupConfig NormalizeConfig(
        int intervalHours,
        int retentionDays,
        int batchSize,
        int lockTimeoutSeconds)
        => new(
            IntervalHours: Math.Clamp(intervalHours, MinIntervalHours, MaxIntervalHours),
            RetentionDays: Math.Clamp(retentionDays, MinRetentionDays, MaxRetentionDays),
            BatchSize: Math.Clamp(batchSize, MinBatchSize, MaxBatchSize),
            LockTimeoutSeconds: Math.Clamp(lockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds));

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval;

        var exponent = Math.Min(consecutiveFailures - 1, 8);
        var retrySeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(retrySeconds, MaxRetryDelay.TotalSeconds));
    }

    private static async Task<int> ReadIntConfigAsync(
        DbContext ctx,
        string key,
        int defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (string.IsNullOrWhiteSpace(entry?.Value))
            return defaultValue;

        return int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    internal readonly record struct DeadLetterCleanupConfig(
        int IntervalHours,
        int RetentionDays,
        int BatchSize,
        int LockTimeoutSeconds)
    {
        public static DeadLetterCleanupConfig Default => new(
            DefaultIntervalHours,
            DefaultRetentionDays,
            DefaultBatchSize,
            DefaultLockTimeoutSeconds);

        public TimeSpan PollInterval => TimeSpan.FromHours(IntervalHours);
    }

    internal readonly record struct DeadLetterCleanupCycleResult(
        DeadLetterCleanupConfig Config,
        long TotalDeleted,
        long DeletedResolvedCount,
        long DeletedSoftDeletedCount,
        long ExpiredUnresolvedCount,
        long RemainingEligibleDeletionCount,
        int BatchesProcessed,
        bool HitBatchLimit,
        string? SkippedReason)
    {
        public static DeadLetterCleanupCycleResult Skipped(
            DeadLetterCleanupConfig config,
            string reason)
            => new(config, 0, 0, 0, 0, 0, 0, false, reason);
    }
}
