using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Application.Services.Alerts;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Tracks Expected Calibration Error (ECE) as a first-class metric for active ML models.
///
/// <para>
/// ECE measures how well a model's predicted probabilities align with observed outcomes.
/// A perfectly calibrated model predicting 70 % confidence should be correct ~70 % of the time.
/// This worker bins resolved predictions by predicted probability, computes per-bin accuracy,
/// and aggregates the weighted absolute difference — the standard 10-bin ECE formulation.
/// </para>
///
/// <list type="bullet">
///   <item>Polls every hour (configurable via <c>MLCalibration:PollIntervalSeconds</c>).</item>
///   <item>For each active model, loads resolved prediction logs from the last 14 days.</item>
///   <item>Computes ECE: bin predictions into 10 bins by predicted probability, compute
///         |accuracy - avg_confidence| per bin, weighted average by bin count.</item>
///   <item>Writes ECE to <see cref="EngineConfig"/>: <c>MLCalibration:{Symbol}:{Tf}:CurrentEce</c>.</item>
///   <item>If ECE exceeds <c>MLCalibration:MaxEce</c> (default 0.15): creates an alert.</item>
///   <item>Tracks ECE trend: if ECE increased by &gt; 0.05 from previous measurement, flags as
///         "calibration degrading" via a separate config key and warning log.</item>
/// </list>
/// </summary>
public sealed class MLCalibrationMonitorWorker : BackgroundService
{
    // ── Config keys ────────────────────────────────────────────────────────────
    private const string CK_PollSecs         = "MLCalibration:PollIntervalSeconds";
    private const string CK_WindowDays       = "MLCalibration:WindowDays";
    private const string CK_MaxEce           = "MLCalibration:MaxEce";
    private const string CK_DegradationDelta = "MLCalibration:DegradationDelta";
    private const string CK_AlertDestination = "MLCalibration:AlertDestination";
    private const int    NumBins             = 10;

    private readonly IServiceScopeFactory                  _scopeFactory;
    private readonly ILogger<MLCalibrationMonitorWorker>   _logger;

    public MLCalibrationMonitorWorker(
        IServiceScopeFactory                 scopeFactory,
        ILogger<MLCalibrationMonitorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCalibrationMonitorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 3600; // default hourly

            try
            {
                await using var scope    = _scopeFactory.CreateAsyncScope();
                var writeDb  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var readDb   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var readCtx  = readDb.GetDbContext();
                var writeCtx = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>   (readCtx, CK_PollSecs,         3600, stoppingToken);
                int    windowDays      = await GetConfigAsync<int>   (readCtx, CK_WindowDays,       14,   stoppingToken);
                double maxEce          = await GetConfigAsync<double>(readCtx, CK_MaxEce,           0.15, stoppingToken);
                double degradeDelta    = await GetConfigAsync<double>(readCtx, CK_DegradationDelta, 0.05, stoppingToken);
                string alertDest       = await GetConfigAsync<string>(readCtx, CK_AlertDestination, "",   stoppingToken);
                int    alertCooldown   = await GetConfigAsync<int>   (readCtx, AlertCooldownDefaults.CK_MLMonitoring, AlertCooldownDefaults.Default_MLMonitoring, stoppingToken);

                await CheckAllModelsAsync(readCtx, writeCtx, windowDays, maxEce, degradeDelta, alertDest, alertCooldown, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCalibrationMonitorWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCalibrationMonitorWorker stopping.");
    }

    // ── Per-model ECE computation ─────────────────────────────────────────────

    private async Task CheckAllModelsAsync(
        DbContext         readCtx,
        DbContext         writeCtx,
        int               windowDays,
        double            maxEce,
        double            degradeDelta,
        string            alertDest,
        int               alertCooldown,
        CancellationToken ct)
    {
        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug(
            "MLCalibrationMonitorWorker: checking {Count} active model(s) (window={Days}d maxEce={E:F2}).",
            activeModels.Count, windowDays, maxEce);

        bool anyChange = false;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            anyChange |= await CheckModelEceAsync(model, readCtx, writeCtx, windowDays, maxEce, degradeDelta, alertDest, alertCooldown, ct);
        }

        if (anyChange)
            await writeCtx.SaveChangesAsync(ct);
    }

    private async Task<bool> CheckModelEceAsync(
        MLModel           model,
        DbContext         readCtx,
        DbContext         writeCtx,
        int               windowDays,
        double            maxEce,
        double            degradeDelta,
        string            alertDest,
        int               alertCooldown,
        CancellationToken ct)
    {
        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        // Load resolved prediction logs for this model within the window.
        // We need: predicted probability (ConfidenceScore or ServedCalibratedProbability)
        // and actual outcome (DirectionCorrect).
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(p => p.MLModelId      == model.Id
                     && p.DirectionCorrect.HasValue
                     && p.PredictedAt    >= windowStart)
            .Select(p => new
            {
                Probability = p.ServedCalibratedProbability ?? p.CalibratedProbability ?? p.ConfidenceScore,
                Correct     = p.DirectionCorrect!.Value,
            })
            .ToListAsync(ct);

        if (logs.Count < 20)
        {
            _logger.LogDebug(
                "Model {Id} ({Symbol}/{Tf}): only {N} resolved predictions — skipping ECE.",
                model.Id, model.Symbol, model.Timeframe, logs.Count);
            return false;
        }

        // ── Compute 10-bin ECE ───────────────────────────────────────────────
        var binCounts    = new int[NumBins];
        var binCorrect   = new int[NumBins];
        var binConfSum   = new double[NumBins];

        foreach (var log in logs)
        {
            double prob = (double)log.Probability;
            int bin = Math.Clamp((int)(prob * NumBins), 0, NumBins - 1);

            binCounts[bin]++;
            binConfSum[bin] += prob;
            if (log.Correct) binCorrect[bin]++;
        }

        double ece = 0;
        int totalSamples = logs.Count;

        for (int b = 0; b < NumBins; b++)
        {
            if (binCounts[b] == 0) continue;

            double accuracy   = (double)binCorrect[b] / binCounts[b];
            double confidence = binConfSum[b] / binCounts[b];
            ece += ((double)binCounts[b] / totalSamples) * Math.Abs(accuracy - confidence);
        }

        string symbol = model.Symbol;
        string tf     = model.Timeframe.ToString();

        _logger.LogDebug(
            "Model {Id} ({Symbol}/{Tf}): ECE={Ece:F4} from {N} predictions.",
            model.Id, symbol, tf, ece, totalSamples);

        // ── Write current ECE to EngineConfig ────────────────────────────────
        string eceKey = $"MLCalibration:{symbol}:{tf}:CurrentEce";
        string prevEceKey = $"MLCalibration:{symbol}:{tf}:PreviousEce";

        // Read previous ECE for trend detection
        double previousEce = await GetConfigAsync<double>(readCtx, eceKey, -1.0, ct);

        // Shift current → previous, then write new current
        if (previousEce >= 0)
            await UpsertConfigAsync(writeCtx, prevEceKey, previousEce.ToString("F6"), ct);

        await UpsertConfigAsync(writeCtx, eceKey, ece.ToString("F6"), ct);

        bool anyAlert = false;

        // ── ECE threshold alert ──────────────────────────────────────────────
        if (ece > maxEce)
        {
            _logger.LogWarning(
                "Model {Id} ({Symbol}/{Tf}): ECE={Ece:F4} exceeds threshold {Max:F2}.",
                model.Id, symbol, tf, ece, maxEce);

            writeCtx.Set<Alert>().Add(new Alert
            {
                Symbol        = symbol,
                AlertType     = AlertType.MLModelDegraded,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    ModelId   = model.Id,
                    Timeframe = tf,
                    Ece       = ece,
                    Threshold = maxEce,
                    Samples   = totalSamples,
                    Reason    = "ECE exceeds threshold",
                }),
                DeduplicationKey = $"MLCalibration:ECE:{symbol}:{tf}",
                CooldownSeconds  = alertCooldown,
            });

            anyAlert = true;
        }

