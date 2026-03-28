using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors the sharpness (informativeness) of active ML model predictions by computing
/// a rolling binary entropy H over the last N confidence scores.
///
/// <para>
/// <b>Purpose:</b> A model that consistently outputs confidence scores near 0.5 is
/// essentially saying "I don't know" on every prediction. These near-random signals are
/// dangerous because they may still pass threshold filters, consuming trade budget without
/// genuine statistical edge. Entropy quantifies this uncertainty: maximum entropy (ln 2)
/// means the model is as uncertain as a coin flip.
/// </para>
///
/// <para>
/// <b>Polling interval:</b> Configurable via <c>MLSharpness:PollIntervalSeconds</c>;
/// defaults to 3600 seconds (1 hour). The hourly cadence balances detection latency
/// against read load on the prediction log table.
/// </para>
///
/// <para>
/// <b>ML lifecycle contribution:</b> Feeds into the degradation-detection layer of the
/// ML pipeline. When average entropy exceeds the alert threshold, an
/// <see cref="AlertType.MLModelDegraded"/> alert is raised, prompting the operator to
/// investigate regime drift, feature staleness, or training data issues. It does NOT
/// automatically suppress or retrain — the suppression decision is left to
/// <see cref="MLModelRetirementWorker"/>, which aggregates multiple degradation signals.
/// </para>
///
/// <para>
/// When the entropy approaches ln(2) ≈ 0.693 (i.e. the model outputs ≈ 50/50), predictions
/// become uninformative and should not be used for trading decisions.
/// </para>
///
/// <para>
/// Alert threshold: H ≥ ln(2) × <c>MLSharpness:EntropyAlertFraction</c> (default 0.90).
/// A high-entropy alert indicates the model is hedging; the operator should investigate
/// whether retraining, feature updates, or regime changes are needed.
/// </para>
///
/// Confidence scores are read from <see cref="MLModelPredictionLog.ConfidenceScore"/>.
/// The confidence value is mapped to a probability via:
///   p(Buy) ≈ 0.5 + conf/2  (Buy prediction)
///   p(Buy) ≈ 0.5 − conf/2  (Sell prediction)
/// so that H = −(p log p + (1−p) log(1−p)).
/// </summary>
public sealed class MLPredictionSharpnessWorker : BackgroundService
{
    private const string CK_PollSecs             = "MLSharpness:PollIntervalSeconds";
    private const string CK_WindowSize           = "MLSharpness:WindowSize";
    private const string CK_EntropyAlertFraction = "MLSharpness:EntropyAlertFraction";
    private const string CK_AlertDest            = "MLSharpness:AlertDestination";

    private static readonly double Ln2 = Math.Log(2.0);

    private readonly IServiceScopeFactory                    _scopeFactory;
    private readonly ILogger<MLPredictionSharpnessWorker>    _logger;

    /// <summary>
    /// Initializes the worker with the DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per poll cycle so that scoped services
    /// (EF DbContexts) are isolated between iterations and disposed promptly.
    /// </param>
    /// <param name="logger">Structured logger for diagnostic and alert messages.</param>
    public MLPredictionSharpnessWorker(
        IServiceScopeFactory                 scopeFactory,
        ILogger<MLPredictionSharpnessWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Runs until the host requests cancellation.
    /// Each iteration:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope to obtain scoped read/write DbContexts.</item>
    ///   <item>Reads the poll interval from <see cref="EngineConfig"/> (hot-reloadable).</item>
    ///   <item>Delegates to <see cref="CheckActiveModelsAsync"/> for the actual entropy check.</item>
    ///   <item>Sleeps for the configured poll interval before the next iteration.</item>
    /// </list>
    /// Errors are caught and logged without stopping the loop; only
    /// <see cref="OperationCanceledException"/> from the host token breaks the loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPredictionSharpnessWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default poll interval used if the config key is absent or unparseable
            int pollSecs = 3600;

            try
            {
                // Create a fresh scope per cycle — EF DbContexts are scoped services
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Re-read poll interval each cycle so config changes take effect without restart
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckActiveModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown — host is stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLPredictionSharpnessWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPredictionSharpnessWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Iterates over all active ML models (excluding regime-scoped shadow models)
    /// and evaluates the prediction sharpness for each one.
    /// </summary>
    /// <param name="readCtx">Read-only EF DbContext for querying config and prediction logs.</param>
    /// <param name="writeCtx">Write EF DbContext for persisting alerts.</param>
    /// <param name="ct">Cancellation token propagated from the host.</param>
    private async Task CheckActiveModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowSize           = await GetConfigAsync<int>   (readCtx, CK_WindowSize,           100,      ct);
        double entropyAlertFraction = await GetConfigAsync<double>(readCtx, CK_EntropyAlertFraction, 0.90,     ct);
        string alertDest            = await GetConfigAsync<string>(readCtx, CK_AlertDest,            "ml-ops", ct);

        // Compute the absolute entropy alert threshold from the fraction of maximum entropy (ln 2).
        // E.g. with fraction=0.90: threshold = 0.90 × 0.693 = 0.624
        // Any model whose average entropy exceeds this value is considered near-uninformative.
        double alertThreshold = Ln2 * entropyAlertFraction;

        // Exclude regime-scoped models (shadow evaluations) — they are scored differently
        // by the shadow arbiter and should not generate sharpness alerts independently.
        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.RegimeScope == null)
            .ToListAsync(ct);

        _logger.LogDebug("Sharpness check: {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            await CheckModelAsync(model, readCtx, writeCtx, windowSize, alertThreshold, alertDest, ct);
        }
    }

