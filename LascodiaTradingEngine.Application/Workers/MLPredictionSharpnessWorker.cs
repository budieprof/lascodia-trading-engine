using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors the sharpness (informativeness) of active ML model predictions by computing
/// a rolling binary entropy H over the last N confidence scores.
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

    public MLPredictionSharpnessWorker(
        IServiceScopeFactory                 scopeFactory,
        ILogger<MLPredictionSharpnessWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPredictionSharpnessWorker started.");

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
                _logger.LogError(ex, "MLPredictionSharpnessWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPredictionSharpnessWorker stopping.");
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private async Task CheckActiveModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowSize           = await GetConfigAsync<int>   (readCtx, CK_WindowSize,           100,      ct);
        double entropyAlertFraction = await GetConfigAsync<double>(readCtx, CK_EntropyAlertFraction, 0.90,     ct);
        string alertDest            = await GetConfigAsync<string>(readCtx, CK_AlertDest,            "ml-ops", ct);

        double alertThreshold = Ln2 * entropyAlertFraction;

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

    private async Task CheckModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowSize,
        double                                  alertThreshold,
        string                                  alertDest,
        CancellationToken                       ct)
    {
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId      == model.Id &&
                        l.ConfidenceScore > 0         &&
                        !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .ToListAsync(ct);

        if (logs.Count < 20)
        {
            _logger.LogDebug(
                "Sharpness skipped model {Id} — only {N} logs (need 20).",
                model.Id, logs.Count);
            return;
        }

        // Compute rolling entropy over confidence scores
        // p = calibrated probability of Buy
        // H = -(p*log(p) + (1-p)*log(1-p))
        double sumH = 0.0;
        foreach (var log in logs)
        {
            double conf   = Math.Clamp((double)log.ConfidenceScore, 0.0, 1.0);
            double p      = log.PredictedDirection == TradeDirection.Buy
                ? 0.5 + conf / 2.0
                : 0.5 - conf / 2.0;

            // Clamp to avoid log(0)
            p = Math.Clamp(p, 1e-10, 1.0 - 1e-10);
            sumH += -(p * Math.Log(p) + (1.0 - p) * Math.Log(1.0 - p));
        }

        double avgH = sumH / logs.Count;

        _logger.LogInformation(
            "Sharpness model {Id} ({Symbol}/{Tf}): avgH={H:F4} threshold={Thr:F4} (ln2={Ln2:F4})",
            model.Id, model.Symbol, model.Timeframe, avgH, alertThreshold, Ln2);

        if (avgH <= alertThreshold) return;

        // ── Low-sharpness alert ───────────────────────────────────────────────
        _logger.LogWarning(
            "LOW PREDICTION SHARPNESS model {Id} ({Symbol}/{Tf}): avgH={H:F4} > threshold={Thr:F4}. " +
            "Model is near-uninformative (entropy fraction={Frac:P1} of ln2).",
            model.Id, model.Symbol, model.Timeframe,
            avgH, alertThreshold, avgH / Ln2);

        var now = DateTime.UtcNow;

        // Suppress if a retrain run is already queued (proxy for a recent alert)
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
                EntropyFractionLn2 = avgH / Ln2,
                WindowSize        = logs.Count,
            }),
            IsActive = true,
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
