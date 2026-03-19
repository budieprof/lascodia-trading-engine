using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects two qualitatively different forms of ML model degradation by computing
/// direction accuracy over two rolling windows simultaneously and comparing them.
///
/// <b>Sudden drift</b> (regime change, data pipeline fault, feature distribution flip):
///   The short-window accuracy collapses much faster than the long-window average.
///   Detected when <c>shortAccuracy − longAccuracy &lt; −ShortLongAccuracyGap</c>.
///   Treated as urgent: creates a <see cref="AlertType.MLModelDegraded"/> alert with
///   <c>severity=critical</c> and queues an immediate retrain.
///
/// <b>Gradual drift</b> (slow concept drift, regime shift over weeks):
///   Both windows fall below the long-window accuracy floor simultaneously.
///   Detected when <c>longAccuracy &lt; LongWindowFloor</c>.
///   Treated as standard: queues a retrain with <c>TriggerType.AutoDegrading</c>.
///
/// The single-window <see cref="MLDriftMonitorWorker"/> continues to monitor accuracy,
/// Brier score, and ensemble disagreement. This worker is complementary — it adds
/// temporal context that a point-in-time check cannot provide.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLMultiScaleDrift:PollIntervalSeconds</c>   — default 1800 (30 min)</item>
///   <item><c>MLMultiScaleDrift:ShortWindowDays</c>       — short window, default 3</item>
///   <item><c>MLMultiScaleDrift:LongWindowDays</c>        — long window, default 21</item>
///   <item><c>MLMultiScaleDrift:MinPredictions</c>        — minimum resolved predictions in the long window, default 20</item>
///   <item><c>MLMultiScaleDrift:ShortLongAccuracyGap</c>  — sudden-drift trigger, default 0.07</item>
///   <item><c>MLMultiScaleDrift:LongWindowFloor</c>       — gradual-drift trigger, default 0.50</item>
/// </list>
/// </summary>
public sealed class MLMultiScaleDriftWorker : BackgroundService
{
    private const string CK_PollSecs           = "MLMultiScaleDrift:PollIntervalSeconds";
    private const string CK_ShortWindowDays    = "MLMultiScaleDrift:ShortWindowDays";
    private const string CK_LongWindowDays     = "MLMultiScaleDrift:LongWindowDays";
    private const string CK_MinPredictions     = "MLMultiScaleDrift:MinPredictions";
    private const string CK_ShortLongGap       = "MLMultiScaleDrift:ShortLongAccuracyGap";
    private const string CK_LongWindowFloor    = "MLMultiScaleDrift:LongWindowFloor";

    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly ILogger<MLMultiScaleDriftWorker> _logger;

