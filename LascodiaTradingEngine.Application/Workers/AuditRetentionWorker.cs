using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically prunes append-only audit tables that would otherwise grow
/// unbounded. Two tables in scope today:
/// <list type="bullet">
///   <item><see cref="SignalRejectionAudit"/> — one row per suppressed
///         signal; at 100+ ticks/sec fanout this can reach millions of rows
///         per month. Default retention: 90 days.</item>
///   <item><see cref="ReconciliationRun"/> — one row per EA snapshot
///         reconciliation; tens of thousands per month. Default retention:
///         180 days.</item>
/// </list>
///
/// <para>
/// <b>Why deletion is safe:</b> both tables are derived / historical — the
/// live pipeline never reads rows older than the last few ticks. The
/// <c>CalibrationSnapshotWorker</c> rolls up rejections into monthly
/// aggregates BEFORE this retention window closes, so the long-term
/// time-series is preserved in <see cref="CalibrationSnapshot"/>. Operators
/// who need raw rejection detail should query within the retention window.
/// </para>
///
/// <para>
/// <b>How:</b> uses EF Core's <c>ExecuteDeleteAsync</c> to delete in a
/// single server-side statement — no row loading, no tracking overhead. The
/// worker processes in bounded batches (default 10,000 rows per
/// <c>ExecuteDeleteAsync</c> call) to avoid long-running transactions on
/// large tables. On each pass, it loops until fewer than batch-size rows
/// were deleted, then sleeps for <c>Retention:PollIntervalHours</c> (default
/// 24) before the next cycle.
/// </para>
///
/// <para>
/// <b>Observability:</b> emits <c>trading.retention.rows_deleted</c> counter
/// tagged by table. Failures increment <c>WorkerErrors</c> with
/// <c>worker="AuditRetentionWorker"</c>.
/// </para>
/// </summary>
public sealed class AuditRetentionWorker : BackgroundService
{
    private const string CK_PollHours             = "Retention:PollIntervalHours";
    private const string CK_BatchSize             = "Retention:BatchSize";
    private const string CK_SignalRejectionDays   = "Retention:SignalRejectionAuditDays";
    private const string CK_ReconciliationDays    = "Retention:ReconciliationRunDays";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditRetentionWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public AuditRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AuditRetentionWorker> logger,
        TradingMetrics metrics,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so retention doesn't compete with startup hydration
        // for DB connections on the first boot of a newly-deployed instance.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollHours = 24;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var db = readCtx.GetDbContext();

                pollHours              = await ReadIntConfigAsync(db, CK_PollHours,           24,  stoppingToken);
                int batchSize          = await ReadIntConfigAsync(db, CK_BatchSize,           10_000, stoppingToken);
                int signalRejectionDays = await ReadIntConfigAsync(db, CK_SignalRejectionDays, 90,  stoppingToken);
                int reconciliationDays  = await ReadIntConfigAsync(db, CK_ReconciliationDays,  180, stoppingToken);

                await RunCycleAsync(scope, batchSize, signalRejectionDays, reconciliationDays, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AuditRetentionWorker: cycle failed");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "AuditRetentionWorker"));
            }

