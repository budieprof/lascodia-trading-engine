using System.Text.Json;
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
/// Monitors the production calibration quality of active <see cref="MLModel"/> instances
/// by computing a rolling Expected Calibration Error (ECE) over recently resolved
/// <see cref="MLModelPredictionLog"/> records and comparing it to the training-time ECE
/// stored in the model's <see cref="ModelSnapshot"/>.
///
/// Actions taken when calibration degrades beyond the threshold:
/// <list type="bullet">
///   <item>Creates a high-priority <see cref="Alert"/> for the ML ops team.</item>
///   <item>Queues a new <see cref="MLTrainingRun"/> to retrain and recalibrate the model,
///         unless one is already queued or running.</item>
/// </list>
///
/// ECE is computed with 10 equal-width confidence bins using the formula:
/// ECE = Σ_m (|Bm| / n) × |acc(Bm) − conf(Bm)|
/// where Bm is the set of predictions falling in bin m.
/// </summary>
public sealed class MLProductionCalibrationWorker : BackgroundService
{
    private const string CK_PollSecs       = "MLCalibration:PollIntervalSeconds";
    private const string CK_WindowSize     = "MLCalibration:WindowSize";
    private const string CK_EceDeltaAlert  = "MLCalibration:EceDeltaToAlert";
    private const string CK_AlertDest      = "MLCalibration:AlertDestination";

    private const int CalibrationBins = 10;

    private readonly IServiceScopeFactory                       _scopeFactory;
    private readonly ILogger<MLProductionCalibrationWorker>     _logger;

    public MLProductionCalibrationWorker(
        IServiceScopeFactory                    scopeFactory,
        ILogger<MLProductionCalibrationWorker>  logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLProductionCalibrationWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 3600, stoppingToken);

                await CheckActiveModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLProductionCalibrationWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLProductionCalibrationWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private async Task CheckActiveModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowSize  = await GetConfigAsync<int>   (readCtx, CK_WindowSize,    200,        ct);
        double eceDelta    = await GetConfigAsync<double>(readCtx, CK_EceDeltaAlert, 0.05,       ct);
        string alertDest   = await GetConfigAsync<string>(readCtx, CK_AlertDest,    "ml-ops",   ct);

        var activeModels = await readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && m.ModelBytes != null && m.RegimeScope == null)
            .ToListAsync(ct);

        _logger.LogDebug("Production calibration: checking {Count} active model(s).", activeModels.Count);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            await CheckModelCalibrationAsync(model, readCtx, writeCtx, windowSize, eceDelta, alertDest, ct);
        }
    }

    private async Task CheckModelCalibrationAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        double                                  eceDelta,
        string                                  alertDest,
        CancellationToken                       ct)
    {
        // ── Load model snapshot to get training-time ECE ──────────────────────
        ModelSnapshot? snap = null;
        try
        {
            snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model {Id}: failed to deserialise snapshot — skipping calibration check", model.Id);
            return;
        }

        if (snap is null) return;

        double trainingEce = snap.Ece;

        // ── Load last N resolved prediction logs for this model ───────────────
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId        == model.Id &&
                        l.DirectionCorrect != null     &&
                        l.ConfidenceScore  > 0         &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .ToListAsync(ct);

        if (logs.Count < 30)
        {
            _logger.LogDebug(
                "Model {Id}: only {N} resolved logs — need at least 30 for calibration check, skipping",
                model.Id, logs.Count);
            return;
        }

        // ── Compute production ECE ────────────────────────────────────────────
        // Reconstruct calibrated probability from confidence + direction.
        // ConfidenceScore was computed as Math.Clamp(rawConviction × disgrFactor, 0, 1).
        // We approximate calibP as:
        //   P(Buy) ≈ 0.5 + confidence/2  when predicted Buy
        //   P(Buy) ≈ 0.5 - confidence/2  when predicted Sell
        var binAcc  = new double[CalibrationBins];
        var binConf = new double[CalibrationBins];
        var binN    = new int[CalibrationBins];

        foreach (var log in logs)
        {
            if (log.DirectionCorrect is null) continue;

            double conf   = Math.Clamp((double)log.ConfidenceScore, 0.0, 1.0);
            double calibP = log.PredictedDirection == TradeDirection.Buy
                ? 0.5 + conf / 2.0
                : 0.5 - conf / 2.0;

            int bin = Math.Clamp((int)(calibP * CalibrationBins), 0, CalibrationBins - 1);
            binN[bin]++;
            binConf[bin] += calibP;
            if (log.DirectionCorrect.Value) binAcc[bin]++;
        }

        double prodEce = 0.0;
        int total = logs.Count(l => l.DirectionCorrect.HasValue);

        for (int m = 0; m < CalibrationBins; m++)
        {
            if (binN[m] == 0) continue;
            double acc  = binAcc[m]  / binN[m];
            double conf = binConf[m] / binN[m];
            prodEce += (double)binN[m] / total * Math.Abs(acc - conf);
        }

        double eceDegradation = prodEce - trainingEce;

        _logger.LogInformation(
            "Calibration check model {Id} ({Symbol}/{Tf}): " +
            "trainingEce={TrainEce:F4} productionEce={ProdEce:F4} degradation={Delta:F4} " +
            "resolvedLogs={N} threshold={Thr:F4}",
            model.Id, model.Symbol, model.Timeframe,
            trainingEce, prodEce, eceDegradation, logs.Count, eceDelta);

        if (eceDegradation <= eceDelta) return;

        // ── ECE degradation detected — alert + queue retrain ──────────────────
        _logger.LogWarning(
            "CALIBRATION DRIFT: model {Id} ({Symbol}/{Tf}) prodEce={ProdEce:F4} vs " +
            "trainingEce={TrainEce:F4} (delta={Delta:F4} > threshold {Thr:F4}). " +
            "Queuing retraining run.",
            model.Id, model.Symbol, model.Timeframe,
            prodEce, trainingEce, eceDegradation, eceDelta);

        var now = DateTime.UtcNow;

        // Create alert
        writeCtx.Set<Alert>().Add(new Alert
        {
            Symbol        = model.Symbol,
            AlertType     = AlertType.MLModelDegraded,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ModelId        = model.Id,
                Timeframe      = model.Timeframe.ToString(),
                ProductionEce  = prodEce,
                TrainingEce    = trainingEce,
                EceDegradation = eceDegradation,
                Threshold      = eceDelta,
                ResolvedLogs   = logs.Count,
            }),
            IsActive = true,
        });

        // Queue retraining if not already queued
        bool alreadyQueued = await writeCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (!alreadyQueued)
        {
            writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol      = model.Symbol,
                Timeframe   = model.Timeframe,
                TriggerType = TriggerType.AutoDegrading,
                Status      = RunStatus.Queued,
                FromDate    = now.AddDays(-365),
                ToDate      = now,
                StartedAt   = now,
            });
        }

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