    /// <summary>
    /// Computes the rolling average binary entropy for a single model's most recent
    /// prediction logs and fires an alert if the entropy exceeds the configured threshold.
    /// </summary>
    /// <param name="model">The active ML model to evaluate.</param>
    /// <param name="readCtx">Read-only DbContext for loading prediction logs and training runs.</param>
    /// <param name="writeCtx">Write DbContext for persisting alerts.</param>
    /// <param name="windowSize">Number of most-recent prediction log records to include.</param>
    /// <param name="alertThreshold">
    /// Pre-computed absolute entropy threshold (= ln(2) × EntropyAlertFraction).
    /// </param>
    /// <param name="alertDest">Webhook/destination label written to the alert record.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task CheckModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        double                                  alertThreshold,
        string                                  alertDest,
        CancellationToken                       ct)
    {
        // Prefer rows with explicit probability persistence, but still keep legacy
        // confidence-based rows for backward compatibility.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId      == model.Id &&
                        (l.ConfidenceScore > 0 ||
                         l.CalibratedProbability != null ||
                         l.RawProbability != null) &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .ToListAsync(ct);

        // Require a minimum sample size to avoid spurious high-entropy readings
        // from a statistically insignificant number of logs.
        if (logs.Count < 20)
        {
            _logger.LogDebug(
                "Sharpness skipped model {Id} — only {N} logs (need 20).",
                model.Id, logs.Count);
            return;
        }

        // ── Binary entropy computation ────────────────────────────────────────
        // For each prediction log, map the raw confidence score [0,1] to a
        // calibrated probability of "Buy" using a linear mapping anchored at 0.5:
        //   Buy  prediction: p = 0.5 + conf/2  (e.g. conf=0.8 → p=0.90)
        //   Sell prediction: p = 0.5 - conf/2  (e.g. conf=0.8 → p=0.10)
        // This ensures that a confidence of 0 maps to p=0.5 (maximum uncertainty)
        // and confidence of 1 maps to p=1.0 or p=0.0 (absolute certainty).
        // H = -(p*log(p) + (1-p)*log(1-p))
        double sumH = 0.0;
        foreach (var log in logs)
        {
            double p = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(log);

            // Clamp to avoid log(0) — numerically stable lower/upper bounds
            p = Math.Clamp(p, 1e-10, 1.0 - 1e-10);
            sumH += -(p * Math.Log(p) + (1.0 - p) * Math.Log(1.0 - p));
        }

        // Average entropy over the window — values close to ln(2) ≈ 0.693 indicate
        // the model is outputting near-random confidence scores.
        double avgH = sumH / logs.Count;

        _logger.LogInformation(
            "Sharpness model {Id} ({Symbol}/{Tf}): avgH={H:F4} threshold={Thr:F4} (ln2={Ln2:F4})",
            model.Id, model.Symbol, model.Timeframe, avgH, alertThreshold, Ln2);

        // If average entropy is below the threshold, the model is sharp — no action needed.
        if (avgH <= alertThreshold) return;

        // ── Low-sharpness alert ───────────────────────────────────────────────
        // The model's predictions are too uncertain to be useful for trading.
        // Log a warning so operators can investigate.
        _logger.LogWarning(
            "LOW PREDICTION SHARPNESS model {Id} ({Symbol}/{Tf}): avgH={H:F4} > threshold={Thr:F4}. " +
            "Model is near-uninformative (entropy fraction={Frac:P1} of ln2).",
            model.Id, model.Symbol, model.Timeframe,
            avgH, alertThreshold, avgH / Ln2);

        var now = DateTime.UtcNow;

        // Deduplication guard: if a retrain is already queued or running for this
        // symbol/timeframe, the issue is already being addressed — skip the alert
        // to avoid stacking duplicate notifications.
        bool retrainQueued = await writeCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (retrainQueued)
        {
            _logger.LogDebug(
                "Sharpness alert suppressed for model {Id} — retrain already queued.", model.Id);
            return;
        }

        // Persist the alert with full diagnostic context so the operator can
        // immediately understand which model, what entropy level, and what the threshold was.
        writeCtx.Set<Alert>().Add(new Alert
        {
            Symbol        = model.Symbol,
            AlertType     = AlertType.MLModelDegraded,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                DetectorType      = "PredictionSharpness",
                ModelId           = model.Id,
                Timeframe         = model.Timeframe.ToString(),
                AvgEntropy        = avgH,
                Threshold         = alertThreshold,
                // Fraction of maximum possible entropy — 1.0 means the model is a coin flip
                EntropyFractionLn2 = avgH / Ln2,
                WindowSize        = logs.Count,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed configuration value from the <see cref="EngineConfig"/> table.
    /// Falls back to <paramref name="defaultValue"/> when the key is absent, null,
    /// or the stored string cannot be converted to <typeparamref name="T"/>.
    /// This allows safe operation even when the config table has not been seeded.
    /// </summary>
    /// <typeparam name="T">Target type (int, double, string, etc.).</typeparam>
    /// <param name="ctx">DbContext to query against.</param>
    /// <param name="key">The <see cref="EngineConfig.Key"/> to look up.</param>
    /// <param name="defaultValue">Value returned when the key is missing or unparseable.</param>
    /// <param name="ct">Cancellation token.</param>
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
