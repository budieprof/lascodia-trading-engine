using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes and maintains an Exponentially-Weighted Moving Average (EWMA) accuracy for
/// each active ML model, providing a faster-responding live performance signal than the
/// equal-weighted rolling accuracy used by <c>MLRollingAccuracyWorker</c>.
///
/// <b>EWMA formula:</b>
/// <c>ewma_t = α × outcome_t + (1−α) × ewma_{t-1}</c>
/// where <c>outcome_t ∈ {1, 0}</c> (correct / incorrect).
///
/// With α = 0.05, the EWMA responds to a directional accuracy change approximately
/// 3–5× faster than an equal-weighted 30-prediction rolling window:
/// a sustained change in model behaviour becomes visible within ~20 predictions
/// rather than 30.
///
/// <b>Alert tiers:</b>
/// <list type="bullet">
///   <item>EWMA &lt; <c>WarnThreshold</c> (default 0.50): <c>Warning</c> alert.</item>
///   <item>EWMA &lt; <c>CriticalThreshold</c> (default 0.48): <c>Critical</c> alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLEwma:PollIntervalSeconds</c>  — default 600 (10 min)</item>
///   <item><c>MLEwma:Alpha</c>                — smoothing factor, default 0.05</item>
///   <item><c>MLEwma:MinPredictions</c>       — warm-up count before alerting, default 20</item>
///   <item><c>MLEwma:WarnThreshold</c>        — warning alert floor, default 0.50</item>
///   <item><c>MLEwma:CriticalThreshold</c>    — critical alert floor, default 0.48</item>
///   <item><c>MLEwma:AlertDestination</c>     — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLEwmaAccuracyWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    // All config is read live from the EngineConfig table each poll cycle.
    // MLEwmaAccuracyWorker polls at 10-minute intervals by default — much more
    // frequently than the other accuracy workers — because the EWMA is the primary
    // real-time degradation signal consumed by downstream suppression logic.
    private const string CK_PollSecs   = "MLEwma:PollIntervalSeconds";
    private const string CK_Alpha      = "MLEwma:Alpha";
    private const string CK_MinPreds   = "MLEwma:MinPredictions";
    private const string CK_WarnThr    = "MLEwma:WarnThreshold";
    private const string CK_CritThr    = "MLEwma:CriticalThreshold";
    private const string CK_AlertDest  = "MLEwma:AlertDestination";

    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly ILogger<MLEwmaAccuracyWorker>  _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory for creating per-poll scoped service lifetimes, ensuring DbContexts
    /// are cleanly disposed after each computation cycle.
    /// </param>
    /// <param name="logger">Structured logger for computation diagnostics and alerts.</param>
    public MLEwmaAccuracyWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLEwmaAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs an infinite polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope for scoped DB context lifetimes.</item>
    ///   <item>Reads the current poll interval from <see cref="EngineConfig"/>.</item>
    ///   <item>Delegates to <see cref="UpdateEwmaAsync"/> to run incremental EWMA updates
    ///         and threshold checks for all active models.</item>
    ///   <item>Sleeps for <c>pollSecs</c> before the next cycle.</item>
    /// </list>
    /// Default poll interval is 600 seconds (10 minutes), making this the most
    /// frequently running accuracy worker in the pipeline.
    /// Non-cancellation exceptions are caught and logged to keep the worker alive.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLEwmaAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval used when the config key is absent.
            int pollSecs = 600;

            try
            {
                // Fresh scope per iteration keeps EF change tracking isolated.
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read interval live so operators can tune without restart.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 600, stoppingToken);

                await UpdateEwmaAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Transient errors must not crash the watchdog permanently.
                _logger.LogError(ex, "MLEwmaAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLEwmaAccuracyWorker stopping.");
    }

    // ── EWMA update core ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads global EWMA parameters for the current poll cycle, then iterates
    /// all active ML models and calls <see cref="UpdateModelEwmaAsync"/> for each,
    /// isolating failures per model so that one bad model cannot block the rest.
    /// </summary>
    /// <param name="readCtx">Read-only DbContext for fetching models, logs, and alert state.</param>
    /// <param name="writeCtx">Write DbContext for upserting EWMA rows and inserting alerts.</param>
    /// <param name="ct">Cancellation token checked between model updates.</param>
    private async Task UpdateEwmaAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load all EWMA parameters once per cycle.
        double alpha         = await GetConfigAsync<double>(readCtx, CK_Alpha,     0.05,    ct);
        int    minPredictions = await GetConfigAsync<int>  (readCtx, CK_MinPreds,  20,      ct);
        double warnThreshold  = await GetConfigAsync<double>(readCtx, CK_WarnThr,  0.50,    ct);
        double critThreshold  = await GetConfigAsync<double>(readCtx, CK_CritThr,  0.48,    ct);
        string alertDest      = await GetConfigAsync<string>(readCtx, CK_AlertDest,"ml-ops", ct);

        // Only update EWMA for actively deployed models.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Allow fast shutdown between model updates.
            ct.ThrowIfCancellationRequested();

            try
            {
                await UpdateModelEwmaAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    alpha, minPredictions, warnThreshold, critThreshold, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-model failures.
                _logger.LogWarning(ex,
                    "EwmaAccuracy: update failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Performs an incremental EWMA update for a single ML model:
    /// <list type="number">
    ///   <item>Loads the model's existing EWMA state row (if any) from
    ///         <see cref="MLModelEwmaAccuracy"/> to resume from the last known EWMA
    ///         value and prediction timestamp.</item>
    ///   <item>Queries only prediction logs that are newer than the last processed
    ///         timestamp (<c>since</c>), making each poll O(new predictions) rather
    ///         than O(all predictions).</item>
    ///   <item>Applies the EWMA recurrence <c>ewma = α × outcome + (1−α) × ewma</c>
    ///         for each new resolved prediction, in chronological order.</item>
    ///   <item>Upserts the updated EWMA state row.</item>
    ///   <item>Checks the updated EWMA against the warning and critical thresholds;
    ///         raises a tiered <see cref="AlertType.MLModelDegraded"/> alert if
    ///         the warm-up period has elapsed and the EWMA is below the warning floor.</item>
    /// </list>
    ///
    /// <b>Cold-start seeding:</b> When no prior EWMA row exists, the EWMA is seeded at
    /// 0.5 (50% — random chance baseline). This is a neutral starting point that lets
    /// the EWMA drift toward the true accuracy once enough predictions are observed.
    ///
    /// <b>Incremental vs recomputed:</b> The EWMA is maintained incrementally rather
    /// than recomputed from scratch each poll. This is intentional — it means the
    /// EWMA remembers history beyond the look-back window, providing a longer-memory
    /// signal that is complementary to the fixed-window rolling accuracy computed by
    /// <c>MLRollingAccuracyWorker</c>.
    ///
    /// <b>Alert deduplication:</b> Alerts are only created when no active
    /// <see cref="AlertType.MLModelDegraded"/> alert already exists for the symbol,
    /// preventing alert storms during extended degradation periods.
    /// </summary>
    /// <param name="modelId">Primary key of the ML model being updated.</param>
    /// <param name="symbol">Instrument symbol (e.g., "EUR_USD").</param>
    /// <param name="timeframe">Candle timeframe for this model.</param>
    /// <param name="alpha">
    /// EWMA smoothing factor α ∈ (0, 1). Lower values (e.g. 0.05) produce a slower,
    /// smoother signal; higher values react faster but are noisier.
    /// </param>
    /// <param name="minPredictions">
    /// Minimum total predictions required before threshold alerting is active.
    /// This is the EWMA warm-up guard — prevents false alerts during the first few
    /// predictions when the EWMA has not yet converged.
    /// </param>
    /// <param name="warnThreshold">EWMA value below which a warning alert is raised.</param>
    /// <param name="critThreshold">EWMA value below which the alert is escalated to critical.</param>
    /// <param name="alertDest">Destination identifier for the alert (e.g., "ml-ops").</param>
    /// <param name="readCtx">Read DbContext for loading existing EWMA state and logs.</param>
    /// <param name="writeCtx">Write DbContext for upserting EWMA rows and inserting alerts.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task UpdateModelEwmaAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        double                                  alpha,
        int                                     minPredictions,
        double                                  warnThreshold,
        double                                  critThreshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load existing EWMA state (null on first run for this model).
        // AsNoTracking is used here because we will write via writeCtx, not readCtx.
        var existing = await readCtx.Set<MLModelEwmaAccuracy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.MLModelId == modelId, ct);

        // Only fetch prediction logs newer than the last processed timestamp.
        // This makes each poll incremental — we only process the "new" predictions
        // since the previous cycle, never re-processing the full history.
        var since = existing?.LastPredictionAt ?? DateTime.MinValue;

        var newLogs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId  &&
                        l.DirectionCorrect != null      &&
                        l.PredictedAt      > since      &&  // strictly newer than last run
                        !l.IsDeleted)
            .OrderBy(l => l.PredictedAt)    // chronological order required for EWMA
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, Correct = l.DirectionCorrect!.Value })
            .ToListAsync(ct);

        // Nothing to update if no new resolved predictions have arrived.
        if (newLogs.Count == 0) return;

        // Resume EWMA from the last persisted state, or cold-start at 50%.
        // Seeding at 0.5 (random-chance baseline) is a neutral default that avoids
        // biasing the EWMA toward a "good" or "bad" starting position.
        double ewma  = existing?.EwmaAccuracy ?? 0.5;
        int    total = existing?.TotalPredictions ?? 0;
        DateTime lastAt = existing?.LastPredictionAt ?? DateTime.MinValue;

        foreach (var log in newLogs)
        {
            // Convert boolean outcome to 1.0 (correct) or 0.0 (incorrect).
            double outcome = log.Correct ? 1.0 : 0.0;

            // EWMA recurrence: ewma_t = α * outcome_t + (1 - α) * ewma_{t-1}
            // Recent outcomes are weighted α; history decays geometrically by (1 - α).
            ewma = alpha * outcome + (1.0 - alpha) * ewma;
            total++;
            lastAt = log.PredictedAt;
        }

        var now = DateTime.UtcNow;

        // Upsert the EWMA state row — update if exists, insert if first run.
        int rows = await writeCtx.Set<MLModelEwmaAccuracy>()
            .Where(r => r.MLModelId == modelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.EwmaAccuracy,     ewma)
                .SetProperty(r => r.Alpha,             alpha)
                .SetProperty(r => r.TotalPredictions,  total)
                .SetProperty(r => r.LastPredictionAt,  lastAt)
                .SetProperty(r => r.ComputedAt,        now),
                ct);

        if (rows == 0)
        {
            // First computation for this model — insert a new row.
            writeCtx.Set<MLModelEwmaAccuracy>().Add(new MLModelEwmaAccuracy
            {
                MLModelId        = modelId,
                Symbol           = symbol,
                Timeframe        = timeframe,
                EwmaAccuracy     = ewma,
                Alpha            = alpha,
                TotalPredictions = total,
                LastPredictionAt = lastAt,
                ComputedAt       = now,
            });
            await writeCtx.SaveChangesAsync(ct);
        }

        _logger.LogDebug(
            "EwmaAccuracy: model {Id} ({Symbol}/{Tf}) — ewma={Ewma:P2} n={N} (+{New} new)",
            modelId, symbol, timeframe, ewma, total, newLogs.Count);

        // ── Alerting ──────────────────────────────────────────────────────────

        // Guard: do not alert until the EWMA has warmed up to a meaningful estimate.
        // Without this guard a cold-start at 0.5 with a first wrong prediction would
        // immediately drop EWMA to ~0.475 and trigger a spurious alert.
        if (total < minPredictions) return;

        // No alert needed — EWMA is above the warning floor.
        if (ewma >= warnThreshold)  return;

        // Determine severity tier: critical if below the lower threshold, else warning.
        string severity = ewma < critThreshold ? "critical" : "warning";

        // Deduplicate: only create an alert if no active MLModelDegraded alert already
        // exists for this symbol. This prevents alert storms during sustained degradation.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        _logger.LogWarning(
            "EwmaAccuracy: model {Id} ({Symbol}/{Tf}) — EWMA={Ewma:P2} below {Severity} threshold. n={N}",
            modelId, symbol, timeframe, ewma, severity, total);

        // Persist the alert with a rich JSON payload for actionable diagnostics.
        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason           = "ewma_accuracy_degraded",
                severity,
                symbol,
                timeframe        = timeframe.ToString(),
                modelId,
                ewmaAccuracy     = ewma,
                alpha,
                warnThreshold,
                criticalThreshold = critThreshold,
                totalPredictions  = total,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Returns <paramref name="defaultValue"/> when the key is absent or the stored
    /// string cannot be converted to <typeparamref name="T"/>.
    /// This allows live tuning of worker thresholds without a service restart.
    /// </summary>
    /// <typeparam name="T">Target CLR type (e.g. <c>int</c>, <c>double</c>, <c>string</c>).</typeparam>
    /// <param name="ctx">DbContext to query.</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> to look up.</param>
    /// <param name="defaultValue">Fallback value used when the key is missing or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed config value or <paramref name="defaultValue"/>.</returns>
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
