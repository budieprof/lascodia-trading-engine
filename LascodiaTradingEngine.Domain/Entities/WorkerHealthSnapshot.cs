using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Periodic health snapshot for a background worker, capturing cycle duration,
/// error rate, and backlog depth. Exposed via the /health/workers endpoint
/// and used for alerting when workers fall behind or stop processing.
/// </summary>
public class WorkerHealthSnapshot : Entity<long>
{
    /// <summary>Fully qualified worker type name (e.g. "MLTrainingWorker").</summary>
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>Whether the worker is currently running.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Timestamp of the last successful cycle completion.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Timestamp of the last error.</summary>
    public DateTime? LastErrorAt { get; set; }

    /// <summary>Last error message (truncated to 500 chars).</summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>Duration of the last cycle in milliseconds.</summary>
    public long LastCycleDurationMs { get; set; }

    /// <summary>P50 cycle duration over the last hour.</summary>
    public long CycleDurationP50Ms { get; set; }

    /// <summary>P95 cycle duration over the last hour.</summary>
    public long CycleDurationP95Ms { get; set; }

    /// <summary>P99 cycle duration over the last hour.</summary>
    public long CycleDurationP99Ms { get; set; }

    /// <summary>Number of consecutive failures (resets on success).</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Total errors in the last hour.</summary>
    public int ErrorsLastHour { get; set; }

    /// <summary>Total successful cycles in the last hour.</summary>
    public int SuccessesLastHour { get; set; }

    /// <summary>Number of items in the worker's processing queue (0 for poll-based workers).</summary>
    public int BacklogDepth { get; set; }

    /// <summary>Configured polling interval in seconds.</summary>
    public int ConfiguredIntervalSeconds { get; set; }

    /// <summary>When this snapshot was captured.</summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
