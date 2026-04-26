using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Rolls up the <see cref="SignalRejectionAudit"/> stream into monthly
/// <see cref="CalibrationSnapshot"/> rows so operators can watch gate hit rates drift over
/// time and calibrate thresholds against real traffic instead of guesses.
///
/// <para><b>Cadence:</b> runs once on startup (after a small initial delay) and then every
/// <c>Calibration:PollIntervalHours</c> (default 24). Each cycle adds uniform jitter from
/// <c>Calibration:PollJitterSeconds</c> so replicas don't poll in lockstep, and applies
/// <c>2^min(consecutiveFailures, FailureBackoffCapShift)</c> exponential backoff so a DB
/// outage isn't hammered at full poll rate.</para>
///
/// <para><b>Coordination:</b> a singleton cycle-level distributed lock keeps only one
/// replica driving backfill per cycle, so two replicas don't redundantly aggregate
/// rejections and race on the unique-index backstop. Per-month idempotency is enforced by
/// an existence check; the unique index <c>(PeriodStart, PeriodGranularity, Stage, Reason)</c>
/// is a defense-in-depth backstop that <see cref="IDatabaseExceptionClassifier"/> classifies
/// as <c>AlreadyExists</c> rather than <c>Failed</c>.</para>
///
/// <para><b>Failure isolation:</b> each month is processed in its own DI scope and its own
/// <c>SaveChangesAsync</c>. A transient DB blip or malformed row fails exactly one month —
/// the remaining months in the back-fill window continue.</para>
///
/// <para><b>Operator alerts:</b>
/// <list type="bullet">
///   <item>Fleet-systemic alert fires when <c>FleetSystemicConsecutiveFailureCycles</c> cycles
///   in a row produce zero successful month writes despite candidates existing — usually a
///   broken DB schema or upstream pipeline.</item>
///   <item>Staleness alert fires when the most recent <see cref="CalibrationSnapshot"/> is
///   older than <c>StalenessAlertHours</c>. Auto-resolves on the next successful write.</item>
/// </list></para>
/// </summary>
public sealed partial class CalibrationSnapshotWorker : BackgroundService
{
    internal const string WorkerName = nameof(CalibrationSnapshotWorker);
    internal const string GranularityMonthly = "Monthly";
    internal const string CycleLockKey = "calibration-snapshot:cycle";
    internal const string FleetSystemicDedupeKey = "Calibration:FleetSystemic";
    internal const string StalenessDedupeKey = "Calibration:Staleness";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CalibrationSnapshotWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly CalibrationSnapshotOptions _options;
    private readonly CalibrationSnapshotConfigReader _configReader;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDatabaseExceptionClassifier? _dbExceptionClassifier;

    private int  _consecutiveFailures;
    private bool _fleetSystemicAlertActive;
    private bool _stalenessAlertActive;

