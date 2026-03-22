namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Thread-safe monotonically increasing sequence number generator for integration events.
/// Consumers can use the sequence number to detect out-of-order delivery and
/// discard stale events (e.g. an older price update arriving after a newer one).
///
/// The sequence is per-process — not globally unique across instances. For cross-instance
/// ordering, consumers should combine <c>SequenceNumber</c> with <c>CreationDate</c>.
/// </summary>
public static class EventSequence
{
    private static long _counter;

    /// <summary>Returns the next sequence number (thread-safe, monotonically increasing).</summary>
    public static long Next() => Interlocked.Increment(ref _counter);
}
