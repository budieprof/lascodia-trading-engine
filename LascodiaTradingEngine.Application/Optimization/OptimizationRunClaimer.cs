using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Encapsulates the atomic run-claiming SQL and lease management for the optimization
/// pipeline. Isolates the PostgreSQL-specific FOR UPDATE SKIP LOCKED pattern behind
/// a provider-aware method so the worker doesn't contain raw SQL inline.
/// </summary>
/// <remarks>
/// <b>PostgreSQL dependency:</b> The claim query uses <c>FOR UPDATE SKIP LOCKED</c>,
/// <c>LIMIT 1</c>, and <c>RETURNING "Id"</c> — all PostgreSQL-specific syntax.
/// If migrating to SQL Server, replace with <c>WITH (UPDLOCK, READPAST)</c> +
/// <c>TOP 1</c> + <c>OUTPUT INSERTED.Id</c>. The <see cref="ClaimNextRunAsync"/>
/// method is the single point that needs updating for a provider switch.
/// </remarks>
internal static class OptimizationRunClaimer
{
    private const string ClaimAdvisoryLockKey = "OptimizationRunClaimer:ClaimNextRun";

    /// <summary>
    /// Atomically claims the next queued optimization run by setting its status to Running,
    /// applying an execution lease, and enforcing the concurrency limit — all in a single
    /// database round-trip. Returns the claimed run's ID, or null if no eligible run exists.
    /// </summary>
    /// <param name="writeDb">The EF DbContext for executing raw SQL.</param>
    /// <param name="maxConcurrentRuns">Max allowed Running runs. &lt;= 0 disables the guard.</param>
    /// <param name="leaseDuration">How long the execution lease lasts before expiry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ID of the claimed run, or null if no queued run is available.</returns>
    internal static async Task<long?> ClaimNextRunAsync(
        DbContext writeDb, int maxConcurrentRuns, TimeSpan leaseDuration, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var leaseExpiry = nowUtc.Add(leaseDuration);
        var tableName = GetQuotedTableName(writeDb, typeof(OptimizationRun));

        // When MaxConcurrentRuns <= 0, skip the concurrency guard (unlimited).
        // Otherwise, the subquery only returns a row if the count of Running runs is below the limit.
        var concurrencyGuard = maxConcurrentRuns > 0
            ? $@"AND (SELECT COUNT(*) FROM {tableName} WHERE ""Status"" = {{0}} AND ""IsDeleted"" = false) < {{4}}"
            : "";

        var claimSql = $@"
            WITH claim_guard AS (
                SELECT pg_try_advisory_xact_lock(hashtext('{ClaimAdvisoryLockKey}')) AS acquired
            ),
            candidate AS (
                SELECT ""Id"" FROM {tableName}
                WHERE ""Status"" = {{3}} AND ""IsDeleted"" = false
                  AND (""DeferredUntilUtc"" IS NULL OR ""DeferredUntilUtc"" <= {{1}})
                  AND EXISTS (SELECT 1 FROM claim_guard WHERE acquired)
                  {concurrencyGuard}
                ORDER BY ""StartedAt""
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {tableName}
            SET ""Status"" = {{0}},
                ""StartedAt"" = {{1}},
                ""LastHeartbeatAt"" = {{1}},
                ""ExecutionLeaseExpiresAt"" = {{2}},
                ""DeferredUntilUtc"" = NULL
            WHERE ""Id"" = (SELECT ""Id"" FROM candidate)
            RETURNING ""Id""";

        var claimParams = maxConcurrentRuns > 0
            ? new object[]
            {
                OptimizationRunStatus.Running.ToString(),
                nowUtc,
                leaseExpiry,
                OptimizationRunStatus.Queued.ToString(),
                maxConcurrentRuns,
            }
            : new object[]
            {
                OptimizationRunStatus.Running.ToString(),
                nowUtc,
                leaseExpiry,
                OptimizationRunStatus.Queued.ToString(),
            };

        return await writeDb.Database.SqlQueryRaw<long?>(claimSql, claimParams)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Reclaims runs stuck in Running state whose execution lease has expired.
    /// Re-queues runs with living strategies; marks orphaned runs (deleted strategy) as Failed.
    /// </summary>
    internal static async Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext db, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;

        var activeStrategyIds = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var activeStrategySet = new HashSet<long>(activeStrategyIds);

        var expiredRuns = await db.Set<OptimizationRun>()
            .Where(r => r.Status == OptimizationRunStatus.Running
                     && !r.IsDeleted
                     && r.ExecutionLeaseExpiresAt != null
                     && r.ExecutionLeaseExpiresAt < nowUtc)
            .Select(r => new { r.Id, r.StrategyId })
            .ToListAsync(ct);

        if (expiredRuns.Count == 0) return (0, 0);

        var toRequeue = expiredRuns.Where(r => activeStrategySet.Contains(r.StrategyId)).Select(r => r.Id).ToList();
        var toOrphan  = expiredRuns.Where(r => !activeStrategySet.Contains(r.StrategyId)).Select(r => r.Id).ToList();

        int requeued = 0;
        if (toRequeue.Count > 0)
        {
            requeued = await db.Set<OptimizationRun>()
                .Where(r => toRequeue.Contains(r.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, OptimizationRunStatus.Queued)
                    .SetProperty(r => r.StartedAt, nowUtc)
                    .SetProperty(r => r.DeferredUntilUtc, (DateTime?)null)
                    .SetProperty(r => r.ExecutionLeaseExpiresAt, (DateTime?)null), ct);
        }

        int orphaned = 0;
        if (toOrphan.Count > 0)
        {
            orphaned = await db.Set<OptimizationRun>()
                .Where(r => toOrphan.Contains(r.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, OptimizationRunStatus.Failed)
                    .SetProperty(r => r.ErrorMessage, "Strategy deleted during optimization run")
                    .SetProperty(r => r.CompletedAt, nowUtc)
                    .SetProperty(r => r.ExecutionLeaseExpiresAt, (DateTime?)null), ct);
        }

        return (requeued, orphaned);
    }

    /// <summary>Updates the heartbeat timestamp and extends the execution lease.</summary>
    internal static void StampHeartbeat(OptimizationRun run, TimeSpan leaseDuration)
    {
        run.LastHeartbeatAt = DateTime.UtcNow;
        run.ExecutionLeaseExpiresAt = run.LastHeartbeatAt.Value.Add(leaseDuration);
    }

    private static string GetQuotedTableName(DbContext db, Type entityType)
    {
        var entity = db.Model.FindEntityType(entityType)
            ?? throw new InvalidOperationException($"Entity metadata not found for {entityType.Name}");

        var tableName = entity.GetTableName()
            ?? throw new InvalidOperationException($"Table name not found for {entityType.Name}");
        var schema = entity.GetSchema();

        return string.IsNullOrWhiteSpace(schema)
            ? $@"""{tableName}"""
            : $@"""{schema}"".""{tableName}""";
    }
}
