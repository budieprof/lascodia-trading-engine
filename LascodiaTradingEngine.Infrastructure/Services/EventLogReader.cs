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
        TimeSpan stuckThreshold, int maxRetries, int batchSize, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - stuckThreshold;

        return await _context.IntegrationEventLogs
            .Where(e => e.State == EventStateEnum.PublishedFailed
                     || (e.State == EventStateEnum.InProgress && e.CreationTime < cutoff))
            .Where(e => e.TimesSent < maxRetries)
            .OrderBy(e => e.CreationTime)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _context.SaveChangesAsync(ct);
}
