using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects silent ML scorer failures by auditing whether approved <see cref="TradeSignal"/>
/// records produced corresponding <see cref="MLModelPredictionLog"/> entries.
///
/// <b>Problem:</b> If <c>MLSignalScorer</c> throws an exception that is caught and swallowed
/// by the calling strategy worker, signals flow through to order execution without any ML
/// evaluation. Aggregate accuracy metrics won't detect this because fewer logs are written —
/// the denominator shrinks silently alongside the numerator.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each active model (symbol/timeframe), count <see cref="TradeSignal"/> records
///         with <c>Status = Approved</c> in the audit window.</item>
///   <item>Count <see cref="MLModelPredictionLog"/> records created in the same window for
///         the same symbol, regardless of model (covers the case where a model was swapped
///         mid-window).</item>
///   <item>If <c>uncoveredFraction = (signals − logs) / signals &gt; CoverageGapThreshold</c>,
///         fire an <see cref="AlertType.MLModelDegraded"/> alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLCoverageAudit:PollIntervalSeconds</c>  — default 86400 (24 h)</item>
///   <item><c>MLCoverageAudit:AuditWindowHours</c>     — look-back window, default 24 h</item>
///   <item><c>MLCoverageAudit:MinSignals</c>           — minimum signals before auditing, default 5</item>
///   <item><c>MLCoverageAudit:CoverageGapThreshold</c> — max tolerated gap fraction, default 0.20</item>
///   <item><c>MLCoverageAudit:AlertDestination</c>     — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLSignalCoverageAuditWorker : BackgroundService
{
    // ── EngineConfig key constants ─────────────────────────────────────────────
    // All values are read from the EngineConfig table so they can be changed at
    // runtime without redeploying. The defaults are conservative enough to be safe
    // out of the box for a typical forex trading operation.

    /// <summary>How many seconds to sleep between audit cycles (default 86400 = 24 h).
    /// This worker runs less frequently than other ML workers because the audit is
    /// diagnostic only — it does not block signals in real time.</summary>
    private const string CK_PollSecs      = "MLCoverageAudit:PollIntervalSeconds";

    /// <summary>How many hours to look back when comparing signal counts to prediction log counts.</summary>
    private const string CK_WindowHours   = "MLCoverageAudit:AuditWindowHours";

    /// <summary>Minimum number of approved signals in the window before the audit runs.
    /// Prevents false positives on rarely-traded symbols with only 1–2 signals.</summary>
    private const string CK_MinSignals    = "MLCoverageAudit:MinSignals";

    /// <summary>Maximum fraction of approved signals allowed to have no corresponding prediction log
    /// before an alert is raised. A gap of 0.20 means more than 1-in-5 signals had no ML evaluation,
    /// which almost certainly indicates a scorer failure rather than normal operation.</summary>
    private const string CK_GapThreshold  = "MLCoverageAudit:CoverageGapThreshold";

    /// <summary>Alert destination (e.g. Webhook URL identifier or channel name) for gap alerts.</summary>
    private const string CK_AlertDest     = "MLCoverageAudit:AlertDestination";

    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly ILogger<MLSignalCoverageAuditWorker>   _logger;

    /// <summary>
    /// Initialises the worker with scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">Used to create per-iteration DI scopes so EF DbContexts
    /// are not shared across loop iterations.</param>
    /// <param name="logger">Structured logger for coverage audit events and gap warnings.</param>
    public MLSignalCoverageAuditWorker(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLSignalCoverageAuditWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Main background loop. Runs indefinitely until the host signals cancellation.
    /// On each iteration:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope to obtain scoped read/write DbContexts.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/> (supports hot-reload).</item>
    ///   <item>Delegates to <see cref="AuditCoverageAsync"/> to check all active models.</item>
    ///   <item>Sleeps for the configured poll interval (default 24 h) before the next cycle.</item>
    /// </list>
    /// The long default interval (24 h) is intentional — this is a diagnostic worker that
    /// generates alerts rather than taking automated corrective action. Running it more
    /// frequently would flood the alert channel with near-duplicate messages.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalCoverageAuditWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default to 24-hour poll interval in case the EngineConfig key is missing.
            int pollSecs = 86400;

            try
            {
                // Fresh scope per iteration — scoped EF DbContexts must not survive across loop ticks.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read interval from DB each cycle to honour hot-reload config changes.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 86400, stoppingToken);

                await AuditCoverageAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown — break without logging an error.
                break;
            }
            catch (Exception ex)
            {
                // Transient errors (e.g. DB connection loss) are logged and swallowed so
                // the worker remains alive and retries on the next cycle.
                _logger.LogError(ex, "MLSignalCoverageAuditWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalCoverageAuditWorker stopping.");
    }

    // ── Audit core ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads all active ML models and runs the coverage audit for each one.
    /// All parameters are read from <see cref="EngineConfig"/> at the start of each audit
    /// cycle, so changes take effect on the next run without a restart.
    /// Failures on individual models are isolated so one bad model cannot abort the
    /// remainder of the audit loop.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for querying models, signals, and logs.</param>
    /// <param name="writeCtx">Write DbContext for persisting gap alerts.</param>
    /// <param name="ct">Cancellation token propagated from the host.</param>
    private async Task AuditCoverageAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load all audit parameters in one batch before iterating over models.
        int    windowHours   = await GetConfigAsync<int>   (readCtx, CK_WindowHours,  24,      ct);
        int    minSignals    = await GetConfigAsync<int>   (readCtx, CK_MinSignals,   5,       ct);
        double gapThreshold  = await GetConfigAsync<double>(readCtx, CK_GapThreshold, 0.20,    ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,   "ml-ops", ct);

        // The audit cutoff: only signals and logs generated after this timestamp are considered.
        var cutoff = DateTime.UtcNow.AddHours(-windowHours);

        // ── Load distinct symbol/timeframe pairs from active models ──────────
        // Projecting to an anonymous type avoids loading ModelBytes (can be megabytes).
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        if (activeModels.Count == 0) return;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await AuditModelCoverageAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    cutoff, minSignals, gapThreshold, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                // Per-model failure isolation — log the failing model and continue.
                _logger.LogWarning(ex,
                    "CoverageAudit: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Runs the coverage audit for a single model:
    /// <list type="number">
    ///   <item>Counts <see cref="TradeSignal"/> records with <c>Status = Approved</c> in the window.
    ///         Approved is used (not Executed) because the scorer fires before risk approval —
    ///         if risk rejects the signal later, the scorer should still have logged a prediction.</item>
    ///   <item>Counts <see cref="MLModelPredictionLog"/> records for this model in the same window.</item>
    ///   <item>Computes the uncovered fraction: <c>(approvedSignals - predictionLogs) / approvedSignals</c>.</item>
    ///   <item>Raises an <see cref="AlertType.MLModelDegraded"/> alert when the gap exceeds the threshold.</item>
    ///   <item>Deduplicates alerts — skips creation if an active alert already exists for this symbol.</item>
    /// </list>
    /// </summary>
    /// <param name="modelId">Database ID of the model being audited.</param>
    /// <param name="symbol">Currency pair symbol (e.g. "EURUSD") the model covers.</param>
    /// <param name="timeframe">Timeframe the model was trained on (used for logging only; TradeSignal has no Tf field).</param>
    /// <param name="cutoff">Only records created on or after this timestamp are included in the audit.</param>
    /// <param name="minSignals">Minimum approved signals in the window before the gap check is meaningful.</param>
    /// <param name="gapThreshold">Maximum tolerated uncovered fraction before an alert fires.</param>
    /// <param name="alertDest">Webhook/channel destination for the alert.</param>
    /// <param name="readCtx">Read-only DbContext.</param>
    /// <param name="writeCtx">Write DbContext for alert creation.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task AuditModelCoverageAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                cutoff,
        int                                     minSignals,
        double                                  gapThreshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Count approved signals in the window for this symbol.
        // Note: TradeSignal doesn't have a Timeframe field, so we audit at symbol level.
        // This intentionally counts all approved signals regardless of which strategy generated
        // them — if ANY approved signal has no prediction log, the scorer may have been bypassed.
        int approvedSignals = await readCtx.Set<TradeSignal>()
            .CountAsync(ts => ts.Symbol == symbol            &&
                              ts.Status == TradeSignalStatus.Approved &&
                              ts.GeneratedAt >= cutoff       &&
                              !ts.IsDeleted, ct);

        // Skip the audit if there are too few signals to be meaningful.
        // A new symbol or one that trades infrequently may legitimately have fewer than minSignals
        // approved signals per day; auditing it would generate false positives.
        if (approvedSignals < minSignals)
        {
            _logger.LogDebug(
                "CoverageAudit: {Symbol}/{Tf} model {Id} — only {N} approved signals in window, skipping audit.",
                symbol, timeframe, modelId, approvedSignals);
            return;
        }

        // Count prediction logs created in the same window for this model.
        // We use MLModelId (not symbol) so we correctly attribute logs to the active model.
        // If two models were active in the window (e.g. after a promotion), the old model's
        // logs are not counted here — intentional, as we're auditing the currently active model.
        int predictionLogs = await readCtx.Set<MLModelPredictionLog>()
            .CountAsync(l => l.MLModelId == modelId &&
                             l.PredictedAt >= cutoff  &&
                             !l.IsDeleted, ct);

        // Compute how many approved signals have no corresponding prediction log.
        // Math.Max(0, ...) ensures we never report a negative uncovered count in edge cases
        // where the same signal triggered multiple prediction logs (e.g. retry scenarios).
        int    uncovered       = Math.Max(0, approvedSignals - predictionLogs);
        double uncoveredFrac   = approvedSignals > 0 ? (double)uncovered / approvedSignals : 0.0;

        _logger.LogDebug(
            "CoverageAudit: {Symbol}/{Tf} model {Id} — signals={Sig} logs={Logs} uncovered={Unc} ({Pct:P1})",
            symbol, timeframe, modelId, approvedSignals, predictionLogs, uncovered, uncoveredFrac);

        // If the gap is within tolerance, no action needed.
        if (uncoveredFrac <= gapThreshold) return;

        // Gap exceeds threshold — this is a strong signal of a silent scorer failure.
        // Signals are flowing to execution without ML evaluation, which degrades live performance.
        _logger.LogWarning(
            "CoverageAudit: {Symbol}/{Tf} model {Id} — {Pct:P1} of {Sig} approved signals have no " +
            "prediction log (threshold {Thr:P0}). Possible scorer failure.",
            symbol, timeframe, modelId, uncoveredFrac, approvedSignals, gapThreshold);

        // Deduplicate: only create an alert if one doesn't already exist.
        // This prevents alert flooding on repeated audit cycles when the issue persists.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        // Create a new MLModelDegraded alert with full diagnostic context in ConditionJson.
        // The AlertWorker will dispatch this to the configured channel (Webhook by default).
        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason          = "coverage_gap",
                severity        = "warning",
                symbol,
                timeframe       = timeframe.ToString(),
                modelId,
                approvedSignals,
                predictionLogs,
                uncoveredFraction = uncoveredFrac,
                // Capture the actual window in hours for clarity in alert dashboards.
                windowHours     = (int)(DateTime.UtcNow - cutoff).TotalHours,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key does not exist or the stored
    /// string value cannot be converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target type — typically <c>int</c>, <c>double</c>, or <c>string</c>.</typeparam>
    /// <param name="ctx">Any DbContext with access to the EngineConfig set.</param>
    /// <param name="key">The EngineConfig key to look up.</param>
    /// <param name="defaultValue">Fallback value returned when the key is absent or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed configuration value, or <paramref name="defaultValue"/>.</returns>
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
