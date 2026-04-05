using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;

namespace LascodiaTradingEngine.Infrastructure.Services;

/// <summary>
/// Tracks processed integration events using a lightweight database table to prevent
/// duplicate handling across multiple engine instances. Uses PostgreSQL's ON CONFLICT
/// to make <see cref="TryMarkAsProcessedAsync"/> atomic and idempotent.
///
/// The table (<c>ProcessedEvents</c>) stores only the event ID, handler name, and timestamp.
/// Rows older than 24 hours are eligible for cleanup by <c>DataRetentionWorker</c>.
/// </summary>
public class ProcessedEventTracker : IProcessedEventTracker
{
    private readonly IWriteApplicationDbContext _context;
    private readonly ILogger<ProcessedEventTracker> _logger;

    public ProcessedEventTracker(IWriteApplicationDbContext context, ILogger<ProcessedEventTracker> logger)
    {
        _context = context;
        _logger  = logger;
    }

    public async Task<bool> TryMarkAsProcessedAsync(Guid eventId, string handlerName, CancellationToken ct)
    {
        try
        {
            // Use raw SQL with ON CONFLICT for atomic insert-or-skip.
            // Returns 1 if inserted (first processor), 0 if already exists (duplicate).
            var sql = """
                INSERT INTO "ProcessedEvents" ("EventId", "HandlerName", "ProcessedAt")
                VALUES ({0}, {1}, {2})
                ON CONFLICT ("EventId", "HandlerName") DO NOTHING
                """;

            int rows = await _context.GetDbContext()
                .Database
                .ExecuteSqlRawAsync(sql, [eventId, handlerName, DateTime.UtcNow], ct);

            if (rows == 0)
            {
                _logger.LogDebug(
                    "ProcessedEventTracker: event {EventId} already processed by {Handler} — skipping",
                    eventId, handlerName);
            }

            return rows > 0;
        }
        catch (Exception ex)
        {
            // If the table doesn't exist yet (migration pending) or DB is unreachable,
            // allow processing to proceed — better to risk a duplicate than to block all events.
            _logger.LogWarning(ex,
                "ProcessedEventTracker: failed to check/mark event {EventId} — allowing processing (fail-open)",
                eventId);
            return true;
        }
    }
}
