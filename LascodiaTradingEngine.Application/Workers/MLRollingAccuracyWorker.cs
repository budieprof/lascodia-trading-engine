using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Tracks the rolling direction accuracy for each active ML model using an
/// Exponentially-Weighted Moving Average (EMA), and fires an alert when the EMA
/// drops below a configurable accuracy floor.
///
/// <b>Accuracy dimension:</b> Rolling-window direction accuracy — whether the model's
/// predicted price direction (up / down) matches the actual next-candle close direction.
/// This is the primary, blended accuracy signal. Companion workers (
/// <c>MLRegimeAccuracyWorker</c>, <c>MLSessionAccuracyWorker</c>, etc.) slice this
/// same signal along finer dimensions (regime, session, volatility, time-of-day, horizon).
///
/// <b>Why EMA instead of a simple mean?</b>
/// An equal-weighted mean over the last N predictions treats a prediction made 30 days
/// ago equally with one made 10 minutes ago. Because model performance can drift
/// progressively, the EMA ages out older outcomes by the factor (1−α) per step —
/// giving recent predictions exponentially higher weight. With α = 0.10, an observation
/// from 20 steps ago carries only (0.90)^20 ≈ 12% of the weight of the latest one.
///
/// <b>Alert behaviour:</b> When the EMA drops below <c>AccuracyFloor</c>, a single
/// <see cref="AlertType.MLModelDegraded"/> alert is created per symbol (deduplicated
/// so that repeat polls do not spam the alert table while the model remains degraded).
///
/// <b>Pipeline position:</b> This worker runs every 2 hours (configurable) and acts as
/// the aggregate accuracy watchdog. Downstream consumers of accuracy data include
/// <c>MLSignalScorer</c> (confidence scaling) and <c>MLShadowArbiterWorker</c>
/// (promotion / suppression decisions).
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRollingAccuracy:PollIntervalSeconds</c> — default 7200 (2 h)</item>
///   <item><c>MLRollingAccuracy:WindowDays</c>          — resolved-log look-back, default 30</item>
///   <item><c>MLRollingAccuracy:MinResolved</c>          — skip model if fewer resolved logs, default 30</item>
///   <item><c>MLRollingAccuracy:EmaAlpha</c>             — EMA smoothing factor α ∈ (0,1), default 0.10</item>
///   <item><c>MLRollingAccuracy:AccuracyFloor</c>        — alert when EMA &lt; this value, default 0.50</item>
/// </list>
/// </summary>
public sealed class MLRollingAccuracyWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    // All config is read live from the EngineConfig table each poll cycle so that
    // operators can tune thresholds without restarting the service.
    private const string CK_PollSecs      = "MLRollingAccuracy:PollIntervalSeconds";
    private const string CK_WindowDays    = "MLRollingAccuracy:WindowDays";
    private const string CK_MinResolved   = "MLRollingAccuracy:MinResolved";
    private const string CK_EmaAlpha      = "MLRollingAccuracy:EmaAlpha";
    private const string CK_AccuracyFloor = "MLRollingAccuracy:AccuracyFloor";

    private readonly IServiceScopeFactory               _scopeFactory;
    private readonly ILogger<MLRollingAccuracyWorker>   _logger;

    /// <summary>
    /// Initialises the worker with a DI scope factory (used to create per-poll
    /// scoped service lifetimes) and a structured logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory used to create an <see cref="IServiceScope"/> for each polling iteration,
    /// ensuring scoped services (DbContexts) are properly disposed after each run.
    /// </param>
    /// <param name="logger">Structured logger for diagnostic and alert output.</param>
    public MLRollingAccuracyWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLRollingAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point for the hosted service. Runs a continuous polling loop that:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope to obtain scoped read/write DB contexts.</item>
    ///   <item>Reads the current poll interval from <see cref="EngineConfig"/> (live config).</item>
    ///   <item>Delegates to <see cref="CheckRollingAccuracyAsync"/> for the actual computation.</item>
    ///   <item>Sleeps for <c>pollSecs</c> before repeating.</item>
    /// </list>
    /// Exceptions other than cancellation are caught and logged so a transient DB error
    /// cannot crash the hosted service.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRollingAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval used if the config key is missing or unparseable.
            int pollSecs = 7200;

            try
            {
                // A new scope per iteration ensures EF change-tracking is clean and
                // scoped services (e.g. DbContext) are disposed after each poll.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read the poll interval each cycle so config changes take effect
                // without restarting the service.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 7200, stoppingToken);

                await CheckRollingAccuracyAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (Exception ex)
            {
                // Log and continue; a transient error (DB timeout, network blip) should
                // not permanently stop the accuracy watchdog.
                _logger.LogError(ex, "MLRollingAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRollingAccuracyWorker stopping.");
    }

    // ── Per-poll accuracy check ───────────────────────────────────────────────

    /// <summary>
    /// Reads global config parameters for the current poll cycle then iterates over
    /// every active ML model, delegating per-model accuracy evaluation to
    /// <see cref="CheckModelAccuracyAsync"/>.
    /// </summary>
    /// <param name="readCtx">Read-only EF DbContext for fetching models and prediction logs.</param>
    /// <param name="writeCtx">Write EF DbContext for persisting alert records.</param>
    /// <param name="ct">Cancellation token; checked between models to allow fast shutdown.</param>
    private async Task CheckRollingAccuracyAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Pull all accuracy parameters once per cycle to avoid repeated DB round-trips.
        int    windowDays    = await GetConfigAsync<int>   (readCtx, CK_WindowDays,    30,   ct);
        int    minResolved   = await GetConfigAsync<int>   (readCtx, CK_MinResolved,   30,   ct);
        double emaAlpha      = await GetConfigAsync<double>(readCtx, CK_EmaAlpha,      0.10, ct);
        double accuracyFloor = await GetConfigAsync<double>(readCtx, CK_AccuracyFloor, 0.50, ct);

        // Only evaluate models currently flagged as active; inactive/suppressed models
        // are excluded so we don't raise alerts for models that are intentionally offline.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            // Respect cancellation between models — each model's check can take
            // several hundred milliseconds on large prediction-log tables.
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelAccuracyAsync(
                    model, readCtx, writeCtx,
                    windowDays, minResolved, emaAlpha, accuracyFloor, ct);
            }
            catch (Exception ex)
            {
                // Isolate failures per model: one bad model must not block the rest.
                _logger.LogWarning(ex,
                    "Rolling accuracy check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Evaluates rolling direction accuracy for a single ML model by:
    /// <list type="number">
    ///   <item>Loading all resolved prediction log entries within the look-back window,
    ///         ordered chronologically (oldest first) for correct EMA initialisation.</item>
    ///   <item>Skipping the model if the resolved sample is smaller than
    ///         <paramref name="minResolved"/> (insufficient data for a reliable estimate).</item>
    ///   <item>Computing the EMA of the direction-correct indicator.</item>
    ///   <item>If the EMA is below <paramref name="accuracyFloor"/>, creating a
    ///         deduplicated <see cref="AlertType.MLModelDegraded"/> alert.</item>
    /// </list>
    /// </summary>
    /// <param name="model">The active ML model being evaluated.</param>
    /// <param name="readCtx">Read DbContext for logs and existing alerts.</param>
    /// <param name="writeCtx">Write DbContext for inserting new alert rows.</param>
    /// <param name="windowDays">Number of days to look back for resolved predictions.</param>
    /// <param name="minResolved">Minimum resolved sample size before computing EMA.</param>
    /// <param name="emaAlpha">EMA smoothing factor α; higher values react faster.</param>
    /// <param name="accuracyFloor">EMA value below which an alert is triggered.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckModelAccuracyAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minResolved,
        double                                  emaAlpha,
        double                                  accuracyFloor,
        CancellationToken                       ct)
    {
        // Calculate the UTC start of the look-back window.
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Load resolved prediction outcomes ordered oldest-first for chronological EMA.
        // Only rows where DirectionCorrect is non-null are "resolved" — the outcome has
        // been matched against actual price movement by MLPredictionOutcomeWorker.
        var outcomes = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null     &&
                        !l.IsDeleted)
            .OrderBy(l => l.PredictedAt)        // chronological order is required for EMA
            .Select(l => l.DirectionCorrect!.Value)
            .ToListAsync(ct);

        // Guard: skip models with insufficient resolved data to produce a reliable EMA.
        // A small sample would give a noisy estimate prone to false positives.
        if (outcomes.Count < minResolved)
        {
            _logger.LogDebug(
                "Rolling accuracy: {Symbol}/{Tf} only {N} resolved predictions (need {Min}) — skip.",
                model.Symbol, model.Timeframe, outcomes.Count, minResolved);
            return;
        }

        // Compute the EMA of direction correctness (1 = correct, 0 = incorrect).
        double ema = ComputeEma(outcomes, emaAlpha);

        _logger.LogDebug(
            "Rolling accuracy: {Symbol}/{Tf} model {Id}: EMA(acc)={Ema:F3} (floor={Floor:F2}, n={N})",
            model.Symbol, model.Timeframe, model.Id, ema, accuracyFloor, outcomes.Count);

        // Model is performing acceptably — nothing to do.
        if (ema >= accuracyFloor) return;

        _logger.LogWarning(
            "Rolling accuracy breach for {Symbol}/{Tf} model {Id}: " +
            "EMA(acc)={Ema:F3} < floor={Floor:F2} — alerting.",
            model.Symbol, model.Timeframe, model.Id, ema, accuracyFloor);

        // Deduplicate: skip if an active MLModelDegraded alert already exists for this symbol.
        // This prevents the alert table from accumulating hundreds of identical rows
        // while the model remains degraded across multiple poll cycles.
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == model.Symbol              &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (!alertExists)
        {
            // Persist the alert with a rich JSON payload so that the alert receiver
            // (Webhook / email / Telegram) can include actionable diagnostics.
            var alert = new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = model.Symbol,
                Channel       = AlertChannel.Webhook,
                Destination   = "ml-ops",
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason        = "rolling_accuracy_below_floor",
                    ema,
                    accuracyFloor,
                    emaAlpha,
                    symbol        = model.Symbol,
                    timeframe     = model.Timeframe.ToString(),
                    modelId       = model.Id,
                    resolvedCount = outcomes.Count,
                }),
                IsActive = true,
            };
            writeCtx.Set<Alert>().Add(alert);
            await writeCtx.SaveChangesAsync(ct);
        }
    }

    // ── EMA computation ───────────────────────────────────────────────────────

    /// <summary>
    /// Computes an EMA over a boolean outcome sequence (true = 1, false = 0),
    /// processing entries in oldest-first (chronological) order.
    ///
    /// <b>Initialisation strategy:</b> The EMA seed is set to the first observation's
    /// value (0 or 1) — equivalent to a warm-up period of 1. This is simpler than
    /// the alternative of seeding with the equal-weighted mean of the first N items,
    /// and is sufficient given the MinResolved guard upstream.
    ///
    /// <b>Update rule:</b> <c>ema = α × outcome + (1−α) × ema</c>
    /// </summary>
    /// <param name="outcomes">
    /// Chronologically ordered sequence of prediction correctness flags.
    /// Must be non-empty (guaranteed by the MinResolved guard in the caller).
    /// </param>
    /// <param name="alpha">
    /// Smoothing coefficient α ∈ (0, 1). Higher values make the EMA more
    /// responsive to recent outcomes but noisier. Default is 0.10.
    /// </param>
    /// <returns>The final EMA value after processing all outcomes.</returns>
    private static double ComputeEma(IReadOnlyList<bool> outcomes, double alpha)
    {
        // Seed EMA with the first outcome; avoids a cold-start bias at 0.5.
        double ema = outcomes[0] ? 1.0 : 0.0;

        // Apply the EMA recurrence relation to each subsequent observation.
        // ema_t = α * x_t + (1 - α) * ema_{t-1}
        for (int i = 1; i < outcomes.Count; i++)
            ema = alpha * (outcomes[i] ? 1.0 : 0.0) + (1.0 - alpha) * ema;

        return ema;
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Falls back to <paramref name="defaultValue"/> when the key is absent or the
    /// stored string cannot be converted to <typeparamref name="T"/>.
    /// This pattern allows live tuning of worker parameters without a service restart.
    /// </summary>
    /// <typeparam name="T">Target CLR type (e.g. <c>int</c>, <c>double</c>, <c>string</c>).</typeparam>
    /// <param name="ctx">DbContext to query (read context is sufficient).</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> value to look up.</param>
    /// <param name="defaultValue">Value returned when the key is missing or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed config value, or <paramref name="defaultValue"/>.</returns>
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
