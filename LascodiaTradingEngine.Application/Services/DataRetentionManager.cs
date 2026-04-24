using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Enforces tiered data retention policies in configurable batches. Each entity type has its own
/// retention period, and all timestamp filters are applied at the database level to avoid
/// materializing excess data.
/// </summary>
[RegisterService]
public sealed class DataRetentionManager : IDataRetentionManager
{
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<DataRetentionManager> _logger;
    private readonly TimeProvider _timeProvider;

    public DataRetentionManager(
        IWriteApplicationDbContext writeContext,
        DataRetentionOptions options,
        ILogger<DataRetentionManager> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writeContext);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _writeContext = writeContext;
        _options      = options;
        _logger       = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<RetentionResult>> EnforceRetentionAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<RetentionResult>();
        var ctx = _writeContext.GetDbContext();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Each entity type gets its own DB-side filtered query to avoid materializing excess data.

        results.Add(await PurgePredictionLogsAsync(ctx, now, cancellationToken));
        results.Add(await PurgeTickRecordsAsync(ctx, now, cancellationToken));
        results.Add(await PurgeCandlesAsync(ctx, now, cancellationToken));
        results.Add(await PurgeWorkerHealthAsync(ctx, now, cancellationToken));
        results.Add(await PurgeAnomaliesAsync(ctx, now, cancellationToken));
        results.Add(await PurgePublishedIntegrationEventsAsync(ctx, now, cancellationToken));
        results.Add(await PurgeDecisionLogsAsync(ctx, now, cancellationToken));
        results.Add(await PurgePendingModelStrategiesAsync(ctx, now, cancellationToken));

        var idempotencyPurged = await PurgeExpiredIdempotencyKeysAsync(now, cancellationToken);
        results.Add(new RetentionResult("ProcessedIdempotencyKey", 0, idempotencyPurged, now));

        foreach (var r in results.Where(r => r.RowsPurged > 0))
        {
            _logger.LogInformation(
                "DataRetention: purged {Count} {Entity} records older than {Cutoff:d}",
                r.RowsPurged, r.EntityType, r.CutoffDate);
        }

