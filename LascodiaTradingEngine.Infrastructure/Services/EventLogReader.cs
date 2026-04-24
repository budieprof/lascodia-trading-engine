using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.IntegrationEventLogEF;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

namespace LascodiaTradingEngine.Infrastructure.Services;

/// <summary>
/// Reads and updates integration event log entries via <see cref="EventLogDbContext"/>.
/// Registered as Scoped so each retry cycle gets a fresh DbContext.
/// </summary>
public class EventLogReader : IEventLogReader
{
    private readonly IntegrationEventLogContext<EventLogDbContext> _context;

    public EventLogReader(IntegrationEventLogContext<EventLogDbContext> context)
    {
        _context = context;
    }

    public async Task<List<IntegrationEventLogEntry>> GetRetryableEventsAsync(
        DateTime stuckInProgressBeforeUtc,
        int batchSize,
        CancellationToken ct)
    {
        // Include events that have exhausted retries but were never transitioned to a
        // terminal state (e.g., process crashed between incrementing TimesSent and
        // marking DeadLettered). The caller is responsible for detecting exhaustion
        // and transitioning these to a terminal state rather than re-publishing.
        return await _context.IntegrationEventLogs
            .Where(e => e.State == EventStateEnum.PublishedFailed
                     || (e.State == EventStateEnum.InProgress && e.CreationTime < stuckInProgressBeforeUtc))
            .OrderBy(e => e.CreationTime)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task<List<IntegrationEventLogEntry>> GetStalePublishedEventsAsync(
        DateTime stalePublishedBeforeUtc,
        int maxTimesSentExclusive,
        int batchSize,
        CancellationToken ct)
    {
        return await _context.IntegrationEventLogs
            .Where(e => e.State == EventStateEnum.Published
                     && e.CreationTime < stalePublishedBeforeUtc
                     && e.TimesSent < maxTimesSentExclusive)
            .OrderBy(e => e.CreationTime)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IntegrationEventStatusSnapshot>> GetEventStatusSnapshotsAsync(
        IReadOnlyCollection<Guid> eventIds,
        CancellationToken ct)
    {
        if (eventIds.Count == 0)
            return new Dictionary<Guid, IntegrationEventStatusSnapshot>();

        return await _context.IntegrationEventLogs
            .AsNoTracking()
            .Where(e => eventIds.Contains(e.EventId))
            .ToDictionaryAsync(
                e => e.EventId,
                e => new IntegrationEventStatusSnapshot(
                    e.EventId,
                    e.State,
                    e.TimesSent,
                    e.CreationTime),
                ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _context.SaveChangesAsync(ct);
}
