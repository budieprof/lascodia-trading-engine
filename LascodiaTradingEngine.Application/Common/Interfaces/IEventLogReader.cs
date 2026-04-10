using Lascodia.Trading.Engine.IntegrationEventLogEF;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public sealed record IntegrationEventStatusSnapshot(
    Guid EventId,
    EventStateEnum State,
    int TimesSent,
    DateTime CreationTime);

/// <summary>
/// Provides read/write access to the integration event log for the retry worker.
/// Implemented in Infrastructure to avoid Application referencing concrete DbContexts.
/// </summary>
public interface IEventLogReader
{
    /// <summary>
    /// Returns failed or stuck integration event log entries eligible for retry.
    /// </summary>
    /// <param name="stuckThreshold">Events in InProgress state older than this are considered stuck.</param>
    /// <param name="maxRetries">Skip events that have already been sent this many times.</param>
    /// <param name="batchSize">Maximum number of events to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<IntegrationEventLogEntry>> GetRetryableEventsAsync(
        TimeSpan stuckThreshold, int maxRetries, int batchSize, CancellationToken ct);

    /// <summary>
    /// Returns current event-log state for a known set of integration event ids.
    /// Missing ids are omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot>> GetEventStatusSnapshotsAsync(
        IReadOnlyCollection<Guid> eventIds,
        CancellationToken ct);

    /// <summary>
    /// Returns events marked as <c>Published</c> but older than the given threshold.
    /// These events may have been lost in transit (broker ACK not confirmed) and should
    /// be re-published to close the data-loss window.
    /// </summary>
    /// <param name="staleThreshold">Events marked Published longer ago than this are considered stale.</param>
    /// <param name="batchSize">Maximum number of events to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<IntegrationEventLogEntry>> GetStalePublishedEventsAsync(
        TimeSpan staleThreshold, int batchSize, CancellationToken ct);

    /// <summary>Persists state changes to event log entries modified in-memory.</summary>
    Task SaveChangesAsync(CancellationToken ct);
}
