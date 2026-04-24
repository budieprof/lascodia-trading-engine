using LascodiaTradingEngine.Application.Common.Diagnostics;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Collects recent latency observations for the end-to-end trading-path segments monitored by
/// <see cref="Workers.LatencySlaWorker"/>.
/// </summary>
public interface ILatencySlaRecorder
{
    /// <summary>
    /// Records a latency sample for a named SLA segment.
    /// </summary>
    void RecordSample(string segmentName, long durationMs, DateTimeOffset? recordedAt = null);

    /// <summary>
    /// Returns the current snapshot for a single SLA segment, or <c>null</c> when no recent
    /// samples are available.
    /// </summary>
    LatencySlaSegmentSnapshot? GetSnapshot(string segmentName, DateTimeOffset? now = null);

    /// <summary>
    /// Returns the current snapshots for all known SLA segments.
    /// </summary>
    IReadOnlyList<LatencySlaSegmentSnapshot> GetCurrentSnapshots(DateTimeOffset? now = null);
}
