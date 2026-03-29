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
/// all tracked features exceeds <c>MLPsiAutoRetrain:PsiThreshold</c>.
///
/// PSI is computed by comparing the current feature distribution (last
/// <c>MLPsiAutoRetrain:WindowDays</c> days of predictions) against the training-time quantile
/// breakpoints stored in <see cref="ModelSnapshot.FeatureQuantileBreakpoints"/>.
///
/// The auto-retrain is skipped when:
/// <list type="bullet">
///   <item>A training run is already queued or running for the symbol/timeframe.</item>
///   <item>Fewer than <c>MLPsiAutoRetrain:MinPredictions</c> recent feature samples can be built
///         from recent candles.</item>
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
    /// Checks whether a single model's recent feature distribution has shifted
    /// significantly (PSI breach) from the training-time distribution and, if so,
    /// enqueues a new <see cref="MLTrainingRun"/>.
    /// </summary>
    /// <remarks>
    /// The worker rebuilds recent feature vectors from candles, standardises them with the
    /// snapshot's training-time means/stds, reapplies fractional differencing and the active
    /// feature mask, then compares each feature's recent distribution with the stored
    /// training-time quantile breakpoints. The average feature PSI is used as the retrain
    /// trigger, matching the worker's contract and avoiding confidence-score proxy drift.
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
        int featureCount = snap.Features.Length > 0 ? snap.Features.Length : MLFeatureHelper.FeatureCount;

        // Load recent candles and rebuild the same feature pipeline used by the trainer.
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var candles = await readCtx.Set<Candle>()
            .AsNoTracking()
            .Where(c => c.Symbol == model.Symbol &&
                        c.Timeframe == model.Timeframe &&
                        c.Timestamp >= since &&
                        !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);

        if (candles.Count < MLFeatureHelper.LookbackWindow + 5)
        {
            _logger.LogDebug(
                "PSI auto-retrain: {Symbol}/{Tf} only {N} candles in window — skip.",
                model.Symbol, model.Timeframe, candles.Count);
            return;
        }

        var recentSamples = MLFeatureHelper.BuildTrainingSamples(candles);
        if (recentSamples.Count < minPredictions)
        {
            _logger.LogDebug(
                "PSI auto-retrain: {Symbol}/{Tf} only {N} recent feature samples (need {Min}) — skip.",
                model.Symbol, model.Timeframe, recentSamples.Count, minPredictions);
            return;
        }

        var recentStandardised = recentSamples
            .Select(s => s with { Features = MLFeatureHelper.Standardize(s.Features, snap.Means, snap.Stds) })
            .ToList();

        if (snap.FracDiffD > 0.0)
            recentStandardised = MLFeatureHelper.ApplyFractionalDifferencing(recentStandardised, featureCount, snap.FracDiffD);

        if (snap.ActiveFeatureMask is { Length: > 0 } activeMask && activeMask.Length == featureCount)
        {
            for (int i = 0; i < recentStandardised.Count; i++)
            {
                var masked = (float[])recentStandardised[i].Features.Clone();
                for (int j = 0; j < featureCount; j++)
                    if (!activeMask[j]) masked[j] = 0f;
                recentStandardised[i] = recentStandardised[i] with { Features = masked };
            }
        }

        var psiValues = new List<double>();
        for (int j = 0; j < Math.Min(featureCount, snap.FeatureQuantileBreakpoints.Length); j++)
        {
            double[] binEdges = snap.FeatureQuantileBreakpoints[j];
            if (binEdges.Length == 0) continue;

            double[] recentVals = recentStandardised
                .Select(s => j < s.Features.Length ? (double)s.Features[j] : 0.0)
                .ToArray();
            if (recentVals.Length == 0) continue;

            double[] trainVals = GenerateUniformFromEdges(binEdges, recentVals.Length);
            psiValues.Add(MLFeatureHelper.ComputeFeaturePsi(binEdges, trainVals, recentVals));
        }

        if (psiValues.Count == 0) return;

        double psi = psiValues.Average();
        _logger.LogDebug(
            "PSI auto-retrain: {Symbol}/{Tf} model {Id}: PSI(avg-feature)={PSI:F4} (threshold={Thr:F2})",
            model.Symbol, model.Timeframe, model.Id, psi, psiThreshold);

        // No significant distributional shift — model remains representative.
        if (psi < psiThreshold) return;

        _logger.LogWarning(
            "PSI breach for {Symbol}/{Tf} model {Id}: PSI(avg-feature)={PSI:F4} ≥ {Thr:F2} — " +
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

    // ── PSI helper ───────────────────────────────────────────────────────────

    private static double[] GenerateUniformFromEdges(double[] edges, int n)
    {
        int bins = edges.Length + 1;
        int perBin = Math.Max(1, n / bins);
        var result = new List<double>(bins * perBin);

        double prevEdge = edges.Length > 0 ? edges[0] - (edges[^1] - edges[0]) * 0.5 : -3.0;

        for (int b = 0; b < bins; b++)
        {
            double lo = b == 0 ? prevEdge : edges[b - 1];
            double hi = b < edges.Length ? edges[b] : edges[^1] + (edges[^1] - edges[0]) * 0.5;
            double mid = (lo + hi) / 2.0;
            for (int i = 0; i < perBin; i++)
                result.Add(mid);
        }

        return [.. result];
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