        // ── ECE trend degradation alert ──────────────────────────────────────
        if (previousEce >= 0 && (ece - previousEce) > degradeDelta)
        {
            string degradeKey = $"MLCalibration:{symbol}:{tf}:CalibrationDegrading";
            await UpsertConfigAsync(writeCtx, degradeKey, "true", ct);

            _logger.LogWarning(
                "Model {Id} ({Symbol}/{Tf}): calibration degrading — ECE rose from {Prev:F4} to {Curr:F4} (delta={D:F4} > {Thresh:F2}).",
                model.Id, symbol, tf, previousEce, ece, ece - previousEce, degradeDelta);

            writeCtx.Set<Alert>().Add(new Alert
            {
                Symbol        = symbol,
                AlertType     = AlertType.MLModelDegraded,
                ConditionJson = JsonSerializer.Serialize(new
                {
                    ModelId      = model.Id,
                    Timeframe    = tf,
                    CurrentEce   = ece,
                    PreviousEce  = previousEce,
                    Delta        = ece - previousEce,
                    Threshold    = degradeDelta,
                    Reason       = "Calibration degrading",
                }),
                DeduplicationKey = $"MLCalibration:Degrade:{symbol}:{tf}",
                CooldownSeconds  = alertCooldown,
            });

            anyAlert = true;
        }
        else if (previousEce >= 0)
        {
            // Clear degradation flag if ECE is stable or improving
            string degradeKey = $"MLCalibration:{symbol}:{tf}:CalibrationDegrading";
            await UpsertConfigAsync(writeCtx, degradeKey, "false", ct);
        }

        return anyAlert;
    }

    // ── Config helpers ────────────────────────────────────────────────────────

    private static async Task<T> GetConfigAsync<T>(
        DbContext         ctx,
        string            key,
        T                 defaultValue,
        CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try   { return (T)Convert.ChangeType(entry.Value, typeof(T)); }
        catch { return defaultValue; }
    }

    private static Task UpsertConfigAsync(
        DbContext         writeCtx,
        string            key,
        string            value,
        CancellationToken ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: LascodiaTradingEngine.Domain.Enums.ConfigDataType.Decimal, ct: ct);
}
