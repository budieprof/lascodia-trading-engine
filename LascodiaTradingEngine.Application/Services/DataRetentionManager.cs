using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Enforces tiered data retention policies: deletes aged records from hot storage (RDBMS)
/// in configurable batches. Each entity type has its own retention period.
/// All timestamp filters are applied at the database level to avoid materializing excess data.
/// </summary>
[RegisterService]
public class DataRetentionManager : IDataRetentionManager
{
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<DataRetentionManager> _logger;

    public DataRetentionManager(
        IWriteApplicationDbContext writeContext,
        DataRetentionOptions options,
        ILogger<DataRetentionManager> logger)
    {
        _writeContext = writeContext;
        _options      = options;
        _logger       = logger;
    }

    public async Task<IReadOnlyList<RetentionResult>> EnforceRetentionAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<RetentionResult>();
        var ctx = _writeContext.GetDbContext();

        // Each entity type gets its own DB-side filtered query to avoid materializing excess data.

        results.Add(await PurgePredictionLogsAsync(ctx, cancellationToken));
        results.Add(await PurgeTickRecordsAsync(ctx, cancellationToken));
        results.Add(await PurgeWorkerHealthAsync(ctx, cancellationToken));
        results.Add(await PurgeAnomaliesAsync(ctx, cancellationToken));

        var idempotencyPurged = await PurgeExpiredIdempotencyKeysAsync(cancellationToken);
        results.Add(new RetentionResult("ProcessedIdempotencyKey", 0, idempotencyPurged, DateTime.UtcNow));

        foreach (var r in results.Where(r => r.RowsPurged > 0))
        {
            _logger.LogInformation(
                "DataRetention: purged {Count} {Entity} records older than {Cutoff:d}",
                r.RowsPurged, r.EntityType, r.CutoffDate);
        }

        return results;
    }

    public async Task<int> PurgeExpiredIdempotencyKeysAsync(CancellationToken cancellationToken)
    {
        var ctx = _writeContext.GetDbContext();
        var cutoff = DateTime.UtcNow;

        var expired = await ctx.Set<ProcessedIdempotencyKey>()
            .Where(k => k.ExpiresAt < cutoff)
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
        DbContext ctx, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.PredictionLogHotDays);

        var batch = await ctx.Set<MLModelPredictionLog>()
            .Where(e => e.PredictedAt < cutoff)
            .OrderBy(e => e.PredictedAt)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<MLModelPredictionLog>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("MLModelPredictionLog", 0, batch.Count, cutoff);
    }

    private async Task<RetentionResult> PurgeTickRecordsAsync(
        DbContext ctx, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.TickRecordHotDays);

        var batch = await ctx.Set<TickRecord>()
            .Where(e => e.ReceivedAt < cutoff)
            .OrderBy(e => e.ReceivedAt)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<TickRecord>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("TickRecord", 0, batch.Count, cutoff);
    }

    private async Task<RetentionResult> PurgeWorkerHealthAsync(
        DbContext ctx, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.WorkerHealthSnapshotDays);

        var batch = await ctx.Set<WorkerHealthSnapshot>()
            .Where(e => e.CapturedAt < cutoff)
            .OrderBy(e => e.CapturedAt)
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
        DbContext ctx, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.MarketDataAnomalyDays);

        var batch = await ctx.Set<MarketDataAnomaly>()
            .Where(e => e.DetectedAt < cutoff)
            .OrderBy(e => e.DetectedAt)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (batch.Count > 0)
        {
            ctx.Set<MarketDataAnomaly>().RemoveRange(batch);
            await ctx.SaveChangesAsync(ct);
        }

        return new RetentionResult("MarketDataAnomaly", 0, batch.Count, cutoff);
    }
}
