using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Coordinates cross-worker degradation signals to make a holistic model retirement
/// decision, bridging the gap between individual alerting workers and automated action.
///
/// <b>Problem:</b> Each monitoring worker alerts independently on its own metric.
/// A model can accumulate 2–3 simultaneous degradation alerts — EWMA below critical,
/// consecutive-miss cooldown active, rolling accuracy below floor — while technically
/// remaining "active" because no single worker has authority to suppress it.
///
/// <b>Algorithm:</b> Evaluate four independent degradation signals for each model:
/// <list type="number">
///   <item><b>Cooldown active</b> — <c>MLCooldown:{Symbol}:{Tf}:ExpiresAt</c> in
///         <see cref="EngineConfig"/> is a future timestamp.</item>
///   <item><b>EWMA accuracy critical</b> — <see cref="MLModelEwmaAccuracy.EwmaAccuracy"/>
///         is below <c>CriticalEwmaThreshold</c> (default 0.48).</item>
///   <item><b>Live accuracy degraded</b> — <see cref="MLModel.LiveDirectionAccuracy"/>
///         is non-null and below <c>LiveAccuracyFloor</c> (default 0.48).</item>
///   <item><b>ADWIN drift detected</b> — <c>MLDrift:{Symbol}:{Tf}:AdwinDriftDetected</c>
///         config key holds a future expiry timestamp, set by <see cref="MLAdwinDriftWorker"/>
///         when a statistically significant accuracy shift is detected.</item>
/// </list>
///
/// When ≥ <c>SignalsRequired</c> (default 2) of these 4 signals are simultaneously
/// active, set <c>MLModel.IsSuppressed = true</c> via <c>ExecuteUpdateAsync</c>
/// and fire a <c>MLModelDecommissioned</c>-reason alert. The model remains suppressed
/// until a new champion is promoted (handled by the shadow-arbiter workflow).
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRetirement:PollIntervalSeconds</c>     — default 1800 (30 min)</item>
///   <item><c>MLRetirement:CriticalEwmaThreshold</c>   — default 0.48</item>
///   <item><c>MLRetirement:LiveAccuracyFloor</c>        — default 0.48</item>
///   <item><c>MLRetirement:SignalsRequired</c>          — degradation signals needed, default 2</item>
///   <item><c>MLRetirement:AlertDestination</c>         — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLModelRetirementWorker : BackgroundService
{
    private const string CK_PollSecs      = "MLRetirement:PollIntervalSeconds";
    private const string CK_EwmaThr       = "MLRetirement:CriticalEwmaThreshold";
    private const string CK_LiveFloor     = "MLRetirement:LiveAccuracyFloor";
    private const string CK_SigRequired   = "MLRetirement:SignalsRequired";
    private const string CK_AlertDest     = "MLRetirement:AlertDestination";
    private const string CK_GraceMinutes  = "MLRetirement:ActivationGraceMinutes";

    private readonly IServiceScopeFactory                _scopeFactory;
    private readonly ILogger<MLModelRetirementWorker>    _logger;

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public MLModelRetirementWorker(
        IServiceScopeFactory                 scopeFactory,
        ILogger<MLModelRetirementWorker>     logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Polls every 30 minutes by default. The 30-minute
    /// cadence balances responsiveness (detecting multi-signal degradation promptly)
    /// against the cost of reading EWMA accuracy rows and cooldown config keys for
    /// all active models each iteration.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLModelRetirementWorker started.");

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

                await EvaluateAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLModelRetirementWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLModelRetirementWorker stopping.");
    }

    // ── Retirement evaluation core ────────────────────────────────────────────

    /// <summary>
    /// Loads retirement thresholds and evaluates all active, non-suppressed models.
    /// Only models where <c>IsActive = true</c> and <c>IsSuppressed = false</c> are
    /// candidates — already-suppressed models are skipped to avoid redundant writes.
    /// </summary>
    private async Task EvaluateAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        double ewmaThr      = await GetConfigAsync<double>(readCtx, CK_EwmaThr,     0.48,    ct);
        double liveFloor    = await GetConfigAsync<double>(readCtx, CK_LiveFloor,    0.48,    ct);
        int    sigsRequired = await GetConfigAsync<int>   (readCtx, CK_SigRequired,  2,       ct);
        string alertDest    = await GetConfigAsync<string>(readCtx, CK_AlertDest,    "ml-ops", ct);
        int    graceMinutes = await GetConfigAsync<int>   (readCtx, CK_GraceMinutes, 30,      ct);

        // Only evaluate active, non-suppressed models — models already suppressed
        // by a previous retirement cycle are excluded to prevent redundant DB updates.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsSuppressed && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe, m.LiveDirectionAccuracy, m.ActivatedAt })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            // Grace period: do not retire models that were activated within the last N minutes.
            // Newly promoted models need time to accumulate enough prediction history for
            // the degradation signals to be statistically meaningful.
            if (model.ActivatedAt.HasValue &&
                (now - model.ActivatedAt.Value).TotalMinutes < graceMinutes)
            {
                _logger.LogDebug(
                    "Retirement: model {Id} ({Symbol}/{Tf}) activated {Ago:F0} min ago — " +
                    "within {Grace}-min grace period, skipping.",
                    model.Id, model.Symbol, model.Timeframe,
                    (now - model.ActivatedAt.Value).TotalMinutes, graceMinutes);
                continue;
            }

            try
            {
                await EvaluateModelAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    model.LiveDirectionAccuracy,
                    ewmaThr, liveFloor, sigsRequired, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Retirement: evaluation failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Evaluates all four retirement signals for a single model and, if enough signals
    /// are simultaneously active, suppresses the model and fires a critical alert.
    /// </summary>
    /// <remarks>
    /// <b>Four-signal retirement criteria:</b>
    /// <list type="number">
    ///   <item>
    ///     <b>Cooldown active (Signal 1):</b> Reads the config key
    ///     <c>MLCooldown:{Symbol}:{Timeframe}:ExpiresAt</c>, which is written by the
    ///     consecutive-miss detector when a model issues several wrong predictions in a row.
    ///     A future expiry time means the model is currently in a forced cooldown period.
    ///   </item>
    ///   <item>
    ///     <b>EWMA accuracy critical (Signal 2):</b> The exponentially-weighted moving
    ///     average accuracy tracked in <c>MLModelEwmaAccuracy</c> has fallen below
    ///     <paramref name="ewmaThreshold"/>. The EWMA is more sensitive to recent
    ///     performance than a simple rolling average because it down-weights older observations.
    ///   </item>
    ///   <item>
    ///     <b>Live accuracy degraded (Signal 3):</b> The model's
    ///     <see cref="MLModel.LiveDirectionAccuracy"/> field (updated by
    ///     <c>MLPredictionOutcomeWorker</c>) is below <paramref name="liveAccuracyFloor"/>.
    ///     This is a simple rolling fraction, complementary to the EWMA.
    ///   </item>
    ///   <item>
    ///     <b>ADWIN drift detected (Signal 4):</b> The <see cref="MLAdwinDriftWorker"/> has
    ///     detected a statistically significant accuracy shift via adaptive windowing.
    ///     The flag is stored as an expiring timestamp in
    ///     <c>MLDrift:{Symbol}:{Timeframe}:AdwinDriftDetected</c> (48-hour TTL).
    ///   </item>
    /// </list>
    ///
    /// <b>Retirement action:</b> When <paramref name="signalsRequired"/> or more signals
    /// are simultaneously active, <c>IsSuppressed = true</c> is written via
    /// <c>ExecuteUpdateAsync</c> (bulk update, no entity tracking overhead).
    /// The model remains suppressed until a new champion model is promoted by the
    /// shadow-arbiter workflow.
    ///
    /// <b>Post-retirement recovery:</b> Suppression is not permanent. When
    /// <see cref="MLShadowArbiterWorker"/> promotes a challenger model as the new
    /// active champion, it sets <c>IsSuppressed = false</c> on the new model and
    /// deactivates the old suppressed one.
    /// </remarks>
    private async Task EvaluateModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        decimal?                                liveDirectionAccuracy,
        double                                  ewmaThreshold,
        double                                  liveAccuracyFloor,
        int                                     signalsRequired,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        var now           = DateTime.UtcNow;
        var activeSignals = new List<string>();

        // ── Signal 1: Consecutive-miss cooldown active ────────────────────────
        // The cooldown key is written by the consecutive-miss detector (e.g.
        // MLSuppressionRollbackWorker or a dedicated cooldown worker) when the model
        // issues N consecutive wrong predictions. The key holds an ISO-8601 expiry
        // timestamp; if it is in the future, the cooldown is still active.
        var cdKey   = $"MLCooldown:{symbol}:{timeframe}:ExpiresAt";
        var cdEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == cdKey, ct);

        if (cdEntry?.Value is not null &&
            DateTime.TryParse(cdEntry.Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var cdExpiry) &&
            now < cdExpiry)
        {
            // Cooldown is actively suppressing the model — record the signal
            activeSignals.Add($"cooldown_active (expires {cdExpiry:HH:mm} UTC)");
        }

        // ── Signal 2: EWMA accuracy below critical threshold ─────────────────
        // MLModelEwmaAccuracy is updated by the EWMA tracking worker after each
        // resolved prediction. The EWMA is weighted toward recent predictions,
        // making it more sensitive to sudden performance deterioration than
        // a simple rolling average over a fixed window.
        var ewmaRow = await readCtx.Set<MLModelEwmaAccuracy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.MLModelId == modelId, ct);

        if (ewmaRow is not null && ewmaRow.EwmaAccuracy < ewmaThreshold)
            activeSignals.Add($"ewma_critical ({ewmaRow.EwmaAccuracy:P2} < {ewmaThreshold:P2})");

        // ── Signal 3: Live direction accuracy below floor ──────────────��──────
        // MLModel.LiveDirectionAccuracy is a simple rolling fraction updated by
        // MLPredictionOutcomeWorker. Unlike EWMA it treats all predictions in the
        // window equally — it serves as a complementary, less reactive signal.
        if (liveDirectionAccuracy.HasValue &&
            (double)liveDirectionAccuracy.Value < liveAccuracyFloor)
        {
            activeSignals.Add(
                $"live_accuracy_degraded ({liveDirectionAccuracy.Value:P2} < {liveAccuracyFloor:P2})");
        }

        // ── Signal 4: ADWIN drift detected ────��──────────────────────────────
        // MLAdwinDriftWorker writes a timestamped flag when it detects a statistically
        // significant accuracy shift via adaptive windowing. The flag expires after
        // 48 hours if ADWIN does not re-detect drift on the next run.
        var adwinKey   = $"MLDrift:{symbol}:{timeframe}:AdwinDriftDetected";
        var adwinEntry = await readCtx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == adwinKey, ct);

        if (adwinEntry?.Value is not null &&
            DateTime.TryParse(adwinEntry.Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var adwinExpiry) &&
            now < adwinExpiry)
        {
            activeSignals.Add($"adwin_drift_detected (expires {adwinExpiry:HH:mm} UTC)");
        }

        int signalCount = activeSignals.Count;

        _logger.LogDebug(
            "Retirement: model {Id} ({Symbol}/{Tf}) — {N}/{Required} degradation signals active: {Sigs}",
            modelId, symbol, timeframe, signalCount, signalsRequired,
            string.Join("; ", activeSignals));

        // Not enough simultaneous signals to justify retirement — the model continues operating.
        if (signalCount < signalsRequired) return;

        // ── Retire the model (suppress) ───────────────────────────────────────
        // ExecuteUpdateAsync performs a single targeted SQL UPDATE without loading
        // the entity, minimizing overhead for a critical path operation.
        _logger.LogWarning(
            "Retirement: model {Id} ({Symbol}/{Tf}) — {N} simultaneous degradation signals. " +
            "Suppressing model. Signals: {Sigs}",
            modelId, symbol, timeframe, signalCount, string.Join("; ", activeSignals));

        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == modelId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsSuppressed, true), ct);

        // ── Critical alert (idempotent insert) ────────────────────────────────
        // Use try/catch on SaveChanges instead of check-then-insert to avoid
        // TOCTOU race conditions where two workers both see "no alert" and both insert.
        try
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = symbol,
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason           = "model_decommissioned",
                    severity         = "critical",
                    symbol,
                    timeframe        = timeframe.ToString(),
                    modelId,
                    activeSignals,
                    signalCount,
                    signalsRequired,
                    // Describe the effect so operators immediately understand the consequence
                    action           = "IsSuppressed set to true — model will no longer score signals",
                }),
                IsActive = true,
            });

            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Duplicate alert — another worker already inserted it. This is expected
            // under concurrent execution and is safe to ignore.
            _logger.LogDebug(
                "Retirement: duplicate alert suppressed for {Symbol}/{Tf} model {Id}.",
                symbol, timeframe, modelId);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/>. Returns
    /// <paramref name="defaultValue"/> if the key is absent or unparseable.
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
