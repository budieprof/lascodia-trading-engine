using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// In-memory health monitor that collects cycle metrics from all background workers.
/// Persists snapshots periodically and exposes current state for the /health/workers endpoint.
/// Thread-safe: workers call Record* methods concurrently.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class WorkerHealthMonitor : IWorkerHealthMonitor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertDispatcher _alertDispatcher;
    private readonly ILogger<WorkerHealthMonitor> _logger;

    /// <summary>Per-worker health state, updated in real-time by worker calls.</summary>
    private readonly ConcurrentDictionary<string, WorkerState> _state = new();

    /// <summary>
    /// Tracks which workers have already had a crash alert dispatched to prevent
    /// duplicate alert spam. Cleared when the worker resumes (heartbeat received).
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _alertedWorkers = new();

    private class WorkerState
    {
        public bool IsRunning { get; set; } = true;
        public DateTime? LastSuccessAt { get; set; }
        public DateTime? LastErrorAt { get; set; }
        public string? LastErrorMessage { get; set; }
        public int ConsecutiveFailures;
        public int BacklogDepth { get; set; }
        public long LastCycleDurationMs { get; set; }
        public long LastQueueLatencyMs { get; set; }
        public long LastExecutionDurationMs { get; set; }

        // Sliding window of recent cycle durations (last 60 entries ~= last hour at 60s intervals)
        public readonly ConcurrentQueue<long> RecentDurations = new();
        public readonly ConcurrentQueue<long> RecentQueueLatencies = new();
        public readonly ConcurrentQueue<long> RecentExecutionDurations = new();
        // Volatile int fields + Interlocked for thread-safe increment/reset across worker threads
        public int SuccessesLastHour;
        public int ErrorsLastHour;
        public int RetriesLastHour;
        public int RecoveriesLastHour;
        public int ConfiguredIntervalSeconds { get; set; } = 60;

        // ── Static metadata for observability ────────────────────────────────
        public string? Purpose;
        public int ExpectedIntervalSeconds;
        public bool IsStopped;
    }

    public WorkerHealthMonitor(
        IServiceScopeFactory scopeFactory,
        IAlertDispatcher alertDispatcher,
        ILogger<WorkerHealthMonitor> logger)
    {
        _scopeFactory    = scopeFactory;
        _alertDispatcher = alertDispatcher;
        _logger          = logger;
    }

    public void RecordCycleSuccess(string workerName, long durationMs)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        state.LastSuccessAt = DateTime.UtcNow;
        state.LastCycleDurationMs = durationMs;
        state.IsStopped = false; // Reset stopped flag — worker is clearly alive
        _alertedWorkers.TryRemove(workerName, out _); // Clear crash alert dedup — worker recovered
        Interlocked.Exchange(ref state.ConsecutiveFailures, 0);
        Interlocked.Increment(ref state.SuccessesLastHour);

        state.RecentDurations.Enqueue(durationMs);
        while (state.RecentDurations.Count > 60)
            state.RecentDurations.TryDequeue(out _);
    }

    public void RecordCycleFailure(string workerName, string errorMessage)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        state.IsStopped = false;
        state.LastErrorAt = DateTime.UtcNow;
        state.LastErrorMessage = errorMessage.Length > 500 ? errorMessage[..500] : errorMessage;
        Interlocked.Increment(ref state.ConsecutiveFailures);
        Interlocked.Increment(ref state.ErrorsLastHour);
    }

    public void RecordBacklogDepth(string workerName, int depth)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        state.BacklogDepth = depth;
    }

    public void RecordQueueLatency(string workerName, long durationMs)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        state.LastQueueLatencyMs = Math.Max(0, durationMs);
        state.RecentQueueLatencies.Enqueue(state.LastQueueLatencyMs);
        while (state.RecentQueueLatencies.Count > 60)
            state.RecentQueueLatencies.TryDequeue(out _);
    }

    public void RecordExecutionDuration(string workerName, long durationMs)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        state.LastExecutionDurationMs = Math.Max(0, durationMs);
        state.RecentExecutionDurations.Enqueue(state.LastExecutionDurationMs);
        while (state.RecentExecutionDurations.Count > 60)
            state.RecentExecutionDurations.TryDequeue(out _);
    }

    public void RecordRetry(string workerName, int count = 1)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        Interlocked.Add(ref state.RetriesLastHour, Math.Max(0, count));
    }

    public void RecordRecovery(string workerName, int count = 1)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        Interlocked.Add(ref state.RecoveriesLastHour, Math.Max(0, count));
    }

    public void RecordWorkerHeartbeat(string workerName)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        state.IsStopped = false;
        state.LastSuccessAt = DateTime.UtcNow;
    }

    public IReadOnlyList<WorkerHealthSnapshot> GetCurrentSnapshots()
    {
        return _state.Select(kvp =>
        {
            var durations = kvp.Value.RecentDurations.ToArray();
            Array.Sort(durations);
            var queueLatencies = kvp.Value.RecentQueueLatencies.ToArray();
            Array.Sort(queueLatencies);
            var executionDurations = kvp.Value.RecentExecutionDurations.ToArray();
            Array.Sort(executionDurations);

            return new WorkerHealthSnapshot
            {
                WorkerName               = kvp.Key,
                IsRunning                = kvp.Value.IsRunning,
                LastSuccessAt            = kvp.Value.LastSuccessAt,
                LastErrorAt              = kvp.Value.LastErrorAt,
                LastErrorMessage         = kvp.Value.LastErrorMessage,
                LastCycleDurationMs      = kvp.Value.LastCycleDurationMs,
                CycleDurationP50Ms       = GetPercentile(durations, 0.50),
                CycleDurationP95Ms       = GetPercentile(durations, 0.95),
                CycleDurationP99Ms       = GetPercentile(durations, 0.99),
                ConsecutiveFailures      = Volatile.Read(ref kvp.Value.ConsecutiveFailures),
                ErrorsLastHour           = Volatile.Read(ref kvp.Value.ErrorsLastHour),
                SuccessesLastHour        = Volatile.Read(ref kvp.Value.SuccessesLastHour),
                BacklogDepth             = kvp.Value.BacklogDepth,
                LastQueueLatencyMs       = kvp.Value.LastQueueLatencyMs,
                QueueLatencyP50Ms        = GetPercentile(queueLatencies, 0.50),
                QueueLatencyP95Ms        = GetPercentile(queueLatencies, 0.95),
                LastExecutionDurationMs  = kvp.Value.LastExecutionDurationMs,
                ExecutionDurationP50Ms   = GetPercentile(executionDurations, 0.50),
                ExecutionDurationP95Ms   = GetPercentile(executionDurations, 0.95),
                RetriesLastHour          = Volatile.Read(ref kvp.Value.RetriesLastHour),
                RecoveriesLastHour       = Volatile.Read(ref kvp.Value.RecoveriesLastHour),
                ConfiguredIntervalSeconds = kvp.Value.ConfiguredIntervalSeconds,
                CapturedAt               = DateTime.UtcNow
            };
        }).ToList();
    }

    public async Task PersistSnapshotsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

        var snapshots = GetCurrentSnapshots();
        foreach (var snapshot in snapshots)
        {
            await ctx.Set<WorkerHealthSnapshot>().AddAsync(snapshot, cancellationToken);
        }

        await ctx.SaveChangesAsync(cancellationToken);

        // ── Connection pool observability ────────────────────────────────────
        // Npgsql exposes pool statistics via NpgsqlConnection.GetPoolStatistics()
        // which returns a Dictionary<string, long> with keys like "Idle", "Busy",
        // "Min", "Max". If the connection is Npgsql, log a warning when pool
        // utilization exceeds 80%.
        // TODO: Once Npgsql pool statistics API is confirmed available in the
        // current driver version, replace this with:
        //   var conn = ctx.Database.GetDbConnection();
        //   if (conn is Npgsql.NpgsqlConnection npgsqlConn)
        //   {
        //       var stats = Npgsql.NpgsqlConnection.GetPoolStatistics(npgsqlConn.ConnectionString);
        //       if (stats is not null && stats.TryGetValue("Max", out var max) &&
        //           stats.TryGetValue("Busy", out var busy) && max > 0)
        //       {
        //           double utilization = (double)busy / max;
        //           if (utilization > 0.80)
        //               _logger.LogWarning(
        //                   "WorkerHealthMonitor: Npgsql pool utilization {Utilization:P0} " +
        //                   "(Busy={Busy}, Idle={Idle}, Max={Max})",
        //                   utilization, busy,
        //                   stats.GetValueOrDefault("Idle"), max);
        //       }
        //   }

        // Detect crashed/stale workers: if a worker has a configured interval but hasn't
        // reported success in 3x that interval, mark it as stopped and log a critical alert.
        foreach (var kvp in _state)
        {
            var name = kvp.Key;
            var state = kvp.Value;

            if (state.IsStopped) continue; // Already marked

            int staleThresholdSecs = Math.Max(180, state.ConfiguredIntervalSeconds * 3);
            bool isStale = state.LastSuccessAt.HasValue &&
                           (DateTime.UtcNow - state.LastSuccessAt.Value).TotalSeconds > staleThresholdSecs;

            // Also detect workers that never reported success (registered but never ran)
            bool neverRan = !state.LastSuccessAt.HasValue && !state.LastErrorAt.HasValue;

            if (isStale)
            {
                state.IsStopped = true;
                var elapsedSeconds = (DateTime.UtcNow - state.LastSuccessAt!.Value).TotalSeconds;
                var consecutiveFailures = Volatile.Read(ref state.ConsecutiveFailures);

                _logger.LogCritical(
                    "WorkerHealthMonitor: worker {Worker} appears CRASHED — no heartbeat for {Elapsed:F0}s " +
                    "(threshold: {Threshold}s). Last success: {LastSuccess}. Consecutive failures: {Failures}",
                    name, elapsedSeconds, staleThresholdSecs, state.LastSuccessAt, consecutiveFailures);

                // Dispatch a critical alert for the crashed worker (deduplicated: one alert per worker until recovery)
                if (_alertedWorkers.TryAdd(name, true))
                {
                    try
                    {
                        var crashAlert = new Alert
                        {
                            AlertType     = AlertType.WorkerCrash,
                            Severity      = AlertSeverity.Critical,
                            IsActive      = true,
                            ConditionJson = JsonSerializer.Serialize(new
                            {
                                Source            = "WorkerHealthMonitor",
                                WorkerName        = name,
                                ElapsedSeconds    = Math.Round(elapsedSeconds, 1),
                                ThresholdSeconds  = staleThresholdSecs,
                                ConsecutiveErrors = consecutiveFailures,
                                LastSuccess       = state.LastSuccessAt,
                                LastError         = state.LastErrorMessage
                            })
                        };
                        await ctx.Set<Alert>().AddAsync(crashAlert, cancellationToken);
                        await ctx.SaveChangesAsync(cancellationToken);

                        var alertMessage = $"Worker {name} has crashed — no heartbeat for {elapsedSeconds:F0}s " +
                                           $"(threshold: {staleThresholdSecs}s, consecutive failures: {consecutiveFailures})";
                        await _alertDispatcher.DispatchAsync(crashAlert, alertMessage, cancellationToken);
                    }
                    catch (Exception alertEx)
                    {
                        _logger.LogError(alertEx,
                            "WorkerHealthMonitor: failed to dispatch crash alert for worker {Worker}", name);
                    }
                }
            }
        }

        // Reset hourly counters atomically
        foreach (var state in _state.Values)
        {
            Interlocked.Exchange(ref state.SuccessesLastHour, 0);
            Interlocked.Exchange(ref state.ErrorsLastHour, 0);
            Interlocked.Exchange(ref state.RetriesLastHour, 0);
            Interlocked.Exchange(ref state.RecoveriesLastHour, 0);
        }
    }

    public void RecordWorkerMetadata(string workerName, string? purpose, TimeSpan expectedInterval)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        int intervalSeconds = Math.Max(1, (int)expectedInterval.TotalSeconds);
        state.IsRunning = true;
        state.IsStopped = false;
        Volatile.Write(ref state.Purpose, purpose);
        Volatile.Write(ref state.ExpectedIntervalSeconds, intervalSeconds);
        state.ConfiguredIntervalSeconds = intervalSeconds;
    }

    public void RecordWorkerStopped(string workerName, string? errorMessage = null)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = false;
        state.IsStopped = true;
        if (errorMessage is not null)
        {
            state.LastErrorAt = DateTime.UtcNow;
            state.LastErrorMessage = errorMessage.Length > 500 ? errorMessage[..500] : errorMessage;
            _logger.LogError("WorkerHealthMonitor: {Worker} stopped with error: {Error}", workerName, errorMessage);
        }
    }

    private static long GetPercentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
