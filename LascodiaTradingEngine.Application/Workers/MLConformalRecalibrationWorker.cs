using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Recalibrates the conformal prediction threshold (<see cref="ModelSnapshot.ConformalQHat"/>)
/// for each active model using recent resolved prediction logs, restoring the coverage guarantee
/// that degrades over time as the feature distribution shifts post-deployment.
///
/// <b>Background:</b> At training time, <c>ConformalQHat</c> is computed as the empirical
/// <c>(1 − alpha)</c> quantile of nonconformity scores on a held-out calibration set.
/// The guarantee is that for any new prediction, the model's prediction set will cover the
/// true label with probability ≥ <c>(1 − alpha)</c>. After deployment, if the live input
/// distribution shifts, the calibration set no longer represents the new regime and the
/// empirical coverage drifts below the nominal level.
///
/// This worker recomputes <c>ConformalQHat</c> from recent production predictions, ensuring
/// that the coverage guarantee remains approximately valid on the current input distribution.
///
/// <b>Algorithm:</b>
/// <list type="bullet">
///   <item>For each resolved prediction log, compute its nonconformity score:
///         <c>score = DirectionCorrect ? (1 − ConfidenceScore) : ConfidenceScore</c></item>
///   <item>Sort scores ascending; take the empirical
///         <c>⌈(1 − NominalAlpha) × (n + 1) / n⌉</c> quantile as the new <c>ConformalQHat</c>.</item>
///   <item>Compute the actual empirical coverage at the current threshold; if the drift
///         relative to nominal exceeds <c>CoverageDriftTolerance</c>, patch <c>ModelBytes</c>
///         and invalidate the scorer's cache entry.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLConformalRecal:PollIntervalSeconds</c>   — default 21600 (6 h)</item>
///   <item><c>MLConformalRecal:WindowDays</c>            — rolling window for scores, default 14</item>
///   <item><c>MLConformalRecal:MinPredictions</c>        — min resolved logs required, default 30</item>
///   <item><c>MLConformalRecal:NominalAlpha</c>          — target mis-coverage rate, default 0.10 (90 % coverage)</item>
///   <item><c>MLConformalRecal:CoverageDriftTolerance</c>— patch threshold, default 0.03</item>
/// </list>
/// </summary>
public sealed class MLConformalRecalibrationWorker : BackgroundService
{
    private const string CK_PollSecs        = "MLConformalRecal:PollIntervalSeconds";
    private const string CK_WindowDays      = "MLConformalRecal:WindowDays";
    private const string CK_MinPredictions  = "MLConformalRecal:MinPredictions";
    private const string CK_NominalAlpha    = "MLConformalRecal:NominalAlpha";
    private const string CK_DriftTolerance  = "MLConformalRecal:CoverageDriftTolerance";

    private readonly IServiceScopeFactory                        _scopeFactory;
    private readonly IMemoryCache                                _cache;
    private readonly ILogger<MLConformalRecalibrationWorker>     _logger;

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">Creates scoped DI scopes per polling cycle.</param>
    /// <param name="cache">
    /// Shared in-memory cache. The <c>MLSnapshot:{modelId}</c> entry is evicted after a
    /// successful QHat patch so <c>MLSignalScorer</c> loads the updated value immediately.
    /// </param>
    /// <param name="logger">Structured logger.</param>
    public MLConformalRecalibrationWorker(
        IServiceScopeFactory                      scopeFactory,
        IMemoryCache                              cache,
        ILogger<MLConformalRecalibrationWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    /// <summary>
    /// Main hosted-service loop. Polls every <c>MLConformalRecal:PollIntervalSeconds</c>
    /// (default 21600 s / 6 h). Creates a fresh DI scope per cycle and delegates
    /// to <see cref="RecalibrateAllModelsAsync"/>.
    /// </summary>
    /// <param name="stoppingToken">Signals graceful shutdown requested by the host.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLConformalRecalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 21600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 21600, stoppingToken);

                await RecalibrateAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLConformalRecalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLConformalRecalibrationWorker stopping.");
    }