        return results;
    }

    public async Task<int> PurgeExpiredIdempotencyKeysAsync(CancellationToken cancellationToken)
        => await PurgeExpiredIdempotencyKeysAsync(_timeProvider.GetUtcNow().UtcDateTime, cancellationToken);

    private async Task<int> PurgeExpiredIdempotencyKeysAsync(DateTime now, CancellationToken cancellationToken)
    {
        var ctx = _writeContext.GetDbContext();
        var cutoff = now;

        var expired = await ctx.Set<ProcessedIdempotencyKey>()
            .Where(k => k.ExpiresAt < cutoff)
            .OrderBy(k => k.ExpiresAt)
            .ThenBy(k => k.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count > 0)
        {
            ctx.Set<ProcessedIdempotencyKey>().RemoveRange(expired);
            await ctx.SaveChangesAsync(cancellationToken);
        }

        return expired.Count;
    }

    private async Task<RetentionResult> PurgePredictionLogsAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        // Prediction logs are first retired from operational queries by MLPredictionLogPruningWorker
        // via soft-delete. This sweep physically reclaims only those already-retired rows, which
        // avoids racing the outcome worker on still-active unresolved logs.
        var cutoff = now.AddDays(-_options.PredictionLogHotDays);

        var batch = await ctx.Set<MLModelPredictionLog>()
            .IgnoreQueryFilters()
            .Where(e => e.IsDeleted && e.PredictedAt < cutoff)
            .OrderBy(e => e.PredictedAt)
            .ThenBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<MLModelPredictionLog>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("MLModelPredictionLog", 0, batch.Count, cutoff);
    }

    private async Task<RetentionResult> PurgeDecisionLogsAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        // DecisionLog is written on every signal-filter rejection and every audit-worthy
        // pipeline decision. At observed peak rates (~400 rows per 5 minutes during heavy
        // signal generation) the table can grow ~120k rows/day, so pruning past the hot
        // retention window is essential to keep the table queryable.
        var cutoff = now.AddDays(-_options.DecisionLogHotDays);

        var batch = await ctx.Set<DecisionLog>()
            .Where(e => e.CreatedAt < cutoff)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<DecisionLog>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("DecisionLog", 0, batch.Count, cutoff);
    }

    private async Task<RetentionResult> PurgePendingModelStrategiesAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        // Strategies parked in LifecycleStage = PendingModel by DeferredCompositeMLRegistrar
        // are waiting for an MLTrainingRun to complete. If the training never succeeds
        // (bad data, repeated quality-gate failures, etc.) the strategy sits forever.
        // TTL sweep prunes them past the configured horizon so the generation pipeline
        // doesn't accumulate zombie rows. Setting the option to 0 disables pruning.
        if (_options.PendingModelStrategyTtlDays <= 0)
            return new RetentionResult("Strategy.PendingModel", 0, 0, now);

        var cutoff = now.AddDays(-_options.PendingModelStrategyTtlDays);

        var stuck = await ctx.Set<Strategy>()
            .Where(s => !s.IsDeleted
                     && s.LifecycleStage == StrategyLifecycleStage.PendingModel
                     && s.LifecycleStageEnteredAt != null
                     && s.LifecycleStageEnteredAt < cutoff)
            .OrderBy(s => s.LifecycleStageEnteredAt)
            .ThenBy(s => s.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (stuck.Count > 0)
        {
            foreach (var s in stuck)
            {
                s.IsDeleted = true;
                s.PrunedAtUtc = now;
                s.PauseReason = $"PendingModel TTL expired ({_options.PendingModelStrategyTtlDays} days) — training run never completed.";
            }
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("Strategy.PendingModel", 0, stuck.Count, cutoff);
    }

    private async Task<RetentionResult> PurgeTickRecordsAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-_options.TickRecordHotDays);

        var batch = await ctx.Set<TickRecord>()
            .Where(e => e.ReceivedAt < cutoff)
            .OrderBy(e => e.ReceivedAt)
            .ThenBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<TickRecord>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("TickRecord", 0, batch.Count, cutoff);
    }

    private async Task<RetentionResult> PurgeCandlesAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-_options.CandleHotDays);

        var batch = await ctx.Set<Candle>()
            .Where(e => e.Timestamp < cutoff)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<Candle>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("Candle", 0, batch.Count, cutoff);
    }

    private async Task<RetentionResult> PurgeWorkerHealthAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-_options.WorkerHealthSnapshotDays);

        var batch = await ctx.Set<WorkerHealthSnapshot>()
            .Where(e => e.CapturedAt < cutoff)
            .OrderBy(e => e.CapturedAt)
            .ThenBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<WorkerHealthSnapshot>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("WorkerHealthSnapshot", 0, batch.Count, cutoff);
    }

    private async Task<RetentionResult> PurgeAnomaliesAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-_options.MarketDataAnomalyDays);

        var batch = await ctx.Set<MarketDataAnomaly>()
            .Where(e => e.DetectedAt < cutoff)
            .OrderBy(e => e.DetectedAt)
            .ThenBy(e => e.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<MarketDataAnomaly>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("MarketDataAnomaly", 0, batch.Count, cutoff);
    }

    /// <summary>
    /// Purges Published (state=2) integration event log entries older than the configured retention.
    /// Failed (state=3) and InProgress (state=1) events are retained for the retry worker / DLQ flow.
    /// Uses raw SQL with a CTE-based batch delete because IntegrationEventLogEntry lives in a separate
    /// assembly and isn't a project-level entity.
    /// </summary>
    private async Task<RetentionResult> PurgePublishedIntegrationEventsAsync(
        DbContext ctx, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-_options.IntegrationEventLogPublishedDays);
        var batchSize = _options.BatchSize;

        // CTE-based batch delete keeps the txn small and the lock window short on a hot table.
        // State=2 = Published (see Lascodia.Trading.Engine.IntegrationEventLogEF.EventStateEnum).
        const string sql = @"
WITH victims AS (
    SELECT ""EventId""
    FROM ""IntegrationEventLog""
    WHERE ""State"" = 2 AND ""CreationTime"" < {0}
    ORDER BY ""CreationTime"", ""EventId""
    LIMIT {1}
)
DELETE FROM ""IntegrationEventLog""
WHERE ""EventId"" IN (SELECT ""EventId"" FROM victims);";

        var rowsDeleted = await ctx.Database.ExecuteSqlRawAsync(
            sql,
            new object[] { cutoff, batchSize },
            ct);

        return new RetentionResult("IntegrationEventLog", 0, rowsDeleted, cutoff);
    }
}
