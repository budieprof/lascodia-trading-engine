namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Manages tiered data retention: migrates aged data from hot (RDBMS) to warm storage,
/// prunes expired records, and enforces retention policies per entity type.
/// </summary>
public record RetentionResult(
    string EntityType,
    int RowsArchived,
    int RowsPurged,
    DateTime CutoffDate);

public interface IDataRetentionManager
{
    /// <summary>Runs the full retention sweep: archives aged data and purges expired records per entity type.</summary>
    Task<IReadOnlyList<RetentionResult>> EnforceRetentionAsync(
        CancellationToken cancellationToken);

    /// <summary>Deletes idempotency keys older than the configured TTL. Returns the number of keys purged.</summary>
    Task<int> PurgeExpiredIdempotencyKeysAsync(
        CancellationToken cancellationToken);
}
