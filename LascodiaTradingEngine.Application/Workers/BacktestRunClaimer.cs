using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

internal static class BacktestRunClaimer
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    internal readonly record struct ClaimResult(long? RunId, Guid LeaseToken);

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
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = claimSql;
        if (writeDb.Database.CurrentTransaction is not null)
            command.Transaction = writeDb.Database.CurrentTransaction.GetDbTransaction();

        AddParameter(command, "@queuedStatus", RunStatus.Queued.ToString());
        AddParameter(command, "@runningStatus", RunStatus.Running.ToString());
        AddParameter(command, "@nowUtc", nowUtc);
        AddParameter(command, "@workerId", workerId);
        AddParameter(command, "@leaseExpiry", leaseExpiry);
        AddParameter(command, "@leaseToken", leaseToken);

        var scalar = await command.ExecuteScalarAsync(ct);
        long? runId = scalar switch
        {
            long longId => longId,
            int intId => intId,
            decimal decimalId => (long)decimalId,
            _ => null
        };

        return new ClaimResult(runId, leaseToken);
    }

    internal static async Task<(int Requeued, int Orphaned)> RequeueExpiredRunsAsync(
        DbContext writeDb,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeStrategyIds = await writeDb.Set<Strategy>()
            .Where(strategy => !strategy.IsDeleted)
            .Select(strategy => strategy.Id)
            .ToListAsync(ct);
        var activeStrategySet = new HashSet<long>(activeStrategyIds);

        var expiredRuns = await writeDb.Set<BacktestRun>()
            .Where(run => run.Status == RunStatus.Running
                       && !run.IsDeleted
                       && run.ExecutionLeaseExpiresAt != null
                       && run.ExecutionLeaseExpiresAt < nowUtc)
            .Select(run => new { run.Id, run.StrategyId })
            .ToListAsync(ct);

        if (expiredRuns.Count == 0)
            return (0, 0);

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
