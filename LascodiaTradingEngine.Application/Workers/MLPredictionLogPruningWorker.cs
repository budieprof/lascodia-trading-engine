using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Soft-deletes resolved <see cref="MLModelPredictionLog"/> records older than a
/// configurable retention window to prevent unbounded DB growth.
///
/// <b>Motivation:</b> Every scored signal writes a prediction log row, and every drift,
/// suppression, accuracy, and conformal recalibration worker queries these rows.
/// Without pruning, the table grows indefinitely; queries over recent windows become
/// progressively slower as the DB must scan or index-skip an ever-larger historical tail.
/// Since all drift/suppression workers look back 7–21 days, logs older than
/// <c>RetentionDays</c> (e.g. 90 days) are dead weight for operational workers while
/// still being preserved for historical analysis (soft-delete, not hard-delete).
///
/// Only <b>resolved</b> logs (<c>DirectionCorrect != null</c>) are pruned — unresolved
/// logs are never deleted so the outcome worker can complete them.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLLogPruning:PollIntervalSeconds</c> — default 86400 (24 h)</item>
///   <item><c>MLLogPruning:RetentionDays</c>        — default 90</item>
///   <item><c>MLLogPruning:BatchSize</c>             — max rows per cycle, default 5000</item>
/// </list>
/// </summary>
public sealed class MLPredictionLogPruningWorker : BackgroundService
{
    private const string CK_PollSecs     = "MLLogPruning:PollIntervalSeconds";
    private const string CK_RetentionDays = "MLLogPruning:RetentionDays";
    private const string CK_BatchSize    = "MLLogPruning:BatchSize";

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLPredictionLogPruningWorker> _logger;

    public MLPredictionLogPruningWorker(
        IServiceScopeFactory                   scopeFactory,
        ILogger<MLPredictionLogPruningWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPredictionLogPruningWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 86400;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 86400, stoppingToken);

                await PruneAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLPredictionLogPruningWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPredictionLogPruningWorker stopping.");
    }

    // ── Pruning ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs one pruning cycle: soft-deletes both resolved and orphaned
    /// <see cref="MLModelPredictionLog"/> records that have aged past their respective
    /// retention thresholds.
    ///
    /// <para><b>Resolved log pruning</b> (<c>DirectionCorrect != null</c>):</para>
    /// <list type="bullet">
    ///   <item>Selects up to <c>BatchSize</c> resolved logs with <c>PredictedAt ≤ cutoff</c>
    ///         where <c>cutoff = now − RetentionDays</c>.</item>
    ///   <item>Applies a single bulk <c>ExecuteUpdateAsync</c> to set <c>IsDeleted = true</c>
    ///         without loading entities into the change tracker — efficient for large batches.</item>
    ///   <item>Soft-delete (not hard-delete) preserves audit history while suppressing the rows
    ///         from all global query filters used by drift, suppression, and accuracy workers.</item>
    /// </list>
    ///
    /// <para><b>Orphaned log pruning</b> (<c>DirectionCorrect == null</c>):</para>
    /// <list type="bullet">
    ///   <item>Uses a 2× retention multiplier as the orphan cutoff to distinguish genuinely
    ///         old unresolved logs from recently created ones still awaiting outcome candles.</item>
    ///   <item><see cref="MLPredictionOutcomeWorker.ResolveOrphanedLogsAsync"/> handles the
    ///         short-term case (signals rejected/expired). This method handles the long-term
    ///         residue — logs that somehow survived without a resolution source for double the
    ///         retention period, likely due to data gaps or system downtime.</item>
    /// </list>
    ///
    /// <para><b>Batch size cap</b>: pruning in bounded batches rather than a single unbounded
    /// DELETE prevents long-running transactions and table lock contention on the prediction log
    /// table, which is also written by <see cref="MLPredictionOutcomeWorker"/> concurrently.
    /// Remaining logs are pruned on the next daily cycle.</para>
    /// </summary>
    private async Task PruneAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int retentionDays = await GetConfigAsync<int>(readCtx, CK_RetentionDays, 90,   ct);
        int batchSize     = await GetConfigAsync<int>(readCtx, CK_BatchSize,     5000, ct);

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        // Collect IDs to soft-delete in batches to avoid long-running transactions
        var ids = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => !l.IsDeleted          &&
                        l.DirectionCorrect != null &&
                        l.PredictedAt      <= cutoff)
            .OrderBy(l => l.PredictedAt)
            .Take(batchSize)
            .AsNoTracking()
            .Select(l => l.Id)
            .ToListAsync(ct);

        if (ids.Count == 0)
        {
            _logger.LogDebug(
                "MLPredictionLogPruningWorker: no logs older than {Days}d to prune.", retentionDays);
            return;
        }

        int deleted = await writeCtx.Set<MLModelPredictionLog>()
            .Where(l => ids.Contains(l.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsDeleted, true), ct);

        _logger.LogInformation(
            "MLPredictionLogPruningWorker: soft-deleted {N} resolved prediction log(s) older than {Days}d.",
            deleted, retentionDays);

        // Also prune orphaned (unresolved) logs that are much older — these will never be resolved.
        // Use 2× retention period as a safe threshold for orphan detection.
        var orphanCutoff = DateTime.UtcNow.AddDays(-retentionDays * 2);

        int orphanedIds = await writeCtx.Set<MLModelPredictionLog>()
            .Where(l => !l.IsDeleted &&
                        l.DirectionCorrect == null &&
                        l.PredictedAt <= orphanCutoff)
            .Take(batchSize)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsDeleted, true), ct);

        if (orphanedIds > 0)
        {
            _logger.LogInformation(
                "MLPredictionLogPruningWorker: soft-deleted {N} orphaned (unresolved) prediction log(s) older than {Days}d.",
                orphanedIds, retentionDays * 2);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or its stored string cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }
}