    public CalibrationSnapshotWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<CalibrationSnapshotWorker> logger,
        TradingMetrics metrics,
        TimeProvider timeProvider,
        CalibrationSnapshotOptions? options = null,
        CalibrationSnapshotConfigReader? configReader = null,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDatabaseExceptionClassifier? dbExceptionClassifier = null)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
        _timeProvider = timeProvider;
        _options      = options ?? new CalibrationSnapshotOptions();
        _configReader = configReader ?? new CalibrationSnapshotConfigReader(_options);
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _dbExceptionClassifier = dbExceptionClassifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Rolls SignalRejectionAudit into monthly CalibrationSnapshot rows.",
            TimeSpan.FromHours(_options.PollIntervalHours));

        try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, _options.InitialDelayMinutes)), _timeProvider, stoppingToken); }
        catch (OperationCanceledException) { _healthMonitor?.RecordWorkerStopped(WorkerName); return; }

        // Lazy-initialised on first cycle so we can read jitter/backoff from EngineConfig.
        var lastConfig = new CalibrationSnapshotRuntimeConfig(
            _options.PollIntervalHours,
            _options.BackfillMonths,
            _options.PollJitterSeconds,
            _options.FailureBackoffCapShift,
            _options.UseCycleLock,
            _options.CycleLockTimeoutSeconds,
            _options.FleetSystemicConsecutiveFailureCycles,
            _options.StalenessAlertHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
            var cycleStopwatch = Stopwatch.StartNew();
            try
            {
                var (result, config) = await RunCycleAsync(stoppingToken);
                lastConfig = config;
                _consecutiveFailures = 0;
                _healthMonitor?.RecordCycleSuccess(WorkerName, (long)cycleStopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _healthMonitor?.RecordWorkerStopped(WorkerName);
                return;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "{Worker}: cycle failed", WorkerName);
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", WorkerName));
                _metrics.CalibrationSnapshotConsecutiveCycleFailures.Add(_consecutiveFailures);
                _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
            }

            try
            {
                await Task.Delay(NextDelay(lastConfig, _consecutiveFailures), _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) { _healthMonitor?.RecordWorkerStopped(WorkerName); return; }
        }
        _healthMonitor?.RecordWorkerStopped(WorkerName);
    }

    /// <summary>
    /// Runs one cycle. Returns the per-month counts plus the resolved runtime config so the
    /// executor can compute the next jittered + backoff-aware poll interval.
    /// </summary>
    internal async Task<(CycleResult Result, CalibrationSnapshotRuntimeConfig Config)> RunCycleAsync(CancellationToken ct)
    {
        var cycleStart = Stopwatch.GetTimestamp();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        CalibrationSnapshotRuntimeConfig config;
        await using (var cfgScope = _scopeFactory.CreateAsyncScope())
        {
            var readCtx = cfgScope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            config = await _configReader.LoadAsync(readCtx.GetDbContext(), ct);
        }

        // Cycle-level distributed lock — only one replica drives backfill per cycle.
        await using var cycleLock = config.UseCycleLock
            ? await TryAcquireCycleLockAsync(config, ct)
            : NoopAsyncDisposable.Instance;
        if (cycleLock is null)
        {
            _metrics.CalibrationSnapshotCycleLockAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "busy"));
            _logger.LogDebug("{Worker}: another replica holds the cycle lock — skipping cycle.", WorkerName);
            RecordCycleDuration(cycleStart, "cycle_lock_busy");
            return (new CycleResult(0, 0, 0, 0, 0), config);
        }
        if (config.UseCycleLock)
            _metrics.CalibrationSnapshotCycleLockAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "acquired"));

        var currentMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        int  monthsProcessed          = 0;
        int  monthsSkippedAlreadyDone = 0;
        int  monthsSkippedEmpty       = 0;
        int  monthsFailed             = 0;
        long snapshotsWritten         = 0;

        for (int i = 1; i <= config.BackfillMonths; i++)
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
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Two writers raced past the existence guard; the unique index won. Record
                // as AlreadyExists, not a failure, since the data was correctly written by
                // the other replica.
                _logger.LogInformation(
                    "{Worker}: unique-constraint race on period {PeriodStart:yyyy-MM} — treating as already-exists.",
                    WorkerName, periodStart);
                monthsSkippedAlreadyDone++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Worker}: failed to process period {PeriodStart:yyyy-MM} — continuing with remaining months",
                    WorkerName, periodStart);
                monthsFailed++;
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", WorkerName));
            }
        }

        // Always log so a quiet cycle still emits an "alive" signal.
        _logger.LogInformation(
            "{Worker}: cycle complete — processed={Processed} alreadyExists={AlreadyExists} empty={Empty} failed={Failed} rowsWritten={Rows}",
            WorkerName, monthsProcessed, monthsSkippedAlreadyDone, monthsSkippedEmpty, monthsFailed, snapshotsWritten);

        await UpdateFleetSystemicAlertAsync(monthsFailed, monthsProcessed, monthsSkippedEmpty + monthsSkippedAlreadyDone, config, now, ct);
        await UpdateStalenessAlertAsync(config, now, ct);

        RecordCycleDuration(cycleStart, "ok");
        return (new CycleResult(monthsProcessed, monthsSkippedAlreadyDone, monthsSkippedEmpty, monthsFailed, snapshotsWritten), config);
    }

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

        var loadStart = Stopwatch.GetTimestamp();
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
        _metrics.CalibrationSnapshotMonthLoadMs.Record(
            Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds,
            new KeyValuePair<string, object?>("period", GranularityMonthly));

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

        var insertStart = Stopwatch.GetTimestamp();
        await writeCtx.SaveChangesAsync(ct);
        _metrics.CalibrationSnapshotMonthInsertMs.Record(
            Stopwatch.GetElapsedTime(insertStart).TotalMilliseconds,
            new KeyValuePair<string, object?>("period", GranularityMonthly));

        // Metric only after the save — a rolled-back batch must not inflate it.
        _metrics.CalibrationSnapshotsWritten.Add(aggregates.Count,
            new KeyValuePair<string, object?>("period", GranularityMonthly));

        return new MonthResult(MonthOutcome.Written, aggregates.Count);
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
