using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors the health of the <see cref="MLTrainingRun"/> execution pipeline per
/// symbol/timeframe and alerts on two failure modes that are invisible to accuracy
/// or drift workers:
///
/// <list type="bullet">
///   <item><b>High failure rate</b> — when more than <c>FailRateThreshold</c> of the
///         last <c>WindowRuns</c> completed runs have <c>Status = Failed</c>, fresh
///         models cannot be produced regardless of how many retraining triggers fire.</item>
///   <item><b>Stalled run</b> — when a run has remained in <c>Running</c> state for
///         longer than <c>MaxRunMinutes</c> without completing, the training worker
///         may be deadlocked or crashed. The run is flagged so operators can
///         intervene or the orchestrator can reschedule.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRunHealth:PollIntervalSeconds</c>  — default 1800 (30 min)</item>
///   <item><c>MLRunHealth:WindowRuns</c>           — last N runs to evaluate, default 10</item>
///   <item><c>MLRunHealth:FailRateThreshold</c>    — failure fraction alert, default 0.30</item>
///   <item><c>MLRunHealth:MaxRunMinutes</c>        — stall detection ceiling, default 120</item>
///   <item><c>MLRunHealth:AlertDestination</c>     — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLTrainingRunHealthWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLRunHealth:PollIntervalSeconds";
    private const string CK_WindowRuns = "MLRunHealth:WindowRuns";
    private const string CK_FailRate   = "MLRunHealth:FailRateThreshold";
    private const string CK_MaxRunMins = "MLRunHealth:MaxRunMinutes";
    private const string CK_AlertDest  = "MLRunHealth:AlertDestination";

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLTrainingRunHealthWorker>    _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each health check pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLTrainingRunHealthWorker(
        IServiceScopeFactory                   scopeFactory,
        ILogger<MLTrainingRunHealthWorker>     logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLRunHealth:PollIntervalSeconds</c>
    /// seconds (default 1800 = 30 min), reading the interval from <see cref="EngineConfig"/>
    /// on each cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTrainingRunHealthWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default 30-minute poll interval; refreshed from DB on every cycle.
            int pollSecs = 1800;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Refresh poll interval from DB each cycle to support hot-reload.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 1800, stoppingToken);

                await CheckHealthAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLTrainingRunHealthWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLTrainingRunHealthWorker stopping.");
    }

    // ── Health check core ─────────────────────────────────────────────────────

    /// <summary>
    /// Master health check orchestrator. Reads all configurable thresholds once per
    /// cycle, then performs two independent checks:
    /// <list type="number">
    ///   <item>Stalled run detection — finds training runs stuck in Running state
    ///         beyond <c>MLRunHealth:MaxRunMinutes</c>.</item>
    ///   <item>Per symbol/timeframe failure-rate check — raises alerts when more than
    ///         <c>MLRunHealth:FailRateThreshold</c> of the last <c>MLRunHealth:WindowRuns</c>
    ///         runs have failed.</item>
    /// </list>
    /// </summary>
    /// <param name="readCtx">Read-only EF context for runs and existing alerts.</param>
    /// <param name="writeCtx">Write EF context for inserting new alert records.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task CheckHealthAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read all health thresholds once per cycle to avoid per-check DB round trips.
        int    windowRuns    = await GetConfigAsync<int>   (readCtx, CK_WindowRuns, 10,      ct);
        double failRateThr   = await GetConfigAsync<double>(readCtx, CK_FailRate,   0.30,    ct);
        int    maxRunMins    = await GetConfigAsync<int>   (readCtx, CK_MaxRunMins, 120,     ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "ml-ops", ct);

        var now = DateTime.UtcNow;

        // ── 1. Stalled run detection ──────────────────────────────────────────
        // A run is stalled when it has been in Running state (PickedUpAt is set by
        // MLTrainingWorker when it takes ownership) for longer than maxRunMins without
        // completing. This typically indicates an OOM kill, deadlock, or unhandled
        // exception that terminated the training process without updating the run status.
        var stalled = await readCtx.Set<MLTrainingRun>()
            .Where(r => r.Status    == RunStatus.Running       &&
                        r.PickedUpAt != null                    &&
                        r.PickedUpAt < now.AddMinutes(-maxRunMins) &&
                        !r.IsDeleted)
            .AsNoTracking()
            .Select(r => new { r.Id, r.Symbol, r.Timeframe, r.PickedUpAt })
            .ToListAsync(ct);

        foreach (var run in stalled)
        {
            double stalledMins = (now - run.PickedUpAt!.Value).TotalMinutes;
            _logger.LogWarning(
                "RunHealth: training run {Id} ({Symbol}/{Tf}) has been Running for {Mins:F0} min " +
                "(threshold {Max} min). Possible stall.",
                run.Id, run.Symbol, run.Timeframe, stalledMins, maxRunMins);

            // Check whether an active stall alert already exists for this symbol to avoid
            // flooding the alert channel with repeated notifications on every poll cycle.
            bool alertExists = await readCtx.Set<Alert>()
                .AnyAsync(a => a.Symbol    == run.Symbol            &&
                               a.AlertType == AlertType.MLModelDegraded &&
                               a.IsActive  && !a.IsDeleted, ct);

            if (!alertExists)
            {
                // Critical severity: a stalled training run blocks all model updates
                // for this symbol until the run is manually cancelled or the process restarts.
                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType     = AlertType.MLModelDegraded,
                    Symbol        = run.Symbol,
                    ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        reason         = "training_run_stalled",
                        severity       = "critical",
                        symbol         = run.Symbol,
                        timeframe      = run.Timeframe.ToString(),
                        runId          = run.Id,
                        stalledMinutes = stalledMins,
                        maxRunMinutes  = maxRunMins,
                    }),
                    IsActive = true,
                });
                await writeCtx.SaveChangesAsync(ct);
            }
        }

        // ── 2. Per symbol/timeframe failure-rate check ────────────────────────
        // A high failure rate means retraining triggers are firing but no new models are
        // being produced — the ML pipeline appears active but model quality is stagnating.
        // Load all distinct symbol/timeframe pairs that have any training run history.
        var pairs = await readCtx.Set<MLTrainingRun>()
            .Where(r => !r.IsDeleted)
            .AsNoTracking()
            .Select(r => new { r.Symbol, r.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckPairFailRateAsync(
                    pair.Symbol, pair.Timeframe,
                    windowRuns, failRateThr, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RunHealth: failure-rate check error for {Symbol}/{Tf} — skipping.",
                    pair.Symbol, pair.Timeframe);
            }
        }
    }

    /// <summary>
    /// Checks the failure rate of the last <paramref name="windowRuns"/> terminal training
    /// runs for a given symbol/timeframe. Creates a warning-severity alert if the failure
    /// fraction exceeds <paramref name="failRateThreshold"/>.
    /// </summary>
    /// <remarks>
    /// Training run health monitoring:
    ///
    /// A high failure rate (e.g. 30%+ of recent runs failing) indicates a systemic issue
    /// with the training pipeline — typically caused by data quality problems (missing
    /// candles, schema mismatches), resource exhaustion (OOM, disk full), or bugs introduced
    /// by a recent deployment. The failure rate check catches this even when individual
    /// runs are silently retried and the queue appears to be draining normally.
    ///
    /// Minimum 3 runs required before computing the rate to prevent false positives during
    /// the initial ramp-up of a new symbol.
    ///
    /// Alert deduplication: only one active alert per symbol is created at a time.
    /// </remarks>
    /// <param name="symbol">Trading symbol to check.</param>
    /// <param name="timeframe">Candle timeframe to check.</param>
    /// <param name="windowRuns">Number of most-recent terminal runs to evaluate.</param>
    /// <param name="failRateThreshold">Failure fraction above which an alert is raised.</param>
    /// <param name="alertDest">Alert destination (e.g. team webhook channel name).</param>
    /// <param name="readCtx">Read-only EF context for run statuses and existing alerts.</param>
    /// <param name="writeCtx">Write EF context for inserting new alert records.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task CheckPairFailRateAsync(
        string                                  symbol,
        Timeframe                               timeframe,
        int                                     windowRuns,
        double                                  failRateThreshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load the last windowRuns completed or failed runs, most-recent first.
        // Queued and Running runs are excluded — only terminal states count.
        var recentStatuses = await readCtx.Set<MLTrainingRun>()
            .Where(r => r.Symbol    == symbol     &&
                        r.Timeframe == timeframe   &&
                        (r.Status   == RunStatus.Completed || r.Status == RunStatus.Failed) &&
                        !r.IsDeleted)
            .OrderByDescending(r => r.StartedAt)
            .Take(windowRuns)
            .AsNoTracking()
            .Select(r => r.Status)
            .ToListAsync(ct);

        // Require at least 3 terminal runs for a statistically meaningful failure rate.
        if (recentStatuses.Count < 3) return;

        int    failed    = recentStatuses.Count(s => s == RunStatus.Failed);
        double failRate  = (double)failed / recentStatuses.Count;

        _logger.LogDebug(
            "RunHealth: {Symbol}/{Tf} — last {N} runs: {F} failed ({Rate:P1})",
            symbol, timeframe, recentStatuses.Count, failed, failRate);

        // Failure rate is within acceptable bounds — no action required.
        if (failRate <= failRateThreshold) return;

        _logger.LogWarning(
            "RunHealth: {Symbol}/{Tf} — {Rate:P1} failure rate over last {N} runs " +
            "(threshold {Thr:P0}).",
            symbol, timeframe, failRate, recentStatuses.Count, failRateThreshold);

        // Deduplication: skip alert creation if one already exists for this symbol.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        // Warning severity: models are not being refreshed but the pipeline is not
        // completely down — other symbols may still be training successfully.
        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason             = "training_run_high_failure_rate",
                severity           = "warning",
                symbol,
                timeframe          = timeframe.ToString(),
                failedRuns         = failed,
                totalRuns          = recentStatuses.Count,
                failureRate        = failRate,
                failRateThreshold,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table,
    /// falling back to <paramref name="defaultValue"/> if the key is absent or
    /// the stored value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target value type (int, double, string, etc.).</typeparam>
    /// <param name="ctx">EF Core context to query against.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Value to return when the key is missing or invalid.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed config value or <paramref name="defaultValue"/>.</returns>
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