            try { await Task.Delay(TimeSpan.FromHours(Math.Max(1, pollHours)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Internal for unit-test access. Deletes one cycle's worth of rows
    /// across both retained tables, looping per table until a partial batch
    /// is returned (i.e. no more expired rows).
    /// </summary>
    internal async Task<RetentionCycleResult> RunCycleAsync(
        IServiceScope scope,
        int batchSize,
        int signalRejectionDays,
        int reconciliationDays,
        CancellationToken ct)
    {
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeCtx.GetDbContext();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        long signalRejectionDeleted = await PruneTableAsync(
            db.Set<SignalRejectionAudit>(),
            cutoff: now.AddDays(-Math.Max(1, signalRejectionDays)),
            dateSelector: r => r.RejectedAt,
            batchSize,
            tableTag: "SignalRejectionAudit",
            ct);

        long reconciliationDeleted = await PruneTableAsync(
            db.Set<ReconciliationRun>(),
            cutoff: now.AddDays(-Math.Max(1, reconciliationDays)),
            dateSelector: r => r.RunAt,
            batchSize,
            tableTag: "ReconciliationRun",
            ct);

        if (signalRejectionDeleted > 0 || reconciliationDeleted > 0)
        {
            _logger.LogInformation(
                "AuditRetentionWorker: deleted {SigRej} SignalRejectionAudit rows, {Recon} ReconciliationRun rows",
                signalRejectionDeleted, reconciliationDeleted);
        }

        return new RetentionCycleResult(signalRejectionDeleted, reconciliationDeleted);
    }

    private async Task<long> PruneTableAsync<TEntity>(
        IQueryable<TEntity> set,
        DateTime cutoff,
        System.Linq.Expressions.Expression<Func<TEntity, DateTime>> dateSelector,
        int batchSize,
        string tableTag,
        CancellationToken ct)
        where TEntity : class
    {
        long totalDeleted = 0;
        int safetyPasses = 100; // cap on iterations per cycle to bound work per tick

        for (int i = 0; i < safetyPasses; i++)
        {
            if (ct.IsCancellationRequested) break;

            // Find the batch-boundary timestamp: the cutoff is fixed, but we
            // limit each delete to batchSize rows by taking the Nth-oldest
            // expired row's timestamp and deleting everything up to (and
            // including) it. When fewer than batchSize expired rows exist we
            // delete all and exit the loop.
            var boundary = await ApplyOrderBy(set, dateSelector)
                .Where(BuildOlderThanPredicate(dateSelector, cutoff))
                .Select(dateSelector)
                .Skip(batchSize - 1)
                .FirstOrDefaultAsync(ct);

            // If Skip returned default(DateTime), there are ≤ batchSize rows
            // still expired. Delete the rest in one shot.
            DateTime deleteCap = boundary == default ? cutoff : boundary;

            int deletedThisPass = await set
                .Where(BuildLessOrEqualPredicate(dateSelector, deleteCap))
                .ExecuteDeleteAsync(ct);

            if (deletedThisPass == 0) break;
            totalDeleted += deletedThisPass;

            _metrics.RetentionRowsDeleted.Add(deletedThisPass,
                new KeyValuePair<string, object?>("table", tableTag));

            if (deletedThisPass < batchSize) break; // small final batch — done
        }

        return totalDeleted;
    }

    // Tiny helpers that build the predicate expressions from the selector so
    // the same pruner works against any entity + date-column pair without
    // separate typed methods per table.
    private static IQueryable<TEntity> ApplyOrderBy<TEntity>(
        IQueryable<TEntity> q,
        System.Linq.Expressions.Expression<Func<TEntity, DateTime>> dateSelector)
        => q.OrderBy(dateSelector);

    private static System.Linq.Expressions.Expression<Func<TEntity, bool>> BuildOlderThanPredicate<TEntity>(
        System.Linq.Expressions.Expression<Func<TEntity, DateTime>> dateSelector,
        DateTime cutoff)
    {
        var param = dateSelector.Parameters[0];
        var body  = System.Linq.Expressions.Expression.LessThan(
            dateSelector.Body,
            System.Linq.Expressions.Expression.Constant(cutoff));
        return System.Linq.Expressions.Expression.Lambda<Func<TEntity, bool>>(body, param);
    }

    private static System.Linq.Expressions.Expression<Func<TEntity, bool>> BuildLessOrEqualPredicate<TEntity>(
        System.Linq.Expressions.Expression<Func<TEntity, DateTime>> dateSelector,
        DateTime cutoff)
    {
        var param = dateSelector.Parameters[0];
        var body  = System.Linq.Expressions.Expression.LessThanOrEqual(
            dateSelector.Body,
            System.Linq.Expressions.Expression.Constant(cutoff));
        return System.Linq.Expressions.Expression.Lambda<Func<TEntity, bool>>(body, param);
    }

    private static async Task<int> ReadIntConfigAsync(DbContext ctx, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);
        if (entry?.Value is null || !int.TryParse(entry.Value, out var parsed)) return defaultValue;
        return parsed;
    }

    /// <summary>Outcome of one retention cycle — counts per table.</summary>
    internal readonly record struct RetentionCycleResult(
        long SignalRejectionDeleted,
        long ReconciliationDeleted);
}