    // ── Model iteration ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads global conformal recalibration parameters from <see cref="EngineConfig"/>
    /// then iterates over every active model, delegating per-model work to
    /// <see cref="RecalibrateModelAsync"/>. Errors for individual models are caught and
    /// logged so one bad model cannot stall the full cycle.
    /// </summary>
    private async Task RecalibrateAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // All parameters are hot-reloadable from EngineConfig without a service restart.
        int    windowDays      = await GetConfigAsync<int>   (readCtx, CK_WindowDays,     14,   ct);
        int    minPredictions  = await GetConfigAsync<int>   (readCtx, CK_MinPredictions, 30,   ct);
        double nominalAlpha    = await GetConfigAsync<double>(readCtx, CK_NominalAlpha,   0.10, ct);
        double driftTolerance  = await GetConfigAsync<double>(readCtx, CK_DriftTolerance, 0.03, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await RecalibrateModelAsync(
                    model, readCtx, writeCtx,
                    windowDays, minPredictions, nominalAlpha, driftTolerance, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ConformalRecal: failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    // ── Per-model recalibration ───────────────────────────────────────────────

    /// <summary>
    /// Recalibrates the conformal coverage threshold <c>ConformalQHat</c> for a single model
    /// by recomputing the empirical quantile from recent resolved prediction logs and patching
    /// <c>ModelBytes</c> only when the empirical coverage has drifted beyond the tolerance band.
    ///
    /// <b>Nonconformity score definition used here:</b>
    /// <list type="bullet">
    ///   <item>When the prediction was correct:   score = 1 − confidence
    ///         (a correct prediction with high confidence has a low nonconformity score — it "conforms" well)</item>
    ///   <item>When the prediction was incorrect: score = confidence
    ///         (an incorrect prediction with high confidence is maximally nonconformant)</item>
    /// </list>
    ///
    /// <b>QHat computation:</b>
    /// Sort scores ascending, then:
    ///   quantileIdx = ⌈(1 − α)(n + 1)⌉ − 1
    ///   newQHat     = scores[quantileIdx]
    /// This is the finite-sample correction that provides the marginal coverage guarantee
    /// P(score ≤ QHat) ≥ 1 − α when QHat is computed on an exchangeable calibration set.
    ///
    /// <b>Drift decision:</b>
    /// The patch is only applied when |empiricalCoverage − nominalCoverage| > driftTolerance.
    /// Empirical coverage = fraction of logs whose nonconformity score ≤ currentQHat.
    /// If empirical coverage has diverged, the stored QHat no longer provides the nominal guarantee
    /// and must be updated.
    /// </summary>
    /// <param name="model">Active model entity to recalibrate.</param>
    /// <param name="readCtx">Read-only EF context.</param>
    /// <param name="writeCtx">Write EF context for persisting the patched snapshot.</param>
    /// <param name="windowDays">Rolling window in days for loading resolved logs.</param>
    /// <param name="minPredictions">Minimum resolved-log count required to proceed.</param>
    /// <param name="nominalAlpha">Target mis-coverage rate (e.g. 0.10 for 90 % coverage).</param>
    /// <param name="driftTolerance">
    /// Maximum acceptable absolute gap between empirical and nominal coverage before a patch is issued.
    /// E.g. 0.03 means patch only when the gap exceeds 3 percentage points.
    /// </param>
    /// <param name="ct">Cooperative cancellation token.</param>
    private async Task RecalibrateModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        double                                  nominalAlpha,
        double                                  driftTolerance,
        CancellationToken                       ct)
    {
        // Deserialise snapshot to get current QHat stored at training or last recalibration.
        ModelSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }
        if (snap is null) return;

        var since = DateTime.UtcNow.AddDays(-windowDays);
        double decisionThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);

