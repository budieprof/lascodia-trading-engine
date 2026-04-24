using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// In-memory rolling recorder for end-to-end SLA segment latencies.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(ILatencySlaRecorder))]
public sealed class LatencySlaRecorder : ILatencySlaRecorder
{
    private const int MaxSamplesPerSegment = 512;
    private static readonly TimeSpan SnapshotWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<LatencySlaSample>> _samples =
        new(StringComparer.Ordinal);

    public void RecordSample(string segmentName, long durationMs, DateTimeOffset? recordedAt = null)
    {
        if (string.IsNullOrWhiteSpace(segmentName))
            return;

        var queue = _samples.GetOrAdd(segmentName, _ => new ConcurrentQueue<LatencySlaSample>());
        queue.Enqueue(new LatencySlaSample(Math.Max(0, durationMs), recordedAt ?? DateTimeOffset.UtcNow));

        while (queue.Count > MaxSamplesPerSegment)
            queue.TryDequeue(out _);
    }

    public LatencySlaSegmentSnapshot? GetSnapshot(string segmentName, DateTimeOffset? now = null)
    {
        if (!_samples.TryGetValue(segmentName, out var queue))
            return null;

        return BuildSnapshot(segmentName, queue, now ?? DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<LatencySlaSegmentSnapshot> GetCurrentSnapshots(DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        var snapshots = new List<LatencySlaSegmentSnapshot>(_samples.Count);

        foreach (var (segmentName, queue) in _samples)
        {
            var snapshot = BuildSnapshot(segmentName, queue, effectiveNow);
            if (snapshot.HasValue)
                snapshots.Add(snapshot.Value);
        }

        return snapshots;
    }

    private static LatencySlaSegmentSnapshot? BuildSnapshot(
        string segmentName,
        ConcurrentQueue<LatencySlaSample> queue,
        DateTimeOffset now)
    {
        var cutoff = now - SnapshotWindow;
        var recentSamples = queue.ToArray()
            .Where(sample => sample.RecordedAt >= cutoff)
            .OrderBy(sample => sample.DurationMs)
            .ToArray();

        if (recentSamples.Length == 0)
            return null;

        var lastRecordedAt = recentSamples[^1].RecordedAt;
        return new LatencySlaSegmentSnapshot(
            segmentName,
            recentSamples.Length,
            GetPercentile(recentSamples, 0.50),
            GetPercentile(recentSamples, 0.95),
            GetPercentile(recentSamples, 0.99),
            lastRecordedAt);
    }

    private static long GetPercentile(LatencySlaSample[] orderedSamples, double percentile)
    {
        if (orderedSamples.Length == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * orderedSamples.Length) - 1;
        index = Math.Clamp(index, 0, orderedSamples.Length - 1);
        return orderedSamples[index].DurationMs;
    }

    private readonly record struct LatencySlaSample(long DurationMs, DateTimeOffset RecordedAt);
}
