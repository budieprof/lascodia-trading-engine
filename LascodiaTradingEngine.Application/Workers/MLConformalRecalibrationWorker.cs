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

    public MLConformalRecalibrationWorker(
        IServiceScopeFactory                      scopeFactory,
        IMemoryCache                              cache,
        ILogger<MLConformalRecalibrationWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

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

    private async Task RecalibrateAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
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

    private sealed record ScoreEntry(double Score, bool WasCorrect);

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
        var since = DateTime.UtcNow.AddDays(-windowDays);

        // Load resolved logs with confidence scores
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null      &&
                        l.ConfidenceScore  > 0          &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new ScoreEntry(
                (double)l.ConfidenceScore,
                l.DirectionCorrect!.Value))
            .ToListAsync(ct);

        if (logs.Count < minPredictions)
        {
            _logger.LogDebug(
                "ConformalRecal: {Symbol}/{Tf} model {Id}: only {N} resolved logs (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, logs.Count, minPredictions);
            return;
        }

        // Deserialise snapshot to get current QHat
        ModelSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!); }
        catch { return; }
        if (snap is null) return;

        double currentQHat = snap.ConformalQHat;

        // Compute nonconformity scores:
        // score_i = DirectionCorrect ? (1 - confidence) : confidence
        var scores = logs
            .Select(l => l.WasCorrect ? (1.0 - l.Score) : l.Score)
            .OrderBy(s => s)
            .ToList();

        int    n           = scores.Count;
        double nominalCoverage = 1.0 - nominalAlpha;

        // Conformal quantile: ceil((1-alpha)(n+1)) / n — pinball at nominal level
        int quantileIdx = (int)Math.Ceiling(nominalCoverage * (n + 1)) - 1;
        quantileIdx     = Math.Clamp(quantileIdx, 0, n - 1);
        double newQHat  = scores[quantileIdx];

        // Actual coverage at current QHat: fraction of predictions where score <= currentQHat
        // (i.e., the model's nonconformity score is within the threshold — prediction is accepted)
        double empiricalCoverage = scores.Count(s => s <= currentQHat) / (double)n;
        double coverageDrift     = Math.Abs(empiricalCoverage - nominalCoverage);

        _logger.LogDebug(
            "ConformalRecal: {Symbol}/{Tf} model {Id}: " +
            "currentQHat={CQ:F4} newQHat={NQ:F4} empiricalCoverage={EC:P1} nominal={NC:P1} drift={D:P1}",
            model.Symbol, model.Timeframe, model.Id,
            currentQHat, newQHat, empiricalCoverage, nominalCoverage, coverageDrift);

        if (coverageDrift <= driftTolerance)
            return; // coverage is within tolerance — no patch needed

        _logger.LogInformation(
            "ConformalRecal: {Symbol}/{Tf} model {Id}: coverage drifted {Drift:P1} " +
            "(empirical={Emp:P1} vs nominal={Nom:P1}). Patching ConformalQHat {Old:F4} → {New:F4}.",
            model.Symbol, model.Timeframe, model.Id,
            coverageDrift, empiricalCoverage, nominalCoverage, currentQHat, newQHat);

        // Patch snapshot in-place
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
