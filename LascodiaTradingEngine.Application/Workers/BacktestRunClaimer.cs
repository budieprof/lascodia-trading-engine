using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Encapsulates the atomic queue-claiming and lease-recovery logic for <see cref="BacktestRun"/>.
/// This keeps the PostgreSQL-specific concurrency SQL isolated from <see cref="BacktestWorker"/>
/// and makes the worker's ownership model explicit.
/// </summary>
/// <remarks>
/// <b>Claiming model:</b> a worker first claims a due queued row, stamps lease metadata, and only
/// then loads the full run for execution. The later transition into active processing happens in
/// <see cref="BacktestWorker"/>, so this helper is intentionally limited to ownership acquisition
/// and stale-lease recovery.
/// <para>
/// <b>PostgreSQL dependency:</b> claiming relies on <c>FOR UPDATE SKIP LOCKED</c> plus
/// <c>RETURNING "Id"</c> so multiple worker instances can race safely without double-claiming the
/// same run. If the provider changes, <see cref="ClaimNextRunAsync"/> is the single place that
/// must be adapted.
/// </para>
/// </remarks>
internal static class BacktestRunClaimer
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Result of a successful or attempted claim operation.
    /// </summary>
    /// <param name="RunId">
    /// The claimed run ID, or <see langword="null"/> when no queued run was eligible.
    /// </param>
    /// <param name="LeaseToken">
    /// The execution lease token written into the row when a claim succeeds. The worker uses this
    /// token to prove ownership when extending heartbeats.
    /// </param>
    internal readonly record struct ClaimResult(long? RunId, Guid LeaseToken);

    /// <summary>
    /// Atomically claims the next eligible queued backtest run and attaches a fresh execution lease.
    /// </summary>
    /// <param name="writeDb">Write-side EF Core DbContext used to execute the raw claim SQL.</param>
    /// <param name="nowUtc">Current UTC timestamp used for queue eligibility and lease timestamps.</param>
    /// <param name="workerId">Stable worker instance ID recorded for observability and recovery.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>
    /// A <see cref="ClaimResult"/> whose <c>RunId</c> is null when no queued run is currently due.
    /// </returns>
    internal static async Task<ClaimResult> ClaimNextRunAsync(
        DbContext writeDb,
        DateTime nowUtc,
        string workerId,
        CancellationToken ct)
    {
        EnsureSupportedProvider(writeDb);
        var leaseToken = Guid.NewGuid();
        var leaseExpiry = nowUtc.Add(BacktestExecutionLeasePolicy.LeaseDuration);
        var tableName = GetQuotedTableName(writeDb, typeof(BacktestRun));

        // The candidate CTE picks exactly one due queued run and locks it without blocking on rows
        // already claimed by another worker. The outer UPDATE performs the ownership transition and
        // lease stamp in the same round-trip, which avoids "read row, then update row" races.
        var claimSql = $@"
            WITH candidate AS (
                SELECT ""Id""
                FROM {tableName}
                WHERE ""Status"" = @queuedStatus
                  AND ""IsDeleted"" = false
                  AND ""AvailableAt"" <= @nowUtc
                ORDER BY ""Priority"" DESC, ""QueuedAt"", ""Id""
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {tableName}
            SET ""Status"" = @runningStatus,
                ""ClaimedAt"" = @nowUtc,
                ""ClaimedByWorkerId"" = @workerId,
                ""LastAttemptAt"" = @nowUtc,
                ""LastHeartbeatAt"" = @nowUtc,
                ""ExecutionLeaseExpiresAt"" = @leaseExpiry,
                ""ExecutionLeaseToken"" = @leaseToken
            WHERE ""Id"" = (SELECT ""Id"" FROM candidate)
            RETURNING ""Id"";";

        var connection = writeDb.Database.GetDbConnection();
        // Raw command execution bypasses EF's automatic connection management, so open it here
        // unless the caller already has an ambient transaction/connection in progress.
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = claimSql;
        // Join the current EF transaction when one exists so claim semantics stay aligned with the
        // caller's broader unit of work.
        if (writeDb.Database.CurrentTransaction is not null)
            command.Transaction = writeDb.Database.CurrentTransaction.GetDbTransaction();

        AddParameter(command, "@queuedStatus", RunStatus.Queued.ToString());
        AddParameter(command, "@runningStatus", RunStatus.Running.ToString());
        AddParameter(command, "@nowUtc", nowUtc);
        AddParameter(command, "@workerId", workerId);
        AddParameter(command, "@leaseExpiry", leaseExpiry);
        AddParameter(command, "@leaseToken", leaseToken);

        var scalar = await command.ExecuteScalarAsync(ct);
        // Providers can surface RETURNING values as different numeric CLR types, so normalize them
        // to long before handing the ID back to the worker.
        long? runId = scalar switch
        {
            long longId => longId,
            int intId => intId,
            decimal decimalId => (long)decimalId,
            _ => null
        };

        return new ClaimResult(runId, leaseToken);
    }

    /// <summary>
    /// Recovers lease-expired backtest runs that are stuck in <see cref="RunStatus.Running"/>.
    /// Runs whose strategies still exist are re-queued for replay; runs whose strategies were
    /// deleted are terminally failed with a structured validation error.
    /// </summary>
    /// <param name="writeDb">Write-side EF Core DbContext.</param>
    /// <param name="nowUtc">Current UTC timestamp used for expiry checks and recovery stamps.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
    /// <returns>
    /// A tuple containing the number of re-queued runs and the number marked orphaned/failed.
    /// </returns>
    internal static async Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Materialize active strategy IDs once so we can classify expired runs in memory without
        // issuing one existence check per candidate.
        var activeStrategyIds = await writeDb.Set<Strategy>()
            .Where(strategy => !strategy.IsDeleted)
            .Select(strategy => strategy.Id)
            .ToListAsync(ct);
        var activeStrategySet = new HashSet<long>(activeStrategyIds);

        // Only lease-expired Running rows are recoverable here. Queued rows remain untouched, and
        // currently leased rows are still considered owned by a live worker.
        var expiredRuns = await writeDb.Set<BacktestRun>()
            .Where(run => run.Status == RunStatus.Running
                       && !run.IsDeleted
                       && run.ExecutionLeaseExpiresAt != null
                       && run.ExecutionLeaseExpiresAt < nowUtc)
            .Select(run => new { run.Id, run.StrategyId })
            .ToListAsync(ct);

        if (expiredRuns.Count == 0)
            return (0, 0);

        // Separate recoverable work from permanently orphaned work before issuing set-based updates.
        var toRequeue = expiredRuns
            .Where(run => activeStrategySet.Contains(run.StrategyId))
            .Select(run => run.Id)
            .ToList();
        var toOrphan = expiredRuns
            .Where(run => !activeStrategySet.Contains(run.StrategyId))
            .Select(run => run.Id)
            .ToList();

        int requeued = 0;
        if (toRequeue.Count > 0)
        {
            // Reset all execution- and result-specific fields so the replacement worker starts from
            // the same clean state as a freshly queued run.
            requeued = await writeDb.Set<BacktestRun>()
                .Where(run => toRequeue.Contains(run.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.Status, RunStatus.Queued)
                    .SetProperty(run => run.QueuedAt, nowUtc)
                    .SetProperty(run => run.AvailableAt, nowUtc)
                    .SetProperty(run => run.ClaimedAt, (DateTime?)null)
                    .SetProperty(run => run.ClaimedByWorkerId, (string?)null)
                    .SetProperty(run => run.ExecutionStartedAt, (DateTime?)null)
                    .SetProperty(run => run.CompletedAt, (DateTime?)null)
                    .SetProperty(run => run.ErrorMessage, (string?)null)
                    .SetProperty(run => run.FailureCode, (ValidationFailureCode?)null)
                    .SetProperty(run => run.FailureDetailsJson, (string?)null)
                    .SetProperty(run => run.LastAttemptAt, (DateTime?)null)
                    .SetProperty(run => run.LastHeartbeatAt, (DateTime?)null)
                    .SetProperty(run => run.ExecutionLeaseExpiresAt, (DateTime?)null)
                    .SetProperty(run => run.ExecutionLeaseToken, (Guid?)null)
                    .SetProperty(run => run.ResultJson, (string?)null)
                    .SetProperty(run => run.TotalTrades, (int?)null)
                    .SetProperty(run => run.WinRate, (decimal?)null)
                    .SetProperty(run => run.ProfitFactor, (decimal?)null)
                    .SetProperty(run => run.MaxDrawdownPct, (decimal?)null)
                    .SetProperty(run => run.SharpeRatio, (decimal?)null)
                    .SetProperty(run => run.FinalBalance, (decimal?)null)
                    .SetProperty(run => run.TotalReturn, (decimal?)null), ct);
        }

        int orphaned = 0;
        if (toOrphan.Count > 0)
        {
            // If the owning strategy was deleted while the run was executing, do not recycle the
            // work item. Mark it failed with a machine-readable reason so operators and tests can
            // distinguish intentional orphan handling from generic execution errors.
            orphaned = await writeDb.Set<BacktestRun>()
                .Where(run => toOrphan.Contains(run.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(run => run.Status, RunStatus.Failed)
                    .SetProperty(run => run.CompletedAt, (DateTime?)nowUtc)
                    .SetProperty(run => run.ErrorMessage, "Strategy deleted during backtest run")
                    .SetProperty(run => run.FailureCode, ValidationRunFailureCodes.StrategyDeleted)
                    .SetProperty(run => run.FailureDetailsJson, ValidationRunException.SerializeDetails(new
                    {
                        FailureCode = ValidationRunFailureCodes.StrategyDeleted,
                        Reason = "Strategy deleted while validation run was executing."
                    }))
                    .SetProperty(run => run.ClaimedByWorkerId, (string?)null)
                    .SetProperty(run => run.ExecutionLeaseExpiresAt, (DateTime?)null)
                    .SetProperty(run => run.ExecutionLeaseToken, (Guid?)null), ct);
        }

        return (requeued, orphaned);
    }

    /// <summary>
    /// Verifies the DbContext is backed by PostgreSQL, which is required for the claim SQL.
    /// </summary>
    /// <param name="db">DbContext whose provider metadata should be validated.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown when the configured provider is not Npgsql.
    /// </exception>
    internal static void EnsureSupportedProvider(DbContext db)
    {
        string? providerName;
        try
        {
            providerName = db.Database?.ProviderName;
        }
        catch (Exception)
        {
            providerName = null;
        }

        if (string.Equals(providerName, PostgresProvider, StringComparison.Ordinal))
            return;

        throw new NotSupportedException(
            $"BacktestRunClaimer requires PostgreSQL ({PostgresProvider}) because it relies on FOR UPDATE SKIP LOCKED. Actual provider: {providerName ?? "<unknown>"}.");
    }

    /// <summary>
    /// Adds a provider-agnostic ADO.NET parameter to the current raw command.
    /// </summary>
    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Resolves the mapped table name from EF metadata and quotes it for raw SQL emission.
    /// This avoids hard-coding schema/table names and stays aligned with model configuration.
    /// </summary>
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
