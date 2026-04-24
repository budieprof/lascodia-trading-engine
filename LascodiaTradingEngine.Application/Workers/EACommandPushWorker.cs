using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Polls for un-acknowledged EA commands and pushes them to connected EA instances via WebSocket.
/// Reduces the latency between command creation and EA execution from the EA's poll interval
/// (~1-2s) to near-zero. Falls back gracefully to poll-based delivery when WebSocket is
/// unavailable — the EA's existing GET /ea/commands endpoint remains the source of truth.
/// Only active when <see cref="WebSocketBridgeOptions.Enabled"/> is <c>true</c>.
/// </summary>
public sealed class EACommandPushWorker : BackgroundService
{
    internal const string WorkerName = nameof(EACommandPushWorker);

    private const int PollIntervalMs = 500;
    private const int MaxBackoffMs = 30_000;
    private const int PendingCommandPageSize = 50;
    private const int MaxPagesPerCycle = 10;
    internal const int MaxPushesPerCycle = 50;
    internal const int ExpiryBatchSize = 50;
    private static readonly TimeSpan BacklogSampleInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<EACommandPushWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebSocketBridge _wsBridge;
    private readonly WebSocketBridgeOptions _wsOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly ILatencySlaRecorder? _latencySlaRecorder;
    private int _consecutiveFailures;
    private DateTimeOffset _nextBacklogSampleAtUtc = DateTimeOffset.MinValue;
    private EACommandScanCursor? _scanCursor;

    /// <summary>
    /// Tracks commands that have already been pushed successfully.  The stored retry
    /// count lets the worker detect an intentional re-queue (TimedOut / Deferred ACK)
    /// and push the command again immediately without blindly re-sending duplicate
    /// executions while the original attempt is still awaiting acknowledgement.
    /// </summary>
    private readonly Dictionary<long, int> _pushedRetryCounts = [];

