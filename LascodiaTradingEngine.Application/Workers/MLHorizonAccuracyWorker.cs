using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Aggregates multi-horizon direction accuracy for each active ML model using
/// the <c>HorizonCorrect3</c>, <c>HorizonCorrect6</c>, and <c>HorizonCorrect12</c>
/// fields on <see cref="MLModelPredictionLog"/>, and persists the results as
/// <see cref="MLModelHorizonAccuracy"/> rows (3 rows per model × horizon).
///
/// <b>Motivation:</b> The primary <c>DirectionCorrect</c> metric measures accuracy
/// at the 1-bar (next-candle) horizon. A model may score 60 % at 1 bar but only
/// 52 % at 3 bars, revealing that its edge decays rapidly. Conversely, a model that
/// scores 55 % at 1 bar but 62 % at 12 bars may be better suited to longer-hold
/// strategies. This worker makes that multi-horizon profile visible.
///
/// <b>Alert condition:</b> When the 3-bar accuracy is more than
/// <c>HorizonGapThreshold</c> below the primary direction accuracy, the model's
/// temporal edge is shallow — it is right about direction but wrong about
/// <i>when</i> the move occurs. An <see cref="AlertType.MLModelDegraded"/> alert
/// is raised with reason <c>"horizon_accuracy_gap"</c>.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLHorizon:PollIntervalSeconds</c>   — default 3600 (1 h)</item>
///   <item><c>MLHorizon:WindowDays</c>            — look-back window, default 30</item>
///   <item><c>MLHorizon:MinPredictions</c>        — minimum per horizon, default 20</item>
///   <item><c>MLHorizon:HorizonGapThreshold</c>   — gap alert floor (0–1), default 0.10</item>
///   <item><c>MLHorizon:AlertDestination</c>      — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLHorizonAccuracyWorker : BackgroundService
{
    private const string CK_PollSecs  = "MLHorizon:PollIntervalSeconds";
    private const string CK_Window    = "MLHorizon:WindowDays";
    private const string CK_MinPreds  = "MLHorizon:MinPredictions";
    private const string CK_GapThr    = "MLHorizon:HorizonGapThreshold";
    private const string CK_AlertDest = "MLHorizon:AlertDestination";

    // The three forward-look horizons tracked in prediction logs
    private static readonly (int Bars, string Field)[] Horizons =
    [
        (3,  "HorizonCorrect3"),
        (6,  "HorizonCorrect6"),
        (12, "HorizonCorrect12"),
    ];

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLHorizonAccuracyWorker>  _logger;

    public MLHorizonAccuracyWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLHorizonAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLHorizonAccuracyWorker started.");

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

                await ComputeAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLHorizonAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLHorizonAccuracyWorker stopping.");
    }

    // ── Computation core ──────────────────────────────────────────────────────

    private async Task ComputeAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays  = await GetConfigAsync<int>   (readCtx, CK_Window,    30,      ct);
        int    minPreds    = await GetConfigAsync<int>   (readCtx, CK_MinPreds,  20,      ct);
        double gapThr      = await GetConfigAsync<double>(readCtx, CK_GapThr,    0.10,    ct);
        string alertDest   = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        var windowStart = DateTime.UtcNow.AddDays(-windowDays);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ComputeForModelAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    windowStart, minPreds, gapThr, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "HorizonAccuracy: compute failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task ComputeForModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                windowStart,
        int                                     minPredictions,
        double                                  horizonGapThreshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load resolved prediction logs with all three horizon fields
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId     &&
                        l.DirectionCorrect != null         &&
                        l.PredictedAt      >= windowStart  &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => new
            {
                l.DirectionCorrect,
                l.HorizonCorrect3,
                l.HorizonCorrect6,
                l.HorizonCorrect12,
            })
            .ToListAsync(ct);

        if (logs.Count < minPredictions) return;

        var now = DateTime.UtcNow;

        // Primary direction accuracy (1-bar) for gap comparison
        int    primaryTotal   = logs.Count;
        int    primaryCorrect = logs.Count(l => l.DirectionCorrect == true);
        double primaryAcc     = (double)primaryCorrect / primaryTotal;

        // Compute and upsert each horizon
        foreach (var (horizonBars, _) in Horizons)
        {
            // Selector for the horizon-specific field
            var resolved = horizonBars switch
            {
                3  => logs.Where(l => l.HorizonCorrect3  != null).Select(l => l.HorizonCorrect3!.Value).ToList(),
                6  => logs.Where(l => l.HorizonCorrect6  != null).Select(l => l.HorizonCorrect6!.Value).ToList(),
                12 => logs.Where(l => l.HorizonCorrect12 != null).Select(l => l.HorizonCorrect12!.Value).ToList(),
                _  => new List<bool>(),
            };

            if (resolved.Count < minPredictions) continue;

            int    total    = resolved.Count;
            int    correct  = resolved.Count(v => v);
            double accuracy = (double)correct / total;

            int rows = await writeCtx.Set<MLModelHorizonAccuracy>()
                .Where(r => r.MLModelId == modelId && r.HorizonBars == horizonBars)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.TotalPredictions,   total)
                    .SetProperty(r => r.CorrectPredictions, correct)
                    .SetProperty(r => r.Accuracy,           accuracy)
                    .SetProperty(r => r.WindowStart,        windowStart)
                    .SetProperty(r => r.ComputedAt,         now),
                    ct);

            if (rows == 0)
            {
                writeCtx.Set<MLModelHorizonAccuracy>().Add(new MLModelHorizonAccuracy
                {
                    MLModelId          = modelId,
                    Symbol             = symbol,
                    Timeframe          = timeframe,
                    HorizonBars        = horizonBars,
                    TotalPredictions   = total,
                    CorrectPredictions = correct,
                    Accuracy           = accuracy,
                    WindowStart        = windowStart,
                    ComputedAt         = now,
                });
                await writeCtx.SaveChangesAsync(ct);
            }

            _logger.LogDebug(
                "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) h={H}bar — acc={Acc:P1} n={N}",
                modelId, symbol, timeframe, horizonBars, accuracy, total);

            // Alert when short-horizon accuracy lags primary accuracy significantly
            if (horizonBars == 3 && primaryAcc - accuracy > horizonGapThreshold)
            {
                bool alertExists = await readCtx.Set<Alert>()
                    .AnyAsync(a => a.Symbol    == symbol                  &&
                                   a.AlertType == AlertType.MLModelDegraded &&
                                   a.IsActive  && !a.IsDeleted, ct);

                if (!alertExists)
                {
                    _logger.LogWarning(
                        "HorizonAccuracy: model {Id} ({Symbol}/{Tf}) — primary={P:P1} h3={H3:P1} " +
                        "gap={Gap:P1} exceeds threshold {Thr:P0}. Model has shallow temporal edge.",
                        modelId, symbol, timeframe, primaryAcc, accuracy,
                        primaryAcc - accuracy, horizonGapThreshold);

                    writeCtx.Set<Alert>().Add(new Alert
                    {
                        AlertType     = AlertType.MLModelDegraded,
                        Symbol        = symbol,
                        Channel       = AlertChannel.Webhook,
                        Destination   = alertDest,
                        ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            reason                = "horizon_accuracy_gap",
                            severity              = "warning",
                            symbol,
                            timeframe             = timeframe.ToString(),
                            modelId,
                            primaryDirectionAcc   = primaryAcc,
                            horizon3BarAcc        = accuracy,
                            gap                   = primaryAcc - accuracy,
                            horizonGapThreshold,
                            sampleCount           = total,
                        }),
                        IsActive = true,
                    });
                    await writeCtx.SaveChangesAsync(ct);
                }
            }
        }
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
