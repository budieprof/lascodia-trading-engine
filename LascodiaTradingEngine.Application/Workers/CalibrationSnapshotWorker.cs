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
/// every <c>Calibration:PollIntervalHours</c> (default 24). On each cycle it
/// writes the snapshot for all complete months not yet snapshotted. The worker
/// is idempotent — the unique index on
/// <c>(PeriodStart, PeriodGranularity, Stage, Reason)</c> turns repeated inserts
/// for the same period into no-ops rather than duplicates.
/// </para>
///
/// <para>
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>Calibration:PollIntervalHours</c>  — default 24</item>
///   <item><c>Calibration:BackfillMonths</c>     — how far back to look on first run, default 6</item>
/// </list>
/// </para>
/// </summary>
public sealed class CalibrationSnapshotWorker : BackgroundService
{
    private const string CK_PollHours      = "Calibration:PollIntervalHours";
    private const string CK_BackfillMonths = "Calibration:BackfillMonths";

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
            int pollHours = 24;
            try
            {
                await RunCycleAsync(stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                pollHours = await ReadIntConfigAsync(readCtx.GetDbContext(), CK_PollHours, 24, stoppingToken);
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

            try { await Task.Delay(TimeSpan.FromHours(Math.Max(1, pollHours)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Internal for unit-test access. Writes snapshots for every complete month
    /// in the back-fill window that hasn't already been snapshotted. A "complete
    /// month" is any month whose end boundary is strictly before <c>UtcNow</c>
    /// — the current month is deliberately excluded because partial data would
    /// make the series non-monotonic.
    /// </summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var backfillMonths = await ReadIntConfigAsync(readCtx.GetDbContext(), CK_BackfillMonths, 6, ct);
        backfillMonths = Math.Max(1, backfillMonths);

        // Walk back <backfillMonths> complete months from the current one.
        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var writeDb = writeCtx.GetDbContext();

        int snapshotsWritten = 0;
        for (int i = 1; i <= backfillMonths; i++)
        {
            if (ct.IsCancellationRequested) break;

            var periodStart = currentMonthStart.AddMonths(-i);
            var periodEnd   = periodStart.AddMonths(1);

            // Skip months already snapshotted.
            bool alreadyExists = await writeDb.Set<CalibrationSnapshot>()
                .AnyAsync(s => s.PeriodStart == periodStart && s.PeriodGranularity == "Monthly", ct);
            if (alreadyExists) continue;

            var aggregates = await readCtx.GetDbContext()
                .Set<SignalRejectionAudit>()
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

            if (aggregates.Count == 0) continue;

            foreach (var agg in aggregates)
            {
                writeDb.Set<CalibrationSnapshot>().Add(new CalibrationSnapshot
                {
                    PeriodStart        = periodStart,
                    PeriodEnd          = periodEnd,
                    PeriodGranularity  = "Monthly",
                    Stage              = agg.Stage,
                    Reason             = agg.Reason,
                    RejectionCount     = agg.Count,
                    DistinctSymbols    = agg.DistinctSymbols,
                    DistinctStrategies = agg.DistinctStrategies,
                    ComputedAt         = now,
                });
                snapshotsWritten++;
                _metrics.CalibrationSnapshotsWritten.Add(1,
                    new KeyValuePair<string, object?>("period", "Monthly"));
            }

            await writeCtx.SaveChangesAsync(ct);
        }

        if (snapshotsWritten > 0)
        {
            _logger.LogInformation(
                "CalibrationSnapshotWorker: wrote {Count} snapshot rows across recent months",
                snapshotsWritten);
        }
    }

    private static async Task<int> ReadIntConfigAsync(DbContext ctx, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);
        if (entry?.Value is null || !int.TryParse(entry.Value, out var parsed)) return defaultValue;
        return parsed;
    }
}
