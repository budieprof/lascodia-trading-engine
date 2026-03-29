using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Conformal Temporal Circuit Breaker. Identifies contiguous runs where an active model is
/// systematically over-confident and wrong (20 or more consecutive incorrect prediction logs),
/// then suspends signal generation by setting <c>IsSuppressed = true</c> on the model.
///
/// <b>Motivation:</b> a long run of consecutive incorrect predictions is strong statistical
/// evidence of a structural regime shift that the model has not adapted to — for example, a
/// market that has transitioned from trending to mean-reverting behaviour. In this state the
/// model's probability estimates are unreliable regardless of their nominal calibration and
/// acting on its signals would systematically increase drawdown.
///
/// <b>Suspension duration heuristic:</b> the suspension length is set to 2× the failing run
/// length, proxy-mapped at 4 hours per bar:
///
///   suspensionBars = runLength × 2
///   resumeAt       = now + suspensionBars × 4 hours
///
/// This gives the model time to accumulate new resolved predictions under the current regime
/// before being re-enabled, and scales the "cooling-off" period to the severity of the failure.
///
/// <b>Lifecycle per cycle:</b>
/// <list type="number">
///   <item>Expire and clear any active breakers whose <c>ResumeAt</c> timestamp has passed,
///         restoring the corresponding models to active signal generation.</item>
///   <item>Evaluate each active model's most recent prediction logs for the longest
///         consecutive run of incorrect predictions.</item>
///   <item>If the longest run ≥ <see cref="TriggerRunLength"/>, upsert an
///         <see cref="MLConformalBreakerLog"/> record and set <c>IsSuppressed = true</c>
///         on the model entity.</item>
/// </list>
///
/// Runs daily with a 35-minute initial startup delay to avoid competing with startup-time
/// migrations and initial data loading.
/// </summary>
public sealed class MLConformalBreakerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLConformalBreakerWorker> _logger;

    // How frequently the circuit-breaker evaluation runs.
    private static readonly TimeSpan _interval     = TimeSpan.FromDays(1);

    // Startup delay to avoid contention with migrations and initial data loading.
    private static readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(35);

    // Maximum number of most-recent resolved logs to examine per model.
    private const int MaxLogs          = 100;

    // Minimum resolved logs required to evaluate a model (fewer = unreliable run statistics).
    private const int MinLogs          = 30;

    // Number of consecutive incorrect predictions that trips the circuit breaker.
    // 20 consecutive misses is extremely unlikely by chance alone (~1/2^20 ≈ 10^-6 probability
    // if the model were randomly correct 50 % of the time), indicating a systematic failure.
    private const int TriggerRunLength = 20;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per run cycle.</param>
    /// <param name="logger">Structured logger for suspension and resumption events.</param>
    public MLConformalBreakerWorker(IServiceScopeFactory scopeFactory, ILogger<MLConformalBreakerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Waits <see cref="_initialDelay"/> (35 min) before starting,
    /// then evaluates all models once per day.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLConformalBreakerWorker started.");
        // Initial delay prevents race conditions with startup migrations and data loading.
        await Task.Delay(_initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLConformalBreakerWorker error"); }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Core circuit-breaker evaluation routine, executed once per daily cycle.
    ///
    /// <b>Phase 1 — Expiry sweep:</b> loads all active <see cref="MLConformalBreakerLog"/>
    /// records whose <c>ResumeAt</c> has passed and clears them, restoring the corresponding
    /// model's <c>IsSuppressed</c> flag to false.
    ///
    /// <b>Phase 2 — Model evaluation:</b> for each active model, loads the most recent
    /// <see cref="MaxLogs"/> resolved prediction logs and scans for the longest contiguous
    /// run of consecutive incorrect predictions using a single linear pass. If the run meets
    /// or exceeds <see cref="TriggerRunLength"/>, the model is suspended and a
    /// <see cref="MLConformalBreakerLog"/> is upserted.
    ///
    /// <b>Suspension duration:</b>
    ///   suspensionBars = maxRun × 2       (proportional to failure severity)
    ///   resumeAt       = now + suspensionBars × 4 hours
    ///
    /// The 4-hours-per-bar proxy maps the bar count to wall-clock time assuming a roughly
    /// 4-hour typical timeframe (configurable future improvement).
    /// </summary>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        var now = DateTime.UtcNow;

        // ── Phase 1: Clear expired circuit breakers ────────────────────────────
        // A breaker expires when its ResumeAt timestamp has passed, meaning the
        // cooling-off period is over and the model is eligible to generate signals again.
        var expiredBreakers = await writeDb.Set<MLConformalBreakerLog>()
            .Where(b => b.IsActive && b.ResumeAt <= now)
            .ToListAsync(ct);

        foreach (var breaker in expiredBreakers)
        {
            // Deactivate the breaker log record.
            breaker.IsActive = false;

            // Restore the model's IsSuppressed flag only when no other active
            // suppression reason is still holding the model offline.
            var suppressed = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == breaker.MLModelId, ct);
            if (suppressed is not null &&
                await MLSuppressionStateHelper.CanLiftSuppressionAsync(
                    writeDb, suppressed, ct, ignoreConformalBreakerId: breaker.Id))
                suppressed.IsSuppressed = false;

            _logger.LogInformation(
                "MLConformalBreakerWorker: RESUMED {Symbol}/{Timeframe} — breaker expired.",
                breaker.Symbol, breaker.Timeframe);
        }

        // ── Phase 2: Evaluate active models for new breaker conditions ─────────
        var activeModels = await readDb.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Load the most recent resolved prediction logs for this model, then
            // reorder them oldest->newest so the run scan preserves time order.
            var recentLogs = await readDb.Set<MLModelPredictionLog>()
                .Where(l => l.MLModelId == model.Id
                            && l.DirectionCorrect != null
                            && l.OutcomeRecordedAt != null
                            && !l.IsDeleted)
                .OrderByDescending(l => l.OutcomeRecordedAt)
                .ThenByDescending(l => l.Id)
                .Take(MaxLogs)
                .ToListAsync(ct);

            var logs = recentLogs
                .OrderBy(l => l.OutcomeRecordedAt)
                .ThenBy(l => l.Id)
                .ToList();

            // Need at least MinLogs resolved predictions for a statistically meaningful check.
            if (logs.Count < MinLogs) continue;

            // Single linear pass to find the longest consecutive run of incorrect predictions.
            // This is O(n) in the number of logs, making it efficient even for MaxLogs = 100.
            int maxRun     = 0;
            int currentRun = 0;
            foreach (var log in logs)
            {
                if (log.DirectionCorrect == false)
                {
                    // Extend the current failing run.
                    currentRun++;
                    if (currentRun > maxRun) maxRun = currentRun;
                }
                else
                {
                    // A correct prediction resets the consecutive run counter.
                    currentRun = 0;
                }
            }

            // Empirical coverage = fraction of predictions that were correct.
            // Stored in the breaker log for diagnostic purposes.
            int totalCorrect = logs.Count(l => l.DirectionCorrect == true);
            double empiricalCoverage = totalCorrect / (double)logs.Count;

            // Check whether the maximum run length meets the trigger threshold.
            if (maxRun < TriggerRunLength) continue;

            // Compute the suspension window.
            // suspensionBars = 2 × maxRun (proportional to severity of failure)
            // resumeAt = now + suspensionBars × 4 hours (4-hour-per-bar proxy)
            int suspensionBars = maxRun * 2;
            var resumeAt       = now.AddHours(suspensionBars * 4.0);

            _logger.LogWarning(
                "MLConformalBreakerWorker: SUSPENDED {S}/{T} — {N} consecutive poor coverage bars",
                model.Symbol, model.Timeframe, maxRun);

            // Upsert the breaker log: update existing active record or insert a new one.
            var existing = await writeDb.Set<MLConformalBreakerLog>()
                .FirstOrDefaultAsync(
                    b => b.MLModelId == model.Id && b.IsActive,
                    ct);

            if (existing is not null)
            {
                // Refresh an already-active breaker with updated metrics.
                existing.ConsecutivePoorCoverageBars = maxRun;
                existing.EmpiricalCoverage           = empiricalCoverage;
                existing.SuspensionBars              = suspensionBars;
                existing.SuspendedAt                 = now;
                existing.ResumeAt                    = resumeAt;
            }
            else
            {
                // Create a new breaker record for this suspension event.
                await writeDb.Set<MLConformalBreakerLog>().AddAsync(new MLConformalBreakerLog
                {
                    MLModelId                    = model.Id,
                    Symbol                       = model.Symbol,
                    Timeframe                    = model.Timeframe,
                    ConsecutivePoorCoverageBars  = maxRun,
                    EmpiricalCoverage            = empiricalCoverage,
                    SuspensionBars               = suspensionBars,
                    SuspendedAt                  = now,
                    ResumeAt                     = resumeAt,
                    IsActive                     = true
                }, ct);
            }

            // Suppress the model: signal generation is blocked until ResumeAt passes.
            var writeModel = await writeDb.Set<MLModel>()
                .FirstOrDefaultAsync(m => m.Id == model.Id, ct);
            if (writeModel is not null)
                writeModel.IsSuppressed = true;
        }

        await writeDb.SaveChangesAsync(ct);
        _logger.LogInformation("MLConformalBreakerWorker: cycle complete.");
    }
}