    public MLMultiScaleDriftWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLMultiScaleDriftWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLMultiScaleDriftWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 1800;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 1800, stoppingToken);

                await CheckMultiScaleDriftAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLMultiScaleDriftWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLMultiScaleDriftWorker stopping.");
    }

    private async Task CheckMultiScaleDriftAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    shortWindowDays  = await GetConfigAsync<int>   (readCtx, CK_ShortWindowDays, 3,    ct);
        int    longWindowDays   = await GetConfigAsync<int>   (readCtx, CK_LongWindowDays,  21,   ct);
        int    minPredictions   = await GetConfigAsync<int>   (readCtx, CK_MinPredictions,  20,   ct);
        double shortLongGap     = await GetConfigAsync<double>(readCtx, CK_ShortLongGap,    0.07, ct);
        double longWindowFloor  = await GetConfigAsync<double>(readCtx, CK_LongWindowFloor, 0.50, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelDriftAsync(
                    model, readCtx, writeCtx,
                    shortWindowDays, longWindowDays, minPredictions,
                    shortLongGap, longWindowFloor, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Multi-scale drift check failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task CheckModelDriftAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     shortWindowDays,
        int                                     longWindowDays,
        int                                     minPredictions,
        double                                  shortLongGap,
        double                                  longWindowFloor,
        CancellationToken                       ct)
    {
        var longSince  = DateTime.UtcNow.AddDays(-longWindowDays);
        var shortSince = DateTime.UtcNow.AddDays(-shortWindowDays);

        // Load all resolved predictions within the long window
        var allResolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == model.Id  &&
                        l.PredictedAt      >= longSince &&
                        l.DirectionCorrect != null       &&
                        !l.IsDeleted)
            .Select(l => new { l.PredictedAt, DirectionCorrect = l.DirectionCorrect!.Value })
            .AsNoTracking()
            .ToListAsync(ct);

        if (allResolved.Count < minPredictions)
        {
            _logger.LogDebug(
                "MultiScaleDrift: {Symbol}/{Tf} model {Id} only {N} resolved in long window — skip.",
                model.Symbol, model.Timeframe, model.Id, allResolved.Count);
            return;
        }

        double longAccuracy  = allResolved.Count(r => r.DirectionCorrect) / (double)allResolved.Count;

        var shortResolved = allResolved.Where(r => r.PredictedAt >= shortSince).ToList();
        if (shortResolved.Count < Math.Max(5, minPredictions / 4))
        {
            _logger.LogDebug(
                "MultiScaleDrift: {Symbol}/{Tf} model {Id} insufficient short-window predictions ({N}) — skip.",
                model.Symbol, model.Timeframe, model.Id, shortResolved.Count);
            return;
        }

        double shortAccuracy = shortResolved.Count(r => r.DirectionCorrect) / (double)shortResolved.Count;
        double gap            = shortAccuracy - longAccuracy;

        _logger.LogDebug(
            "MultiScaleDrift: {Symbol}/{Tf} model {Id}: short={Short:P1}(n={Ns}) " +
            "long={Long:P1}(n={Nl}) gap={Gap:+0.0%;-0.0%}",
            model.Symbol, model.Timeframe, model.Id,
            shortAccuracy, shortResolved.Count, longAccuracy, allResolved.Count, gap);

        bool suddenDrift  = gap < -shortLongGap;
        bool gradualDrift = !suddenDrift && longAccuracy < longWindowFloor;

        if (!suddenDrift && !gradualDrift) return;

        string driftType = suddenDrift ? "sudden" : "gradual";

        _logger.LogWarning(
            "MultiScaleDrift: {Symbol}/{Tf} model {Id}: {DriftType} drift detected. " +
            "short={Short:P1} long={Long:P1} gap={Gap:+0.0%;-0.0%} floor={Floor:P1}",
            model.Symbol, model.Timeframe, model.Id, driftType,
            shortAccuracy, longAccuracy, gap, longWindowFloor);

        // Queue retrain if not already pending
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        bool saved = false;

        if (!alreadyQueued)
        {
            writeCtx.Set<MLTrainingRun>().Add(new MLTrainingRun
            {
                Symbol    = model.Symbol,
                Timeframe = model.Timeframe,
                Status    = RunStatus.Queued,
                HyperparamConfigJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    triggeredBy    = "MLMultiScaleDriftWorker",
                    driftType,
                    shortAccuracy,
                    longAccuracy,
                    gap,
                    shortWindowDays,
                    longWindowDays,
                    modelId        = model.Id,
                }),
            });
            saved = true;
        }

        // Create alert (deduplicated)
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == model.Symbol              &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (!alertExists)
        {
            writeCtx.Set<Alert>().Add(new Alert
            {
                AlertType     = AlertType.MLModelDegraded,
                Symbol        = model.Symbol,
                Channel       = AlertChannel.Webhook,
                Destination   = "ml-ops",
                ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason          = "multi_scale_drift",
                    driftType,
                    severity        = suddenDrift ? "critical" : "standard",
                    shortAccuracy,
                    longAccuracy,
                    gap,
                    shortWindowDays,
                    longWindowDays,
                    symbol          = model.Symbol,
                    timeframe       = model.Timeframe.ToString(),
                    modelId         = model.Id,
                }),
                IsActive = true,
            });
            saved = true;
        }

        if (saved) await writeCtx.SaveChangesAsync(ct);
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
