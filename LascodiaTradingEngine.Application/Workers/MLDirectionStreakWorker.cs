using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects models that are stuck predicting the same direction for an extended period,
/// which is a signal that the model has degenerated into a directional bias rather than
/// responding dynamically to market conditions.
///
/// <b>Problem:</b> A model can achieve nominal accuracy by always predicting Buy in a
/// sustained uptrend. When the trend reverses the model continues to predict Buy, causing
/// systematic losses. Simple rolling accuracy metrics won't catch this early because
/// accuracy only degrades gradually after the trend turns.
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>For each active model, load the last <c>WindowSize</c> resolved
///         <see cref="MLModelPredictionLog"/> records ordered by <c>PredictedAt</c>.</item>
///   <item>Compute the fraction of predictions in the most common direction.</item>
///   <item>If the fraction exceeds <c>MaxSameDirectionFraction</c>, fire an
///         <see cref="AlertType.MLModelDegraded"/> alert.</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLStreak:PollIntervalSeconds</c>        — default 3600 (1 h)</item>
///   <item><c>MLStreak:WindowSize</c>                 — number of recent predictions, default 30</item>
///   <item><c>MLStreak:MaxSameDirectionFraction</c>   — max tolerated fraction, default 0.85</item>
///   <item><c>MLStreak:AlertDestination</c>           — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLDirectionStreakWorker : BackgroundService
{
    private const string CK_PollSecs   = "MLStreak:PollIntervalSeconds";
    private const string CK_Window     = "MLStreak:WindowSize";
    private const string CK_MaxFrac    = "MLStreak:MaxSameDirectionFraction";
    private const string CK_AlertDest  = "MLStreak:AlertDestination";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLDirectionStreakWorker>  _logger;

    public MLDirectionStreakWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLDirectionStreakWorker>   logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLDirectionStreakWorker started.");

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

                await CheckStreaksAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLDirectionStreakWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLDirectionStreakWorker stopping.");
    }

    // ── Streak detection core ─────────────────────────────────────────────────

    private async Task CheckStreaksAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowSize   = await GetConfigAsync<int>   (readCtx, CK_Window,    30,      ct);
        double maxFraction  = await GetConfigAsync<double>(readCtx, CK_MaxFrac,   0.85,    ct);
        string alertDest    = await GetConfigAsync<string>(readCtx, CK_AlertDest, "ml-ops", ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .Select(m => new { m.Id, m.Symbol, m.Timeframe })
            .ToListAsync(ct);

        if (activeModels.Count == 0) return;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelStreakAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    windowSize, maxFraction, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DirectionStreak: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    private async Task CheckModelStreakAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        int                                     windowSize,
        double                                  maxFraction,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load the N most recent predictions (resolved or unresolved — we only care about direction)
        var recent = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId == modelId && !l.IsDeleted)
            .OrderByDescending(l => l.PredictedAt)
            .Take(windowSize)
            .AsNoTracking()
            .Select(l => l.PredictedDirection)
            .ToListAsync(ct);

        if (recent.Count < windowSize)
        {
            _logger.LogDebug(
                "DirectionStreak: model {Id} ({Symbol}/{Tf}) — only {N}/{Window} predictions available, skipping.",
                modelId, symbol, timeframe, recent.Count, windowSize);
            return;
        }

        int    buyCount       = recent.Count(d => d == TradeDirection.Buy);
        int    sellCount      = recent.Count - buyCount;
        int    dominantCount  = Math.Max(buyCount, sellCount);
        double dominantFrac   = (double)dominantCount / recent.Count;
        var    dominantDir    = buyCount >= sellCount ? TradeDirection.Buy : TradeDirection.Sell;

        _logger.LogDebug(
            "DirectionStreak: model {Id} ({Symbol}/{Tf}) — last {N}: Buy={B} Sell={S} dominant={Dir} ({Frac:P1})",
            modelId, symbol, timeframe, recent.Count, buyCount, sellCount, dominantDir, dominantFrac);

        if (dominantFrac <= maxFraction) return;

        _logger.LogWarning(
            "DirectionStreak: model {Id} ({Symbol}/{Tf}) — {Frac:P1} of last {N} predictions are {Dir} " +
            "(threshold {Thr:P0}). Possible directional bias.",
            modelId, symbol, timeframe, dominantFrac, recent.Count, dominantDir, maxFraction);

        // Deduplicate: skip if an active streak alert already exists for this model
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                     &&
                           a.AlertType == AlertType.MLModelDegraded  &&
                           a.IsActive  && !a.IsDeleted, ct);

        if (alertExists) return;

        writeCtx.Set<Alert>().Add(new Alert
        {
            AlertType     = AlertType.MLModelDegraded,
            Symbol        = symbol,
            Channel       = AlertChannel.Webhook,
            Destination   = alertDest,
            ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason           = "direction_streak",
                severity         = "warning",
                symbol,
                timeframe        = timeframe.ToString(),
                modelId,
                dominantDirection = dominantDir.ToString(),
                dominantFraction  = dominantFrac,
                windowSize,
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
