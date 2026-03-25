namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Abstraction for dead-letter persistence. The primary implementation writes to the
/// database (<see cref="IWriteApplicationDbContext"/>). The file-based fallback writes
/// to a local JSON file when the database is unavailable, ensuring events are never
/// truly lost even during a DB outage.
/// </summary>
public interface IDeadLetterSink
{
    /// <summary>
    /// Persists a dead-letter event. Tries the database first, falling back to a local
    /// JSON file if the DB write fails.
    /// </summary>
    Task WriteAsync(
        string handlerName,
        string eventType,
        string eventPayloadJson,
        string errorMessage,
        string? stackTrace,
        int attempts,
        CancellationToken ct = default);
}