    public EACommandPushWorker(
        ILogger<EACommandPushWorker> logger,
        IServiceScopeFactory scopeFactory,
        IWebSocketBridge wsBridge,
        WebSocketBridgeOptions wsOptions,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        ILatencySlaRecorder? latencySlaRecorder = null)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _wsBridge     = wsBridge;
        _wsOptions    = wsOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _latencySlaRecorder = latencySlaRecorder;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_wsOptions.Enabled)
        {
            _logger.LogInformation("{Worker}: WebSocket bridge disabled — exiting", WorkerName);
            return;
        }

        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Pushes pending EA commands to currently connected bridge sessions, while falling back to poll-based delivery and expiring commands past the 24-hour TTL.",
            TimeSpan.FromMilliseconds(PollIntervalMs));

        _logger.LogInformation("{Worker} starting", WorkerName);

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
                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;

                    if (result.BacklogDepthSample.HasValue)
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.BacklogDepthSample.Value);

                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));

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
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "ea_command_push_cycle"));
                    _logger.LogError(ex,
                        "{Worker} error (failure #{Count})",
                        WorkerName,
                        _consecutiveFailures);
                }

                try
                {
                    await Task.Delay(CalculateDelay(_consecutiveFailures), _timeProvider, stoppingToken);
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
            _logger.LogInformation("{Worker} stopped", WorkerName);
        }
    }

    /// <summary>
    /// Commands older than this are auto-expired to prevent indefinite accumulation
    /// when the target EA instance never reconnects.
    /// </summary>
    private static readonly TimeSpan CommandExpiryThreshold = TimeSpan.FromHours(24);

    internal static TimeSpan CalculateDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return TimeSpan.FromMilliseconds(PollIntervalMs);

        var backoffMs = Math.Min(
            (double)PollIntervalMs * Math.Pow(2, consecutiveFailures - 1),
            MaxBackoffMs);

        return TimeSpan.FromMilliseconds(backoffMs);
    }

    internal async Task<EACommandPushCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var now = _timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        int expiredCount = await ExpireStaleCommandsAsync(writeContext, nowUtc, ct);
        if (expiredCount > 0)
            _metrics?.EaCommandsExpired.Add(expiredCount);

        int pushedCount = 0;
        int pushFailureCount = 0;
        int requeuedRepushCount = 0;
        var connectedInstanceIds = _wsBridge.GetConnectedInstanceIds()
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        int? backlogDepthSample = await SampleBacklogDepthAsync(db, connectedInstanceIds, now, ct);

        if (connectedInstanceIds.Length > 0)
        {
            var candidates = await LoadPushCandidatesAsync(db, connectedInstanceIds, nowUtc, ct);

            foreach (var command in candidates)
            {
                if (ct.IsCancellationRequested)
                    break;

                string commandTypeTag = command.CommandType.ToString();
                double queueLatencyMs = Math.Max(
                    0,
                    (_timeProvider.GetUtcNow().UtcDateTime - command.CreatedAt).TotalMilliseconds);
                var pushStarted = Stopwatch.GetTimestamp();
                var success = await _wsBridge.PushCommandAsync(command.TargetInstanceId, command, ct);
                double pushDurationMs = Stopwatch.GetElapsedTime(pushStarted).TotalMilliseconds;
                if (!success)
                {
                    _pushedRetryCounts.Remove(command.Id);
                    pushFailureCount++;
                    _metrics?.EaCommandPushFailures.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", "bridge_returned_false"),
                        new KeyValuePair<string, object?>("command_type", commandTypeTag));
                    continue;
                }

                if (_pushedRetryCounts.TryGetValue(command.Id, out var previousRetryCount)
                    && command.RetryCount > previousRetryCount)
                {
                    requeuedRepushCount++;
                }

                _pushedRetryCounts[command.Id] = command.RetryCount;
                pushedCount++;
                _healthMonitor?.RecordQueueLatency(WorkerName, (long)queueLatencyMs);
                _healthMonitor?.RecordExecutionDuration(WorkerName, (long)pushDurationMs);
                _latencySlaRecorder?.RecordSample(
                    LatencySlaSegments.EaPollToSubmit,
                    (long)Math.Round(queueLatencyMs + pushDurationMs));
                _metrics?.EaCommandsPushed.Add(
                    1,
                    new KeyValuePair<string, object?>("command_type", commandTypeTag));
                _metrics?.EaCommandQueueLatencyMs.Record(
                    queueLatencyMs,
                    new KeyValuePair<string, object?>("command_type", commandTypeTag));
                _metrics?.EaCommandPushSendDurationMs.Record(
                    pushDurationMs,
                    new KeyValuePair<string, object?>("command_type", commandTypeTag));
            }
        }

        await EvictCompletedPushStateAsync(db, nowUtc, ct);

        if (expiredCount > 0)
        {
            _logger.LogWarning(
                "{Worker}: expired {Count} stale command(s) older than {Hours}h",
                WorkerName,
                expiredCount,
                CommandExpiryThreshold.TotalHours);
        }

        if (pushedCount > 0)
        {
            _logger.LogDebug(
                "{Worker}: pushed {Count} command(s) via WebSocket across {ConnectedCount} connected instance(s) (requeuedRepushes={RequeuedRepushes}, failures={Failures})",
                WorkerName,
                pushedCount,
                connectedInstanceIds.Length,
                requeuedRepushCount,
                pushFailureCount);
        }

        return new EACommandPushCycleResult(
            expiredCount,
            pushedCount,
            connectedInstanceIds.Length,
            backlogDepthSample,
            pushFailureCount,
            requeuedRepushCount);
    }

    private async Task<int> ExpireStaleCommandsAsync(
        IWriteApplicationDbContext writeContext,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var db = writeContext.GetDbContext();
        var expiryCutoff = nowUtc - CommandExpiryThreshold;

        var expiredCommands = await db.Set<EACommand>()
            .Where(c => !c.Acknowledged
                     && !c.IsDeleted
                     && c.CreatedAt < expiryCutoff)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(ExpiryBatchSize)
            .ToListAsync(ct);

        if (expiredCommands.Count == 0)
            return 0;

        foreach (var expired in expiredCommands)
        {
            expired.FinalizeAck(
                isRetryable: false,
                success: false,
                result: $"Expired: pending for >{CommandExpiryThreshold.TotalHours}h");
            _pushedRetryCounts.Remove(expired.Id);
        }

        await writeContext.SaveChangesAsync(ct);
        return expiredCommands.Count;
    }

    private async Task<List<EACommand>> LoadPushCandidatesAsync(
        DbContext db,
        string[] connectedInstanceIds,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var candidates = new List<EACommand>(MaxPushesPerCycle);
        var selectedIds = new HashSet<long>();

        var requeuedCandidates = await LoadRequeuedCandidatesAsync(
            db,
            connectedInstanceIds,
            nowUtc,
            MaxPushesPerCycle,
            ct);

        foreach (var command in requeuedCandidates)
        {
            if (selectedIds.Add(command.Id))
                candidates.Add(command);
        }

        bool wrappedToStart = false;
        for (int page = 0; page < MaxPagesPerCycle && candidates.Count < MaxPushesPerCycle; page++)
        {
            var pageCommands = await LoadCandidatePageAsync(
                db,
                connectedInstanceIds,
                nowUtc,
                _scanCursor,
                ct);

            if (pageCommands.Count == 0)
            {
                if (_scanCursor.HasValue && !wrappedToStart)
                {
                    _scanCursor = null;
                    wrappedToStart = true;
                    continue;
                }

                break;
            }

            var lastScanned = pageCommands[^1];
            _scanCursor = new EACommandScanCursor(lastScanned.CreatedAt, lastScanned.Id);

            foreach (var command in pageCommands)
            {
                if (selectedIds.Contains(command.Id) || ShouldSkipPush(command))
                    continue;

                candidates.Add(command);
                selectedIds.Add(command.Id);
                if (candidates.Count >= MaxPushesPerCycle)
                    break;
            }
        }

        return candidates;
    }

    private bool ShouldSkipPush(EACommand command)
        => _pushedRetryCounts.TryGetValue(command.Id, out var pushedRetryCount)
        && pushedRetryCount == command.RetryCount;

    private async Task<List<EACommand>> LoadRequeuedCandidatesAsync(
        DbContext db,
        string[] connectedInstanceIds,
        DateTime nowUtc,
        int maxCandidates,
        CancellationToken ct)
    {
        if (_pushedRetryCounts.Count == 0 || maxCandidates <= 0)
            return [];

        var expiryCutoff = nowUtc - CommandExpiryThreshold;
        var candidates = new List<EACommand>(Math.Min(maxCandidates, _pushedRetryCounts.Count));

        foreach (var chunk in _pushedRetryCounts.Keys.Chunk(200))
        {
            var trackedCommands = await db.Set<EACommand>()
                .AsNoTracking()
                .Where(c => !c.Acknowledged
                         && !c.IsDeleted
                         && c.RetryCount <= EACommand.MaxRetries
                         && c.CreatedAt >= expiryCutoff
                         && connectedInstanceIds.Contains(c.TargetInstanceId)
                         && chunk.Contains(c.Id))
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .ToListAsync(ct);

            foreach (var command in trackedCommands)
            {
                if (_pushedRetryCounts.TryGetValue(command.Id, out var pushedRetryCount)
                    && command.RetryCount > pushedRetryCount)
                {
                    candidates.Add(command);
                    if (candidates.Count >= maxCandidates)
                        break;
                }
            }

            if (candidates.Count >= maxCandidates)
                break;
        }

        if (candidates.Count <= 1)
            return candidates;

        return candidates
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();
    }

    private static Task<List<EACommand>> LoadCandidatePageAsync(
        DbContext db,
        string[] connectedInstanceIds,
        DateTime nowUtc,
        EACommandScanCursor? cursor,
        CancellationToken ct)
    {
        var expiryCutoff = nowUtc - CommandExpiryThreshold;
        var query = db.Set<EACommand>()
            .AsNoTracking()
            .Where(c => !c.Acknowledged
                     && !c.IsDeleted
                     && c.RetryCount <= EACommand.MaxRetries
                     && c.CreatedAt >= expiryCutoff
                     && connectedInstanceIds.Contains(c.TargetInstanceId));

        if (cursor.HasValue)
        {
            var createdAt = cursor.Value.CreatedAt;
            var id = cursor.Value.Id;
            query = query.Where(c => c.CreatedAt > createdAt
                                  || (c.CreatedAt == createdAt && c.Id > id));
        }

        return query
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(PendingCommandPageSize)
            .ToListAsync(ct);
    }

    private async Task<int?> SampleBacklogDepthAsync(
        DbContext db,
        string[] connectedInstanceIds,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (_healthMonitor is null && _metrics is null)
            return null;

        if (now < _nextBacklogSampleAtUtc)
            return null;

        _nextBacklogSampleAtUtc = now + BacklogSampleInterval;

        if (connectedInstanceIds.Length == 0)
        {
            _metrics?.EaCommandPushBacklogDepth.Record(0);
            return 0;
        }

        var expiryCutoff = now.UtcDateTime - CommandExpiryThreshold;
        int backlogDepth = await db.Set<EACommand>()
            .AsNoTracking()
            .CountAsync(c => !c.Acknowledged
                          && !c.IsDeleted
                          && c.RetryCount <= EACommand.MaxRetries
                          && c.CreatedAt >= expiryCutoff
                          && connectedInstanceIds.Contains(c.TargetInstanceId), ct);

        _metrics?.EaCommandPushBacklogDepth.Record(backlogDepth);
        return backlogDepth;
    }

    private async Task EvictCompletedPushStateAsync(
        DbContext db,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (_pushedRetryCounts.Count == 0)
            return;

        var trackedIds = _pushedRetryCounts.Keys.ToArray();
        var stillPendingIds = new HashSet<long>();
        var expiryCutoff = nowUtc - CommandExpiryThreshold;

        foreach (var chunk in trackedIds.Chunk(200))
        {
            var chunkIds = await db.Set<EACommand>()
                .AsNoTracking()
                .Where(c => !c.Acknowledged
                         && !c.IsDeleted
                         && c.CreatedAt >= expiryCutoff
                         && chunk.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(ct);

            stillPendingIds.UnionWith(chunkIds);
        }

        foreach (var commandId in trackedIds)
        {
            if (!stillPendingIds.Contains(commandId))
                _pushedRetryCounts.Remove(commandId);
        }
    }
}

internal readonly record struct EACommandPushCycleResult(
    int ExpiredCount,
    int PushedCount,
    int ConnectedInstanceCount,
    int? BacklogDepthSample,
    int PushFailureCount,
    int RequeuedRepushCount);

internal readonly record struct EACommandScanCursor(
    DateTime CreatedAt,
    long Id);
