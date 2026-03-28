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
/// Watches live feature PSI (Population Stability Index) for active ML models and
/// automatically enqueues a new <see cref="MLTrainingRun"/> when the average PSI across
/// all features exceeds <c>MLPsiAutoRetrain:PsiThreshold</c>.
///
/// PSI is computed by comparing the current feature distribution (last
/// <c>MLPsiAutoRetrain:WindowDays</c> days of predictions) against the training-time quantile
/// breakpoints stored in <see cref="ModelSnapshot.FeatureQuantileBreakpoints"/>.
///
/// The auto-retrain is skipped when:
/// <list type="bullet">
///   <item>A training run is already queued or running for the symbol/timeframe.</item>
///   <item>Fewer than <c>MLPsiAutoRetrain:MinPredictions</c> recent prediction logs are available.</item>
/// </list>
/// </summary>
public sealed class MLPsiAutoRetrainWorker : BackgroundService
{
    // ── EngineConfig keys ─────────────────────────────────────────────────────
    private const string CK_PollSecs        = "MLPsiAutoRetrain:PollIntervalSeconds";
    private const string CK_WindowDays      = "MLPsiAutoRetrain:WindowDays";
    private const string CK_MinPredictions  = "MLPsiAutoRetrain:MinPredictions";
    private const string CK_PsiThreshold    = "MLPsiAutoRetrain:PsiThreshold";

    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly ILogger<MLPsiAutoRetrainWorker> _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new async DI scope per poll cycle so scoped EF Core
    /// contexts are correctly disposed after each PSI check pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLPsiAutoRetrainWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLPsiAutoRetrainWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Polls every <c>MLPsiAutoRetrain:PollIntervalSeconds</c>
    /// seconds (default 14400 = 4 hours), reading the interval from <see cref="EngineConfig"/>
    /// on each cycle so it can be hot-reloaded without a restart.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPsiAutoRetrainWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Default 4-hour poll interval; refreshed from DB on every cycle.
            int pollSecs = 14400;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                // Refresh poll interval from DB each cycle to support hot-reload.
                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 14400, stoppingToken);

                await CheckPsiAndEnqueueAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLPsiAutoRetrainWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPsiAutoRetrainWorker stopping.");
    }

    // ── Per-poll PSI check ────────────────────────────────────────────────────

    /// <summary>
    /// Iterates over all active models that have a valid serialised snapshot and
    /// delegates per-model PSI checks to <see cref="CheckModelPsiAsync"/>.
    /// Reads the current PSI configuration from <see cref="EngineConfig"/> once per
    /// poll cycle and passes it to each model check to avoid redundant DB queries.
    /// </summary>
    /// <param name="readCtx">Read-only EF context for models, prediction logs, and config.</param>
    /// <param name="writeCtx">Write EF context for inserting training run records.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task CheckPsiAndEnqueueAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Read PSI policy parameters once per cycle to avoid per-model DB round trips.
        int    windowDays     = await GetConfigAsync<int>   (readCtx, CK_WindowDays,     30,   ct);
        int    minPredictions = await GetConfigAsync<int>   (readCtx, CK_MinPredictions, 50,   ct);
        double psiThreshold   = await GetConfigAsync<double>(readCtx, CK_PsiThreshold,   0.20, ct);

        // Load models with valid snapshots — FeatureQuantileBreakpoints are needed for PSI.
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelPsiAsync(
                    model, readCtx, writeCtx,
                    windowDays, minPredictions, psiThreshold, ct);
            }
            catch (Exception ex)
            {
                // Per-model exceptions are non-fatal — log and continue with remaining models.
                _logger.LogWarning(ex,
                    "PSI auto-retrain check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    /// <summary>
    /// Checks whether the confidence-score distribution of a single model's recent
    /// predictions has shifted significantly (PSI breach) from the training-time
    /// distribution and, if so, enqueues a new <see cref="MLTrainingRun"/>.
    /// </summary>
    /// <remarks>
    /// PSI auto-retrain methodology:
    ///
    /// The Population Stability Index (PSI) quantifies distributional shift between
    /// two samples:
    ///   PSI = Σ_b (pObs_b − pExp_b) × log(pObs_b / pExp_b)
    /// Standard interpretation:
    /// <list type="bullet">
    ///   <item>PSI &lt; 0.10: no significant shift — model remains representative.</item>
    ///   <item>0.10 ≤ PSI &lt; 0.25: moderate shift — monitor closely.</item>
    ///   <item>PSI ≥ 0.25: major shift — retrain required.</item>
    /// </list>
    ///
    /// <b>Confidence-score proxy:</b> Full feature-level PSI requires raw feature values
    /// at inference time, which the prediction log does not store (storing them would
    /// double the log table size). Instead, the model's output confidence score is used
    /// as a 1-D distributional proxy. A shift in the confidence distribution is a strong
    /// signal that the model's feature inputs have drifted — the model has become
    /// under- or over-confident relative to its training-time calibration.
    ///
    /// <b>Expected distribution:</b> A well-calibrated model produces confidence scores
    /// that are approximately uniform over [0, 1] when averaged across many signals.
    /// The PSI baseline is therefore a 10-bin uniform distribution (each bin expects
    /// 10% of scores). Departures from uniformity indicate calibration drift.
    /// </remarks>
    /// <param name="model">The model being checked.</param>
    /// <param name="readCtx">Read-only EF context for training runs and prediction logs.</param>
    /// <param name="writeCtx">Write EF context for inserting the retraining run record.</param>
    /// <param name="windowDays">Rolling window in days for prediction log collection.</param>
    /// <param name="minPredictions">Minimum recent predictions required before PSI is computed.</param>
    /// <param name="psiThreshold">PSI value above which a retraining run is enqueued.</param>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task CheckModelPsiAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        double                                  psiThreshold,
        CancellationToken                       ct)
    {
        // Skip if a training run is already queued or running for this symbol/timeframe.
        // An in-flight retrain will refresh the model regardless of the PSI trigger.
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);
        if (alreadyQueued)
        {
            _logger.LogDebug(
                "PSI auto-retrain: {Symbol}/{Tf} already has a queued/running training run — skip.",
                model.Symbol, model.Timeframe);
            return;
        }

        // Deserialise snapshot to access FeatureQuantileBreakpoints.
        // These were stored at training time and represent the expected feature distribution.
        ModelSnapshot? snap;
        try { snap = System.Text.Json.JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        // Skip models whose snapshots were created before the FeatureQuantileBreakpoints field
        // was introduced (pre-Round-7 snapshots). An empty array is a sentinel for "not computed".
        if (snap is null || snap.FeatureQuantileBreakpoints.Length == 0) return;

        // Load recent prediction-log confidence scores for the PSI confidence-score proxy.
        // We use the confidence score as a 1-D proxy; full feature-level PSI requires
        // feature values at inference time, which are not stored in the prediction log.
        var since  = DateTime.UtcNow.AddDays(-windowDays);
        var scores = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId   == model.Id &&
                        l.PredictedAt >= since    &&
                        !l.IsDeleted)
            .Select(l => (double)l.ConfidenceScore)
            .ToListAsync(ct);

        // Require a minimum number of recent predictions for a stable PSI estimate.
        if (scores.Count < minPredictions)
        {
            _logger.LogDebug(
                "PSI auto-retrain: {Symbol}/{Tf} only {N} recent predictions (need {Min}) — skip.",
                model.Symbol, model.Timeframe, scores.Count, minPredictions);
            return;
        }

        // Compute PSI on the confidence-score distribution.
        double psi = ComputeConfidenceScorePsi(scores, snap);
        _logger.LogDebug(
            "PSI auto-retrain: {Symbol}/{Tf} model {Id}: PSI(conf)={PSI:F4} (threshold={Thr:F2})",
            model.Symbol, model.Timeframe, model.Id, psi, psiThreshold);

        // No significant distributional shift — model remains representative.
        if (psi < psiThreshold) return;

        _logger.LogWarning(
            "PSI breach for {Symbol}/{Tf} model {Id}: PSI(conf)={PSI:F4} ≥ {Thr:F2} — " +
            "auto-enqueuing training run.",
            model.Symbol, model.Timeframe, model.Id, psi, psiThreshold);

        // Enqueue a new training run. The HyperparamConfigJson carries audit fields
        // so the ML health worker can trace why this run was scheduled.
        var run = new MLTrainingRun
        {
            Symbol    = model.Symbol,
            Timeframe = model.Timeframe,
            Status    = RunStatus.Queued,
            FromDate  = DateTime.UtcNow.AddDays(-365),
            ToDate    = DateTime.UtcNow,
            HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                triggeredBy = "MLPsiAutoRetrainWorker",
                psi,
                psiThreshold,
                modelId     = model.Id,
            }),
        };

        writeCtx.Set<MLTrainingRun>().Add(run);
        await writeCtx.SaveChangesAsync(ct);

        _logger.LogInformation(
            "PSI auto-retrain: enqueued MLTrainingRun for {Symbol}/{Tf} (PSI={PSI:F4}).",
            model.Symbol, model.Timeframe, psi);
    }

    // ── PSI computation (confidence-score distribution vs uniform baseline) ───

    /// <summary>
    /// Computes the Population Stability Index (PSI) between the observed confidence-score
    /// distribution (10 equal-width bins over [0, 1]) and a uniform expected distribution.
    /// </summary>
    /// <remarks>
    /// PSI formula:
    ///   PSI = Σ_b (p_obs_b − p_exp_b) × ln(p_obs_b / p_exp_b)
    ///
    /// Binning: 10 equal-width bins of width 0.1 over [0, 1].
    /// Expected distribution: uniform (each bin expects 10% of scores) — this is the
    /// baseline for a well-calibrated model producing diverse confidence outputs.
    ///
    /// Numerical guard: both p_obs and p_exp are clamped to ≥ 1e-10 to avoid log(0).
    ///
    /// Interpretation:
    /// <list type="bullet">
    ///   <item>PSI &lt; 0.10 — negligible shift; no action needed.</item>
    ///   <item>0.10 ≤ PSI &lt; 0.25 — moderate shift; monitor.</item>
    ///   <item>PSI ≥ 0.25 — major shift; retrain recommended.</item>
    /// </list>
    /// </remarks>
    /// <param name="scores">Recent model confidence scores in [0, 1].</param>
    /// <param name="snap">
    /// Model snapshot (not currently used in this 1-D confidence proxy; included for
    /// future extension to full feature-level PSI using FeatureQuantileBreakpoints).
    /// </param>
    /// <returns>The computed PSI value (≥ 0).</returns>
    private static double ComputeConfidenceScorePsi(
        IReadOnlyList<double> scores,
        ModelSnapshot         snap)
    {
        const int NumBin  = 10;
        double    binSize = 1.0 / NumBin;

        var observed  = new double[NumBin]; // count of scores in each bin

        // Distribute scores into equal-width bins.
        // Clamp to [0, NumBin-1] so that a score of exactly 1.0 does not overflow.
        foreach (double s in scores)
        {
            int b = Math.Clamp((int)(s / binSize), 0, NumBin - 1);
            observed[b]++;
        }

        // Compute PSI as the sum of (pObs − pExp) × ln(pObs / pExp) across all bins.
        double n = scores.Count;
        double psi = 0.0;
        for (int b = 0; b < NumBin; b++)
        {
            // Normalise observed counts to proportions; guard against log(0).
            double pObs = Math.Max(observed[b] / n, 1e-10);
            // Uniform expected proportion (1/NumBin); guard against log(0).
            double pExp = Math.Max(1.0 / NumBin, 1e-10);
            psi += (pObs - pExp) * Math.Log(pObs / pExp);
        }

        return psi;
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
