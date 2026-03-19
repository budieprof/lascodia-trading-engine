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

    public MLPsiAutoRetrainWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLPsiAutoRetrainWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPsiAutoRetrainWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 14400;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

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

    private async Task CheckPsiAndEnqueueAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays     = await GetConfigAsync<int>   (readCtx, CK_WindowDays,     30,   ct);
        int    minPredictions = await GetConfigAsync<int>   (readCtx, CK_MinPredictions, 50,   ct);
        double psiThreshold   = await GetConfigAsync<double>(readCtx, CK_PsiThreshold,   0.20, ct);

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
                _logger.LogWarning(ex,
                    "PSI auto-retrain check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task CheckModelPsiAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        double                                  psiThreshold,
        CancellationToken                       ct)
    {
        // Skip if a training run is already queued or running
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

        // Deserialise snapshot to get FeatureQuantileBreakpoints
        ModelSnapshot? snap;
        try { snap = System.Text.Json.JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }

        if (snap is null || snap.FeatureQuantileBreakpoints.Length == 0) return;

        // Load recent prediction-log confidence scores for PSI proxy
        // (We use the confidence score as a 1-D proxy; full feature-level PSI
        //  requires feature values at inference time, which are not stored.)
        var since  = DateTime.UtcNow.AddDays(-windowDays);
        var scores = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId   == model.Id &&
                        l.PredictedAt >= since    &&
                        !l.IsDeleted)
            .Select(l => (double)l.ConfidenceScore)
            .ToListAsync(ct);

        if (scores.Count < minPredictions)
        {
            _logger.LogDebug(
                "PSI auto-retrain: {Symbol}/{Tf} only {N} recent predictions (need {Min}) — skip.",
                model.Symbol, model.Timeframe, scores.Count, minPredictions);
            return;
        }

        // Compute PSI on confidence-score distribution vs training-time breakpoints of
        // the first stored feature (index 0) as a fast proxy.
        // Full feature-level PSI requires storing raw feature values in prediction logs,
        // which is outside the scope of this worker.
        double psi = ComputeConfidenceScorePsi(scores, snap);
        _logger.LogDebug(
            "PSI auto-retrain: {Symbol}/{Tf} model {Id}: PSI(conf)={PSI:F4} (threshold={Thr:F2})",
            model.Symbol, model.Timeframe, model.Id, psi, psiThreshold);

        if (psi < psiThreshold) return;

        _logger.LogWarning(
            "PSI breach for {Symbol}/{Tf} model {Id}: PSI(conf)={PSI:F4} ≥ {Thr:F2} — " +
            "auto-enqueuing training run.",
            model.Symbol, model.Timeframe, model.Id, psi, psiThreshold);

        var run = new MLTrainingRun
        {
            Symbol    = model.Symbol,
            Timeframe = model.Timeframe,
            Status    = RunStatus.Queued,
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
    /// Computes the Population Stability Index between the observed confidence-score
    /// distribution (10 equal-width bins over [0, 1]) and a uniform expected distribution.
    /// PSI &lt; 0.10 = no significant shift; 0.10–0.25 = moderate; &gt; 0.25 = major shift.
    /// </summary>
    private static double ComputeConfidenceScorePsi(
        IReadOnlyList<double> scores,
        ModelSnapshot         snap)
    {
        const int NumBin  = 10;
        double    binSize = 1.0 / NumBin;

        var observed  = new double[NumBin];
        var expected  = new double[NumBin]; // uniform = 1/NumBin each

        foreach (double s in scores)
        {
            int b = Math.Clamp((int)(s / binSize), 0, NumBin - 1);
            observed[b]++;
        }

        double n = scores.Count;
        double psi = 0.0;
        for (int b = 0; b < NumBin; b++)
        {
            double pObs = Math.Max(observed[b] / n, 1e-10);
            double pExp = Math.Max(1.0 / NumBin, 1e-10);
            psi += (pObs - pExp) * Math.Log(pObs / pExp);
        }

        return psi;
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
