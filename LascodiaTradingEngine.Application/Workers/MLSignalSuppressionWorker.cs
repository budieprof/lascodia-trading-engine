using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Kill-switch worker: sets <see cref="MLModel.IsSuppressed"/> on active models whose
/// rolling prediction accuracy falls below a hard floor, and clears the flag when
/// accuracy recovers.
///
/// Motivation: <see cref="MLDriftMonitorWorker"/> queues a retrain when drift is
/// detected, but the degraded model continues scoring signals for hours or days while
/// the retrain runs. This worker bridges that gap by suppressing scoring immediately
/// once accuracy crosses the hard floor — <see cref="Services.MLSignalScorer"/> returns
/// an empty result for suppressed models (same as "no model deployed").
///
/// The suppression is <b>self-healing</b>:
/// <list type="bullet">
///   <item>Suppress  when rolling accuracy &lt; <c>HardAccuracyFloor</c>.</item>
///   <item>Un-suppress when rolling accuracy ≥ <c>HardAccuracyFloor</c>.</item>
///   <item>Skip the model entirely when fewer than <c>MinPredictions</c> resolved
///         predictions are available (do not change suppression state).</item>
/// </list>
///
/// A retrain is queued on every suppression event (idempotent — no duplicate if one
/// is already pending).
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLSuppression:PollIntervalSeconds</c>  — default 600 (10 min)</item>
///   <item><c>MLSuppression:WindowDays</c>           — rolling accuracy window, default 7</item>
///   <item><c>MLSuppression:MinPredictions</c>       — minimum resolved predictions, default 20</item>
///   <item><c>MLSuppression:HardAccuracyFloor</c>    — suppression trigger, default 0.44</item>
/// </list>
/// </summary>
public sealed class MLSignalSuppressionWorker : BackgroundService
{
    private const string CK_PollSecs          = "MLSuppression:PollIntervalSeconds";
    private const string CK_WindowDays        = "MLSuppression:WindowDays";
    private const string CK_MinPredictions    = "MLSuppression:MinPredictions";
    private const string CK_HardAccuracyFloor = "MLSuppression:HardAccuracyFloor";

    private readonly IServiceScopeFactory                _scopeFactory;
    private readonly ILogger<MLSignalSuppressionWorker>  _logger;

    public MLSignalSuppressionWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLSignalSuppressionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLSignalSuppressionWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 600;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 600, stoppingToken);

                await EvaluateSuppressionAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLSignalSuppressionWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLSignalSuppressionWorker stopping.");
    }

    private async Task EvaluateSuppressionAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays       = await GetConfigAsync<int>   (readCtx, CK_WindowDays,        7,    ct);
        int    minPredictions   = await GetConfigAsync<int>   (readCtx, CK_MinPredictions,    20,   ct);
        double hardAccuracyFloor = await GetConfigAsync<double>(readCtx, CK_HardAccuracyFloor, 0.44, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await EvaluateModelAsync(
                    model, readCtx, writeCtx,
                    windowDays, minPredictions, hardAccuracyFloor, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Suppression evaluation failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task EvaluateModelAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minPredictions,
        double                                  hardAccuracyFloor,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id &&
                        l.PredictedAt      >= since    &&
                        l.DirectionCorrect != null      &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => l.DirectionCorrect!.Value)
            .ToListAsync(ct);

        if (resolved.Count < minPredictions)
        {
            _logger.LogDebug(
                "Suppression: {Symbol}/{Tf} model {Id} only {N} resolved predictions (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minPredictions);
            return;
        }

        double rollingAccuracy = resolved.Count(c => c) / (double)resolved.Count;
        bool shouldSuppress    = rollingAccuracy < hardAccuracyFloor;

        _logger.LogDebug(
            "Suppression: {Symbol}/{Tf} model {Id}: rollingAcc={Acc:P1} floor={Floor:P1} " +
            "currentlySuppressed={Supp}",
            model.Symbol, model.Timeframe, model.Id,
            rollingAccuracy, hardAccuracyFloor, model.IsSuppressed);

        if (shouldSuppress && !model.IsSuppressed)
        {
            // ── Suppress ──────────────────────────────────────────────────────
            _logger.LogWarning(
                "Suppression: SUPPRESSING {Symbol}/{Tf} model {Id} — rolling accuracy {Acc:P1} " +
                "< hard floor {Floor:P1}. Signals will be blocked until recovery or retrain.",
                model.Symbol, model.Timeframe, model.Id, rollingAccuracy, hardAccuracyFloor);

            await writeCtx.Set<MLModel>()
                .Where(m => m.Id == model.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsSuppressed, true), ct);

            // Queue retrain if not already pending
            bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
                .AnyAsync(r => r.Symbol    == model.Symbol    &&
                               r.Timeframe == model.Timeframe &&
                               (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

            if (!alreadyQueued)
            {
                writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
                {
                    Symbol    = model.Symbol,
                    Timeframe = model.Timeframe,
                    Status    = RunStatus.Queued,
                    HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        triggeredBy      = "MLSignalSuppressionWorker",
                        rollingAccuracy,
                        hardAccuracyFloor,
                        windowDays,
                        resolvedCount    = resolved.Count,
                        modelId          = model.Id,
                    }),
                });
                await writeCtx.SaveChangesAsync(ct);
            }
        }
        else if (!shouldSuppress && model.IsSuppressed)
        {
            // ── Un-suppress (accuracy recovered) ────────────────────────────
            _logger.LogInformation(
                "Suppression: LIFTING suppression for {Symbol}/{Tf} model {Id} — rolling accuracy " +
                "{Acc:P1} has recovered above floor {Floor:P1}.",
                model.Symbol, model.Timeframe, model.Id, rollingAccuracy, hardAccuracyFloor);

            await writeCtx.Set<MLModel>()
                .Where(m => m.Id == model.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsSuppressed, false), ct);
        }
    }

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
