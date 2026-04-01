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
    Task<IReadOnlyList<RetentionResult>> EnforceRetentionAsync(
        CancellationToken cancellationToken);

    Task<int> PurgeExpiredIdempotencyKeysAsync(
        CancellationToken cancellationToken);
}
