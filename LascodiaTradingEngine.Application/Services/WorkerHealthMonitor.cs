using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

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
    private readonly ILogger<WorkerHealthMonitor> _logger;

    /// <summary>Per-worker health state, updated in real-time by worker calls.</summary>
    private readonly ConcurrentDictionary<string, WorkerState> _state = new();

    private class WorkerState
    {
        public bool IsRunning { get; set; } = true;
        public DateTime? LastSuccessAt { get; set; }
        public DateTime? LastErrorAt { get; set; }
        public string? LastErrorMessage { get; set; }
        public int ConsecutiveFailures;
        public int BacklogDepth { get; set; }

        // Sliding window of recent cycle durations (last 60 entries ~= last hour at 60s intervals)
        public readonly ConcurrentQueue<long> RecentDurations = new();
        // Volatile int fields + Interlocked for thread-safe increment/reset across worker threads
        public int SuccessesLastHour;
        public int ErrorsLastHour;
        public int ConfiguredIntervalSeconds { get; set; } = 60;

        // ── Static metadata for observability ────────────────────────────────
        public string? Purpose;
        public int ExpectedIntervalSeconds;
        public bool IsStopped;
    }

    public WorkerHealthMonitor(
        IServiceScopeFactory scopeFactory,
        ILogger<WorkerHealthMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public void RecordCycleSuccess(string workerName, long durationMs)
    {
        var state = _state.GetOrAdd(workerName, _ => new WorkerState());
        state.IsRunning = true;
        state.LastSuccessAt = DateTime.UtcNow;
        state.IsStopped = false; // Reset stopped flag — worker is clearly alive
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

            return new WorkerHealthSnapshot
            {
                WorkerName               = kvp.Key,
                IsRunning                = kvp.Value.IsRunning,
                LastSuccessAt            = kvp.Value.LastSuccessAt,
                LastErrorAt              = kvp.Value.LastErrorAt,
                LastErrorMessage         = kvp.Value.LastErrorMessage,
                LastCycleDurationMs      = durations.Length > 0 ? durations[^1] : 0,
                CycleDurationP50Ms       = GetPercentile(durations, 0.50),
                CycleDurationP95Ms       = GetPercentile(durations, 0.95),
                CycleDurationP99Ms       = GetPercentile(durations, 0.99),
                ConsecutiveFailures      = Volatile.Read(ref kvp.Value.ConsecutiveFailures),
                ErrorsLastHour           = Volatile.Read(ref kvp.Value.ErrorsLastHour),
                SuccessesLastHour        = Volatile.Read(ref kvp.Value.SuccessesLastHour),
                BacklogDepth             = kvp.Value.BacklogDepth,
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
                _logger.LogCritical(
                    "WorkerHealthMonitor: worker {Worker} appears CRASHED — no heartbeat for {Elapsed:F0}s " +
                    "(threshold: {Threshold}s). Last success: {LastSuccess}. Consecutive failures: {Failures}",
                    name,
                    (DateTime.UtcNow - state.LastSuccessAt!.Value).TotalSeconds,
                    staleThresholdSecs,
                    state.LastSuccessAt,
                    Volatile.Read(ref state.ConsecutiveFailures));
            }
        }

        // Reset hourly counters atomically
        foreach (var state in _state.Values)
        {
            Interlocked.Exchange(ref state.SuccessesLastHour, 0);
            Interlocked.Exchange(ref state.ErrorsLastHour, 0);
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
        var index = (int)Math.Floor(percentile * (sorted.Length - 1));
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
