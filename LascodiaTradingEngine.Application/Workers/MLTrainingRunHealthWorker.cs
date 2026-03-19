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

    public MLTrainingRunHealthWorker(
        IServiceScopeFactory                   scopeFactory,
        ILogger<MLTrainingRunHealthWorker>     logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLTrainingRunHealthWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 1800;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

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

    private async Task CheckHealthAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowRuns    = await GetConfigAsync<int>   (readCtx, CK_WindowRuns, 10,      ct);
        double failRateThr   = await GetConfigAsync<double>(readCtx, CK_FailRate,   0.30,    ct);
        int    maxRunMins    = await GetConfigAsync<int>   (readCtx, CK_MaxRunMins, 120,     ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,  "ml-ops", ct);

        var now = DateTime.UtcNow;

        // ── 1. Stalled run detection ──────────────────────────────────────────
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

            bool alertExists = await readCtx.Set<Alert>()
                .AnyAsync(a => a.Symbol    == run.Symbol            &&
                               a.AlertType == AlertType.MLModelDegraded &&
                               a.IsActive  && !a.IsDeleted, ct);

            if (!alertExists)
            {
                writeCtx.Set<Alert>().Add(new Alert
                {
                    AlertType     = AlertType.MLModelDegraded,
                    Symbol        = run.Symbol,
                    Channel       = AlertChannel.Webhook,
                    Destination   = alertDest,
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
        // Load the distinct symbol/timeframe pairs that have recent runs
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

        if (recentStatuses.Count < 3) return; // not enough history

        int    failed    = recentStatuses.Count(s => s == RunStatus.Failed);
        double failRate  = (double)failed / recentStatuses.Count;

        _logger.LogDebug(
            "RunHealth: {Symbol}/{Tf} — last {N} runs: {F} failed ({Rate:P1})",
            symbol, timeframe, recentStatuses.Count, failed, failRate);

        if (failRate <= failRateThreshold) return;

        _logger.LogWarning(
            "RunHealth: {Symbol}/{Tf} — {Rate:P1} failure rate over last {N} runs " +
            "(threshold {Thr:P0}).",
            symbol, timeframe, failRate, recentStatuses.Count, failRateThreshold);

        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
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
