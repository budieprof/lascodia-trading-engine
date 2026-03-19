using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes and maintains an Exponentially-Weighted Moving Average (EWMA) accuracy for
/// each active ML model, providing a faster-responding live performance signal than the
/// equal-weighted rolling accuracy used by <c>MLRollingAccuracyWorker</c>.
///
/// <b>EWMA formula:</b>
/// <c>ewma_t = α × outcome_t + (1−α) × ewma_{t-1}</c>
/// where <c>outcome_t ∈ {1, 0}</c> (correct / incorrect).
///
/// With α = 0.05, the EWMA responds to a directional accuracy change approximately
/// 3–5× faster than an equal-weighted 30-prediction rolling window:
/// a sustained change in model behaviour becomes visible within ~20 predictions
/// rather than 30.
///
/// <b>Alert tiers:</b>
/// <list type="bullet">
///   <item>EWMA &lt; <c>WarnThreshold</c> (default 0.50): <c>Warning</c> alert.</item>
///   <item>EWMA &lt; <c>CriticalThreshold</c> (default 0.48): <c>Critical</c> alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLEwma:PollIntervalSeconds</c>  — default 600 (10 min)</item>
///   <item><c>MLEwma:Alpha</c>                — smoothing factor, default 0.05</item>
///   <item><c>MLEwma:MinPredictions</c>       — warm-up count before alerting, default 20</item>
///   <item><c>MLEwma:WarnThreshold</c>        — warning alert floor, default 0.50</item>
///   <item><c>MLEwma:CriticalThreshold</c>    — critical alert floor, default 0.48</item>
///   <item><c>MLEwma:AlertDestination</c>     — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLEwmaAccuracyWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLEwma:PollIntervalSeconds";
    private const string CK_Alpha      = "MLEwma:Alpha";
    private const string CK_MinPreds   = "MLEwma:MinPredictions";
    private const string CK_WarnThr    = "MLEwma:WarnThreshold";
    private const string CK_CritThr    = "MLEwma:CriticalThreshold";
    private const string CK_AlertDest  = "MLEwma:AlertDestination";

    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly ILogger<MLEwmaAccuracyWorker>  _logger;

    public MLEwmaAccuracyWorker(
        IServiceScopeFactory            scopeFactory,
        ILogger<MLEwmaAccuracyWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLEwmaAccuracyWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 600;

            try
            {
                await using var scope   = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 600, stoppingToken);

                await UpdateEwmaAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLEwmaAccuracyWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLEwmaAccuracyWorker stopping.");
    }

    // ── EWMA update core ──────────────────────────────────────────────────────

    private async Task UpdateEwmaAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        double alpha         = await GetConfigAsync<double>(readCtx, CK_Alpha,     0.05,    ct);
        int    minPredictions = await GetConfigAsync<int>  (readCtx, CK_MinPreds,  20,      ct);
        double warnThreshold  = await GetConfigAsync<double>(readCtx, CK_WarnThr,  0.50,    ct);
        double critThreshold  = await GetConfigAsync<double>(readCtx, CK_CritThr,  0.48,    ct);
        string alertDest      = await GetConfigAsync<string>(readCtx, CK_AlertDest,"ml-ops", ct);

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
                await UpdateModelEwmaAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    alpha, minPredictions, warnThreshold, critThreshold, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "EwmaAccuracy: update failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task UpdateModelEwmaAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        double                                  alpha,
        int                                     minPredictions,
        double                                  warnThreshold,
        double                                  critThreshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load existing EWMA state (null = first run)
        var existing = await readCtx.Set<MLModelEwmaAccuracy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.MLModelId == modelId, ct);

        // Load resolved prediction logs since the last update (incremental)
        var since = existing?.LastPredictionAt ?? DateTime.MinValue;

        var newLogs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId  &&
                        l.DirectionCorrect != null      &&
                        l.PredictedAt      > since      &&
                        !l.IsDeleted)
            .OrderBy(l => l.PredictedAt)
            .AsNoTracking()
            .Select(l => new { l.PredictedAt, Correct = l.DirectionCorrect!.Value })
            .ToListAsync(ct);

        if (newLogs.Count == 0) return;

        // Run EWMA update incrementally over new logs
        double ewma  = existing?.EwmaAccuracy ?? 0.5; // start at 50% if no prior state
        int    total = existing?.TotalPredictions ?? 0;
        DateTime lastAt = existing?.LastPredictionAt ?? DateTime.MinValue;

        foreach (var log in newLogs)
        {
            double outcome = log.Correct ? 1.0 : 0.0;
            ewma = alpha * outcome + (1.0 - alpha) * ewma;
            total++;
            lastAt = log.PredictedAt;
        }

        var now = DateTime.UtcNow;

        // Upsert the EWMA row
        int rows = await writeCtx.Set<MLModelEwmaAccuracy>()
            .Where(r => r.MLModelId == modelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.EwmaAccuracy,     ewma)
                .SetProperty(r => r.Alpha,             alpha)
                .SetProperty(r => r.TotalPredictions,  total)
                .SetProperty(r => r.LastPredictionAt,  lastAt)
                .SetProperty(r => r.ComputedAt,        now),
                ct);

        if (rows == 0)
        {
            writeCtx.Set<MLModelEwmaAccuracy>().Add(new MLModelEwmaAccuracy
            {
                MLModelId        = modelId,
                Symbol           = symbol,
                Timeframe        = timeframe,
                EwmaAccuracy     = ewma,
                Alpha            = alpha,
                TotalPredictions = total,
                LastPredictionAt = lastAt,
                ComputedAt       = now,
            });
            await writeCtx.SaveChangesAsync(ct);
        }

        _logger.LogDebug(
            "EwmaAccuracy: model {Id} ({Symbol}/{Tf}) — ewma={Ewma:P2} n={N} (+{New} new)",
            modelId, symbol, timeframe, ewma, total, newLogs.Count);

        // ── Alerting ──────────────────────────────────────────────────────────
        if (total < minPredictions) return;
        if (ewma >= warnThreshold)  return;

        string severity = ewma < critThreshold ? "critical" : "warning";

        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        _logger.LogWarning(
            "EwmaAccuracy: model {Id} ({Symbol}/{Tf}) — EWMA={Ewma:P2} below {Severity} threshold. n={N}",
            modelId, symbol, timeframe, ewma, severity, total);

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason           = "ewma_accuracy_degraded",
                severity,
                symbol,
                timeframe        = timeframe.ToString(),
                modelId,
                ewmaAccuracy     = ewma,
                alpha,
                warnThreshold,
                criticalThreshold = critThreshold,
                totalPredictions  = total,
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