        // Load recent resolved logs and reconstruct their calibrated probabilities exactly
        // when the scorer persisted them, otherwise fall back to the legacy contract.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null      &&
                        l.ActualDirection  != null      &&
                        !l.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        if (logs.Count < minPredictions)
        {
            _logger.LogDebug(
                "ConformalRecal: {Symbol}/{Tf} model {Id}: only {N} resolved logs (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, logs.Count, minPredictions);
            return;
        }

        double currentQHat = snap.ConformalQHat;

        // Compute nonconformity scores:
        //   Correct prediction:   score = 1 − confidence  (low score = highly conformant)
        //   Incorrect prediction: score = confidence       (high score = highly nonconformant)
        // This definition is symmetric with the conformal set membership rule used at inference:
        //   Include Buy  if (1 − pBuy)  ≤ QHat
        //   Include Sell if (1 − pSell) ≤ QHat
        var scores = logs
            .Select(l =>
            {
                double pBuy = MLFeatureHelper.ResolveLoggedCalibratedBuyProbability(l, decisionThreshold);
                double pTrue = l.ActualDirection == LascodiaTradingEngine.Domain.Enums.TradeDirection.Buy
                    ? pBuy
                    : 1.0 - pBuy;
                return 1.0 - pTrue;
            })
            .OrderBy(s => s)
            .ToList();

        int    n               = scores.Count;
        double nominalCoverage = 1.0 - nominalAlpha; // e.g. 0.90 for α=0.10

        // Split-conformal quantile with finite-sample correction:
        //   quantileIdx = ⌈(1−α)(n+1)⌉ − 1  (0-based index into sorted scores)
        // This gives the smallest QHat such that P(score ≤ QHat) ≥ 1−α.
        int quantileIdx = (int)Math.Ceiling(nominalCoverage * (n + 1)) - 1;
        quantileIdx     = Math.Clamp(quantileIdx, 0, n - 1);
        double newQHat  = scores[quantileIdx]; // newly computed coverage threshold

        // Actual coverage at current QHat: fraction of predictions where score <= currentQHat.
        // If this fraction has diverged significantly from the nominal level, we need to update.
        double empiricalCoverage = scores.Count(s => s <= currentQHat) / (double)n;
        double coverageDrift     = Math.Abs(empiricalCoverage - nominalCoverage);

        _logger.LogDebug(
            "ConformalRecal: {Symbol}/{Tf} model {Id}: " +
            "currentQHat={CQ:F4} newQHat={NQ:F4} empiricalCoverage={EC:P1} nominal={NC:P1} drift={D:P1}",
            model.Symbol, model.Timeframe, model.Id,
            currentQHat, newQHat, empiricalCoverage, nominalCoverage, coverageDrift);

        // No patch needed — empirical coverage is still close enough to the nominal level.
        if (coverageDrift <= driftTolerance)
            return;

        _logger.LogInformation(
            "ConformalRecal: {Symbol}/{Tf} model {Id}: coverage drifted {Drift:P1} " +
            "(empirical={Emp:P1} vs nominal={Nom:P1}). Patching ConformalQHat {Old:F4} → {New:F4}.",
            model.Symbol, model.Timeframe, model.Id,
            coverageDrift, empiricalCoverage, nominalCoverage, currentQHat, newQHat);

        // Patch the ConformalQHat in-place on the existing snapshot object.
        snap.ConformalQHat = newQHat;

        byte[] updatedBytes;
        try { updatedBytes = JsonSerializer.SerializeToUtf8Bytes(snap); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ConformalRecal: {Symbol}/{Tf} model {Id}: failed to serialise patched snapshot — skip.",
                model.Symbol, model.Timeframe, model.Id);
            return;
        }

        // Persist the patched snapshot bytes using a targeted update.
        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ModelBytes, updatedBytes), ct);

        // Invalidate the scorer's 30-min cache so the patched snapshot is loaded on the next score.
        _cache.Remove($"MLSnapshot:{model.Id}");

        _logger.LogInformation(
            "ConformalRecal: {Symbol}/{Tf} model {Id}: ConformalQHat patched and cache invalidated.",
            model.Symbol, model.Timeframe, model.Id);
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from the <see cref="EngineConfig"/> table by key.
    /// Falls back to <paramref name="defaultValue"/> when the key is missing or unparseable.
    /// </summary>
    /// <typeparam name="T">Target CLR type.</typeparam>
    /// <param name="ctx">EF Core context to query.</param>
    /// <param name="key">Configuration key stored in <c>EngineConfig.Key</c>.</param>
    /// <param name="defaultValue">Fallback value.</param>
    /// <param name="ct">Cooperative cancellation token.</param>
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
