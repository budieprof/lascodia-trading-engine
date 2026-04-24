using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Rolls up the <see cref="SignalRejectionAudit"/> stream into monthly
/// <see cref="CalibrationSnapshot"/> rows so operators can watch gate hit rates
/// drift over time and calibrate thresholds against real traffic instead of
/// guesses.
///
/// <para>
/// <b>Why:</b> the engine has 200+ config knobs, 12 screening gates, and ~20
/// runtime rejection stages. Defaults carry a lot of load — when a gate's
/// rejection rate climbs to 40% of signals, that usually means the threshold is
/// mis-calibrated, not that traders suddenly got worse. A time series of
/// rejection counts per (stage, reason) turns a subjective "seems off" into an
/// alertable signal.
/// </para>
///
/// <para>
/// <b>Cadence:</b> runs once on startup (after a small initial delay) and then
/// every <c>Calibration:PollIntervalHours</c> (default 24, clamped to
/// <c>[1, 168]</c>). On each cycle it writes the snapshot for all complete
/// months not yet snapshotted.
/// </para>
///
/// <para>
/// <b>Idempotency:</b> each month is guarded by an <c>AnyAsync</c> existence
/// check keyed on <c>(PeriodStart, PeriodGranularity)</c> — that is the primary
/// defence against duplicate work. The unique index on
/// <c>(PeriodStart, PeriodGranularity, Stage, Reason)</c> is a defense-in-depth
/// backstop: if two writers race past the guard, one succeeds and the other
/// surfaces as a <c>DbUpdateException</c>, is logged by the per-month catch,
/// and retried on the next poll cycle.
/// </para>
///
/// <para>
/// <b>Failure isolation:</b> each month is processed in its own DI scope and
/// its own <c>SaveChangesAsync</c>. A transient DB blip or malformed row fails
/// exactly one month — the remaining months in the back-fill window continue
/// to be processed in the same cycle. The failed scope (and its change
/// tracker) is disposed immediately, so no pending entities leak into the next
/// month's save.
/// </para>
///
/// <para>
/// <b>Configuration</b> (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>Calibration:PollIntervalHours</c> — default 24, clamped to <c>[1, 168]</c></item>
///   <item><c>Calibration:BackfillMonths</c>    — default 6, clamped to <c>[1, 120]</c>
///         (the ceiling prevents a config typo like "6000" from triggering a decade-plus scan)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Observability:</b> emits <c>trading.calibration.snapshots_written</c>
/// counter (tagged <c>period="Monthly"</c>) — incremented only after a
/// successful per-month save, so a rolled-back batch never inflates the
/// counter. Per-month failures increment <c>WorkerErrors</c> with
/// <c>worker="CalibrationSnapshotWorker"</c>. The end-of-cycle summary log
/// reports processed / skipped / failed counts.
/// </para>
/// </summary>
public sealed class CalibrationSnapshotWorker : BackgroundService
{
    private const string CK_PollHours      = "Calibration:PollIntervalHours";
    private const string CK_BackfillMonths = "Calibration:BackfillMonths";

    internal const string GranularityMonthly = "Monthly";

