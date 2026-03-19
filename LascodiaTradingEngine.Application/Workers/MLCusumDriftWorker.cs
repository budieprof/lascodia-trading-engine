using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects bidirectional accuracy drift using the Cumulative Sum (CUSUM) control chart.
///
/// <para>
/// Two CUSUM accumulators run in parallel:
/// <list type="bullet">
///   <item>C⁺ detects accuracy <i>degradation</i> (downward shift): fires when the model is getting worse.</item>
///   <item>C⁻ detects accuracy <i>improvement</i> (upward shift): optionally fires when the model improves
///       significantly, signalling an opportunity to re-calibrate.</item>
/// </list>
/// </para>
///
/// <para>
/// Update rule (one-sided CUSUM for degradation):
///   S_n⁺ = max(0, S_{n-1}⁺ + (μ_0 − x_n) − k)
/// where μ_0 is the reference accuracy, x_n is the accuracy of the nth resolved prediction,
/// and k is the allowable slack (default 0.005). An alert fires when S_n⁺ > h (threshold, default 5.0).
/// </para>
///
/// <para>
/// This is <b>complementary</b> to <c>MLPageHinkleyDriftWorker</c>:
/// Page-Hinkley detects <i>gradual</i> mean shifts; CUSUM is optimal for detecting <i>sudden</i> step changes.
/// Both can run simultaneously.
/// </para>
///
/// Config keys (under <c>MLCusum:</c>):
/// <c>PollIntervalSeconds</c>, <c>WindowSize</c>, <c>K</c> (slack, default 0.005),
/// <c>H</c> (decision interval, default 5.0), <c>AlertDestination</c>.
/// </summary>
public sealed class MLCusumDriftWorker : BackgroundService
{
    private const string CK_PollSecs  = "MLCusum:PollIntervalSeconds";
    private const string CK_Window    = "MLCusum:WindowSize";
    private const string CK_K         = "MLCusum:K";
    private const string CK_H         = "MLCusum:H";
    private const string CK_AlertDest = "MLCusum:AlertDestination";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<MLCusumDriftWorker>     _logger;

    public MLCusumDriftWorker(
        IServiceScopeFactory         scopeFactory,
        ILogger<MLCusumDriftWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCusumDriftWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCusumDriftWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCusumDriftWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private async Task CheckAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowSize = await GetConfigAsync<int>   (readCtx, CK_Window,    300,     ct);
        double k          = await GetConfigAsync<double>(readCtx, CK_K,         0.005,   ct);
        double h          = await GetConfigAsync<double>(readCtx, CK_H,         5.0,     ct);
        string alertDest  = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted)
            .ToListAsync(ct);

        _logger.LogDebug("CUSUM drift: {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await CheckModelAsync(model, readCtx, writeCtx, windowSize, k, h, alertDest, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CUSUM drift check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task CheckModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        double                                  k,
        double                                  h,
        string                                  alertDest,
        CancellationToken                       ct)
    {
        // ── Load resolved prediction logs in chronological order ──────────────
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId       == model.Id &&
                        l.DirectionCorrect != null    &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .ToListAsync(ct);

        if (logs.Count < 30)
        {
            _logger.LogDebug(
                "CUSUM skipped model {Id} — only {N} resolved logs (need 30).", model.Id, logs.Count);
            return;
        }

        // Reverse to chronological order
        logs.Reverse();

        // ── Estimate reference accuracy from the first half ───────────────────
        int    refHalf    = logs.Count / 2;
        double refAcc     = logs.Take(refHalf).Count(l => l.DirectionCorrect == true) / (double)refHalf;
        var    recentLogs = logs.Skip(refHalf).ToList();

        // ── Run CUSUM accumulators on the second half ─────────────────────────
        double sPlus  = 0; // degradation detector (accuracy drop)
        double sMinus = 0; // improvement detector (accuracy rise)
        bool   fired  = false;
        string fireType = string.Empty;
        int    fireIdx  = -1;

        for (int i = 0; i < recentLogs.Count; i++)
        {
            double x = recentLogs[i].DirectionCorrect == true ? 1.0 : 0.0;

            // Degradation CUSUM (upward CUSUM on error = 1 - accuracy)
            sPlus  = Math.Max(0, sPlus  + (refAcc - x) - k);
            // Improvement CUSUM (downward CUSUM on error)
            sMinus = Math.Max(0, sMinus + (x - refAcc) - k);

            if (sPlus >= h && !fired)
            {
                fired    = true;
                fireType = "Degradation";
                fireIdx  = i;
                break;
            }
        }

        if (!fired)
        {
            _logger.LogDebug(
                "CUSUM model {Id} ({Symbol}/{Tf}): S+={SPlus:F2} S-={SMinus:F2} h={H:F1} — no drift.",
                model.Id, model.Symbol, model.Timeframe, sPlus, sMinus, h);
            return;
        }

        double recentAcc = recentLogs.Take(fireIdx + 1).Count(l => l.DirectionCorrect == true)
                         / (double)(fireIdx + 1);

        _logger.LogWarning(
            "CUSUM {Type} drift detected model {Id} ({Symbol}/{Tf}): " +
            "refAcc={Ref:P1} recentAcc={Recent:P1} S+={SPlus:F2} >= h={H:F1} at step {Step}/{Total}.",
            fireType, model.Id, model.Symbol, model.Timeframe,
            refAcc, recentAcc, sPlus, h, fireIdx + 1, recentLogs.Count);

        // ── Suppress if retrain already queued ────────────────────────────────
        bool retrainQueued = await writeCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (retrainQueued)
        {
            _logger.LogDebug(
                "CUSUM alert suppressed for model {Id} — retrain already queued.", model.Id);
            return;
        }

        writeCtx.Set<Alert>().Add(new Alert
        {
            Symbol        = model.Symbol,
            AlertType     = AlertType.MLModelDegraded,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                DetectorType   = "CUSUM",
                ModelId        = model.Id,
                Timeframe      = model.Timeframe.ToString(),
                DriftType      = fireType,
                ReferenceAcc   = Math.Round(refAcc,    4),
                RecentAcc      = Math.Round(recentAcc, 4),
                CusumSPlus     = Math.Round(sPlus,     4),
                DecisionInterval = h,
                SlackK         = k,
                FireStep       = fireIdx + 1,
                WindowHalf     = recentLogs.Count,
            }),
            IsActive = true,
        });

        writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol      = model.Symbol,
            Timeframe   = model.Timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status      = RunStatus.Queued,
            FromDate    = DateTime.UtcNow.AddDays(-365),
            ToDate      = DateTime.UtcNow,
            StartedAt   = DateTime.UtcNow,
        });

        await writeCtx.SaveChangesAsync(ct);
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
