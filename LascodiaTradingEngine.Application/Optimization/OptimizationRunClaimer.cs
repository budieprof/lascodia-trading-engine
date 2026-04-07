using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    internal readonly record struct ClaimResult(long? RunId, Guid LeaseToken, bool WasDeferred);

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
    internal static async Task<ClaimResult> ClaimNextRunAsync(
        DbContext writeDb, int maxConcurrentRuns, TimeSpan leaseDuration, DateTime nowUtc, CancellationToken ct)
    {
        var leaseExpiry = nowUtc.Add(leaseDuration);
        var leaseToken = Guid.NewGuid();
        var tableName = GetQuotedTableName(writeDb, typeof(OptimizationRun));

        // When MaxConcurrentRuns <= 0, skip the concurrency guard (unlimited).
        // Otherwise, the subquery only returns a row if the count of Running runs is below the limit.
        var concurrencyGuard = maxConcurrentRuns > 0
            ? $@"AND (SELECT COUNT(*) FROM {tableName} WHERE ""Status"" = @runningStatus AND ""IsDeleted"" = false) < @maxConcurrentRuns"
            : "";

        var claimSql = $@"
            WITH claim_guard AS (
                SELECT pg_try_advisory_xact_lock(hashtext('{ClaimAdvisoryLockKey}')) AS acquired
            ),
            candidate AS (
                SELECT ""Id"", (""DeferredUntilUtc"" IS NOT NULL) AS ""WasDeferred"" FROM {tableName}
                WHERE ""Status"" = @queuedStatus AND ""IsDeleted"" = false
                  AND (""DeferredUntilUtc"" IS NULL OR ""DeferredUntilUtc"" <= @nowUtc)
                  AND EXISTS (SELECT 1 FROM claim_guard WHERE acquired)
                  {concurrencyGuard}
                ORDER BY ""StartedAt""
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {tableName}
            SET ""Status"" = @runningStatus,
                ""StartedAt"" = @nowUtc,
                ""LastHeartbeatAt"" = @nowUtc,
                ""ExecutionLeaseExpiresAt"" = @leaseExpiry,
                ""ExecutionLeaseToken"" = @leaseToken,
                ""DeferredUntilUtc"" = NULL
            WHERE ""Id"" = (SELECT ""Id"" FROM candidate)
            RETURNING ""Id"", COALESCE((SELECT ""WasDeferred"" FROM candidate), false) AS ""WasDeferred""";

        var connection = writeDb.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = claimSql;

        if (writeDb.Database.CurrentTransaction is not null)
            command.Transaction = writeDb.Database.CurrentTransaction.GetDbTransaction();

        AddParameter(command, "@runningStatus", OptimizationRunStatus.Running.ToString());
        AddParameter(command, "@queuedStatus", OptimizationRunStatus.Queued.ToString());
        AddParameter(command, "@nowUtc", nowUtc);
        AddParameter(command, "@leaseExpiry", leaseExpiry);
        AddParameter(command, "@leaseToken", leaseToken);

        if (maxConcurrentRuns > 0)
            AddParameter(command, "@maxConcurrentRuns", maxConcurrentRuns);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new ClaimResult(null, leaseToken, false);

        return new ClaimResult(
            reader.GetInt64(0),
            leaseToken,
            !reader.IsDBNull(1) && reader.GetBoolean(1));
    }

    /// <summary>
    /// Reclaims runs stuck in Running state whose execution lease has expired.
    /// Re-queues runs with living strategies; marks orphaned runs (deleted strategy) as Failed.
    /// </summary>
    internal static async Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext db, DateTime nowUtc, CancellationToken ct)
    {
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
            // Mirror OptimizationRunStateMachine.Transition(Running → Queued) side effects:
            // clear CompletedAt, ApprovedAt, ErrorMessage, FailureCategory, lease fields.
            requeued = await db.Set<OptimizationRun>()
                .Where(r => toRequeue.Contains(r.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, OptimizationRunStatus.Queued)
                    .SetProperty(r => r.StartedAt, nowUtc)
                    .SetProperty(r => r.CompletedAt, (DateTime?)null)
                    .SetProperty(r => r.ApprovedAt, (DateTime?)null)
                    .SetProperty(r => r.ErrorMessage, (string?)null)
                    .SetProperty(r => r.FailureCategory, (OptimizationFailureCategory?)null)
                    .SetProperty(r => r.DeferredUntilUtc, (DateTime?)null)
                    .SetProperty(r => r.ExecutionLeaseExpiresAt, (DateTime?)null)
                    .SetProperty(r => r.ExecutionLeaseToken, (Guid?)null), ct);
        }

        int orphaned = 0;
        if (toOrphan.Count > 0)
        {
            // Mirror OptimizationRunStateMachine.Transition(Running → Failed) side effects:
            // set CompletedAt, ErrorMessage, FailureCategory, clear lease fields.
            orphaned = await db.Set<OptimizationRun>()
                .Where(r => toOrphan.Contains(r.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, OptimizationRunStatus.Failed)
                    .SetProperty(r => r.ErrorMessage, "Strategy deleted during optimization run")
                    .SetProperty(r => r.FailureCategory, (OptimizationFailureCategory?)OptimizationFailureCategory.StrategyRemoved)
                    .SetProperty(r => r.CompletedAt, (DateTime?)nowUtc)
                    .SetProperty(r => r.ExecutionLeaseExpiresAt, (DateTime?)null)
                    .SetProperty(r => r.ExecutionLeaseToken, (Guid?)null), ct);
        }

        return (requeued, orphaned);
    }

    /// <summary>Updates the heartbeat timestamp and extends the execution lease.</summary>
    internal static void StampHeartbeat(OptimizationRun run, TimeSpan leaseDuration, DateTime utcNow)
    {
        run.LastHeartbeatAt = utcNow;
        run.ExecutionLeaseExpiresAt = run.LastHeartbeatAt.Value.Add(leaseDuration);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
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