    private const int DefaultBackfillMonths =   6;
    private const int MaxBackfillMonths     = 120;
    private const int DefaultPollHours      =  24;
    private const int MaxPollHours          = 168;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CalibrationSnapshotWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public CalibrationSnapshotWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CalibrationSnapshotWorker> logger,
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
        // Small initial delay so the worker does not compete with startup
        // hydration for DB connections. Each cycle then self-paces based on
        // EngineConfig (hot-reloadable).
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollHours = DefaultPollHours;
            try
            {
                await RunCycleAsync(stoppingToken);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                pollHours = await ReadIntConfigAsync(readCtx.GetDbContext(), CK_PollHours, DefaultPollHours, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CalibrationSnapshotWorker: cycle failed");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "CalibrationSnapshotWorker"));
            }

            try { await Task.Delay(TimeSpan.FromHours(Math.Clamp(pollHours, 1, MaxPollHours)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Internal for unit-test access. Writes snapshots for every complete month
    /// in the back-fill window that hasn't already been snapshotted. A "complete
    /// month" is any month whose end boundary is strictly before <c>UtcNow</c>
    /// — the current month is deliberately excluded because partial data would
    /// make the series non-monotonic. Each month is processed in its own DI
    /// scope so a single bad month does not stall the remaining window.
    /// </summary>
    internal async Task<CycleResult> RunCycleAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        int backfillMonths;
        await using (var cfgScope = _scopeFactory.CreateAsyncScope())
        {
            var readCtx = cfgScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            backfillMonths = await ReadIntConfigAsync(readCtx.GetDbContext(), CK_BackfillMonths, DefaultBackfillMonths, ct);
        }
        backfillMonths = Math.Clamp(backfillMonths, 1, MaxBackfillMonths);

        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        int  monthsProcessed          = 0;
        int  monthsSkippedAlreadyDone = 0;
        int  monthsSkippedEmpty       = 0;
        int  monthsFailed             = 0;
        long snapshotsWritten         = 0;

        for (int i = 1; i <= backfillMonths; i++)
        {
            if (ct.IsCancellationRequested) break;

            var periodStart = currentMonthStart.AddMonths(-i);
            var periodEnd   = periodStart.AddMonths(1);

            try
            {
                var outcome = await ProcessMonthAsync(periodStart, periodEnd, computedAt: now, ct);
                switch (outcome.Outcome)
                {
                    case MonthOutcome.Written:
                        monthsProcessed++;
                        snapshotsWritten += outcome.Rows;
                        break;
                    case MonthOutcome.AlreadyExists:
                        monthsSkippedAlreadyDone++;
                        break;
                    case MonthOutcome.Empty:
                        monthsSkippedEmpty++;
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CalibrationSnapshotWorker: failed to process period {PeriodStart:yyyy-MM} — continuing with remaining months",
                    periodStart);
                monthsFailed++;
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "CalibrationSnapshotWorker"));
            }
        }

        if (snapshotsWritten > 0 || monthsFailed > 0)
        {
            _logger.LogInformation(
                "CalibrationSnapshotWorker: cycle complete — processed={Processed} alreadyExists={AlreadyExists} empty={Empty} failed={Failed} rowsWritten={Rows}",
                monthsProcessed, monthsSkippedAlreadyDone, monthsSkippedEmpty, monthsFailed, snapshotsWritten);
        }

        return new CycleResult(
            monthsProcessed,
            monthsSkippedAlreadyDone,
            monthsSkippedEmpty,
            monthsFailed,
            snapshotsWritten);
    }

    /// <summary>
    /// Processes exactly one month in its own DI scope. Returns an outcome
    /// distinguishing a successful write, a skip because the month was
    /// already snapshotted, and a skip because the month had no rejections.
    /// Throws on DB failures — the outer loop catches and keeps the cycle
    /// alive.
    /// </summary>
    private async Task<MonthResult> ProcessMonthAsync(
        DateTime periodStart,
        DateTime periodEnd,
        DateTime computedAt,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb  = writeCtx.GetDbContext();

        bool alreadyExists = await writeDb.Set<CalibrationSnapshot>()
            .AnyAsync(s => s.PeriodStart == periodStart && s.PeriodGranularity == GranularityMonthly, ct);
        if (alreadyExists) return new MonthResult(MonthOutcome.AlreadyExists, 0);

        var aggregates = await readCtx.GetDbContext()
            .Set<SignalRejectionAudit>()
            .AsNoTracking()
            .Where(a => a.RejectedAt >= periodStart && a.RejectedAt < periodEnd)
            .GroupBy(a => new { a.Stage, a.Reason })
            .Select(g => new
            {
                g.Key.Stage,
                g.Key.Reason,
                Count = (long)g.Count(),
                DistinctSymbols = g.Select(x => x.Symbol).Distinct().Count(),
                DistinctStrategies = g.Select(x => x.StrategyId).Distinct().Count(),
            })
            .ToListAsync(ct);

        if (aggregates.Count == 0) return new MonthResult(MonthOutcome.Empty, 0);

        foreach (var agg in aggregates)
        {
            writeDb.Set<CalibrationSnapshot>().Add(new CalibrationSnapshot
            {
                PeriodStart        = periodStart,
                PeriodEnd          = periodEnd,
                PeriodGranularity  = GranularityMonthly,
                Stage              = agg.Stage,
                Reason             = agg.Reason,
                RejectionCount     = agg.Count,
                DistinctSymbols    = agg.DistinctSymbols,
                DistinctStrategies = agg.DistinctStrategies,
                ComputedAt         = computedAt,
            });
        }

        await writeCtx.SaveChangesAsync(ct);

        // Metric only after the save — a rolled-back batch must not inflate it.
        _metrics.CalibrationSnapshotsWritten.Add(aggregates.Count,
            new KeyValuePair<string, object?>("period", GranularityMonthly));

        return new MonthResult(MonthOutcome.Written, aggregates.Count);
    }

    private static async Task<int> ReadIntConfigAsync(DbContext ctx, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);
        if (entry?.Value is null || !int.TryParse(entry.Value, out var parsed)) return defaultValue;
        return parsed;
    }

    /// <summary>Outcome of one cycle — per-month counts and total rows written.</summary>
    internal readonly record struct CycleResult(
        int  MonthsProcessed,
        int  MonthsSkippedAlreadyExists,
        int  MonthsSkippedEmpty,
        int  MonthsFailed,
        long SnapshotsWritten);

    /// <summary>Outcome of a single month's processing.</summary>
    private enum MonthOutcome { Written, AlreadyExists, Empty }

    /// <summary>Result of <c>ProcessMonthAsync</c>: the outcome and, on Written, the row count.</summary>
    private readonly record struct MonthResult(MonthOutcome Outcome, long Rows);
}
