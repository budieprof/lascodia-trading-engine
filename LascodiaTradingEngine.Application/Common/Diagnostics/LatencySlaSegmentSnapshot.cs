namespace LascodiaTradingEngine.Application.Common.Diagnostics;

/// <summary>
/// Rolling latency-percentile snapshot for one monitored SLA segment.
/// </summary>
public readonly record struct LatencySlaSegmentSnapshot(
    string SegmentName,
    int SampleCount,
    long P50Ms,
    long P95Ms,
    long P99Ms,
    DateTimeOffset LastRecordedAt);
