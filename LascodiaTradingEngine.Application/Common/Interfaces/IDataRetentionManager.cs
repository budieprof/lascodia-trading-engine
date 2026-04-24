namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Manages retention sweeps for hot storage: prunes expired records, reclaims already-retired
/// rows, and soft-deletes stale PendingModel strategies.
/// </summary>
public record RetentionResult(
    string EntityType,
    int RowsArchived,
    int RowsPurged,
    DateTime CutoffDate);

public interface IDataRetentionManager
{
    /// <summary>Runs the full retention sweep across all configured entity types.</summary>
    Task<IReadOnlyList<RetentionResult>> EnforceRetentionAsync(
        CancellationToken cancellationToken);

    /// <summary>Deletes idempotency keys older than the configured TTL. Returns the number of keys purged.</summary>
    Task<int> PurgeExpiredIdempotencyKeysAsync(
        CancellationToken cancellationToken);
}
