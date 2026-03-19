using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors the realized P&amp;L quality of each active ML model by simulating
/// trade returns from resolved <see cref="MLModelPredictionLog"/> records.
///
/// Directional accuracy is a proxy metric — a model can maintain 55% accuracy while
/// systematically losing money if it bets large on wrong predictions and small on
/// correct ones (magnitude miscalibration), or if the wrong predictions happen to
/// coincide with large adverse moves. This worker tracks the actual economic outcome
/// using:
///
///   return_i = DirectionCorrect_i ? +|ActualMagnitudePips| : −|ActualMagnitudePips|
///   scaled by ConfidenceScore_i as a position-sizing proxy
///
/// From the return series it computes a rolling Sharpe ratio. When Sharpe drops below
/// <c>MLPnlMonitor:MinRollingSharpeCeiling</c>, an <see cref="AlertType.MLModelDegraded"/>
/// alert is created and a retrain is queued.
///
/// This catches magnitude-calibration drift and P&amp;L degradation that accuracy
/// metrics alone miss.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLPnlMonitor:PollIntervalSeconds</c>       — default 10800 (3 h)</item>
///   <item><c>MLPnlMonitor:WindowDays</c>                — look-back window, default 30</item>
///   <item><c>MLPnlMonitor:MinResolved</c>               — skip if fewer records, default 30</item>
///   <item><c>MLPnlMonitor:MinRollingSharpeCeiling</c>   — alert/retrain trigger, default −1.0</item>
/// </list>
/// </summary>
public sealed class MLPredictionPnlWorker : BackgroundService
{
    private const string CK_PollSecs     = "MLPnlMonitor:PollIntervalSeconds";
    private const string CK_WindowDays   = "MLPnlMonitor:WindowDays";
    private const string CK_MinResolved  = "MLPnlMonitor:MinResolved";
    private const string CK_MinSharpe    = "MLPnlMonitor:MinRollingSharpeCeiling";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<MLPredictionPnlWorker>  _logger;

    public MLPredictionPnlWorker(
        IServiceScopeFactory           scopeFactory,
        ILogger<MLPredictionPnlWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLPredictionPnlWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollSecs = 10800;

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var readDb  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var ctx     = readDb.GetDbContext();
                var wCtx    = writeDb.GetDbContext();

                pollSecs = await GetConfigAsync<int>(ctx, CK_PollSecs, 10800, stoppingToken);

                await MonitorPnlAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLPredictionPnlWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLPredictionPnlWorker stopping.");
    }

    private async Task MonitorPnlAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays  = await GetConfigAsync<int>   (readCtx, CK_WindowDays,  30,   ct);
        int    minResolved = await GetConfigAsync<int>   (readCtx, CK_MinResolved, 30,   ct);
        double minSharpe   = await GetConfigAsync<double>(readCtx, CK_MinSharpe,  -1.0, ct);

        var activeModels = await readCtx.Set<MLModel>()
            .Where(m => m.IsActive && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await CheckModelPnlAsync(model, readCtx, writeCtx,
                    windowDays, minResolved, minSharpe, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "P&L monitor failed for {Symbol}/{Tf} model {Id} — skipping.",
                    model.Symbol, model.Timeframe, model.Id);
            }
        }
    }

    private async Task CheckModelPnlAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                                     windowDays,
        int                                     minResolved,
        double                                  minSharpe,
        CancellationToken                       ct)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);

        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId          == model.Id &&
                        l.PredictedAt        >= since    &&
                        l.DirectionCorrect   != null     &&
                        l.ActualMagnitudePips != null    &&
                        !l.IsDeleted)
            .OrderBy(l => l.PredictedAt)
            .Select(l => new
            {
                DirectionCorrect   = l.DirectionCorrect!.Value,
                ActualMagnitudePips = (double)l.ActualMagnitudePips!.Value,
                ConfidenceScore    = (double)l.ConfidenceScore,
            })
            .ToListAsync(ct);

        if (resolved.Count < minResolved)
        {
            _logger.LogDebug(
                "PnlMonitor: {Symbol}/{Tf} model {Id} only {N} resolved with magnitudes (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minResolved);
            return;
        }

        // Simulate returns: confidence-scaled signed magnitude
        // return_i = sign × |actualPips| × confidence
        var returns = resolved
            .Select(r =>
            {
                double sign   = r.DirectionCorrect ? 1.0 : -1.0;
                double sizing = Math.Clamp(r.ConfidenceScore, 0.01, 1.0);
                return sign * Math.Abs(r.ActualMagnitudePips) * sizing;
            })
            .ToList();

        double meanReturn = returns.Average();
        double stdReturn  = returns.Count > 1
            ? Math.Sqrt(returns.Select(r => (r - meanReturn) * (r - meanReturn)).Average())
            : 0.0;

        double annualisedSharpe = stdReturn > 1e-10
            ? meanReturn / stdReturn * Math.Sqrt(252.0)  // annualised assuming daily bars
            : (meanReturn > 0 ? double.MaxValue : double.MinValue);

        _logger.LogDebug(
            "PnlMonitor: {Symbol}/{Tf} model {Id}: rollingSharpee={Sharpe:F2} " +
            "(mean={Mean:F3}, std={Std:F3}, n={N})",
            model.Symbol, model.Timeframe, model.Id,
            annualisedSharpe, meanReturn, stdReturn, resolved.Count);

        if (annualisedSharpe >= minSharpe) return;

        _logger.LogWarning(
            "P&L degradation: {Symbol}/{Tf} model {Id}: rolling Sharpe={Sharpe:F2} " +
            "< floor={Floor:F2} — model may be profitable on direction but losing on magnitude.",
            model.Symbol, model.Timeframe, model.Id, annualisedSharpe, minSharpe);

        // Deduplicate alert
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
                    reason           = "pnl_sharpe_degradation",
                    rollingSharpee   = annualisedSharpe,
                    minSharpeFloor   = minSharpe,
                    meanReturnPips   = meanReturn,
                    stdReturnPips    = stdReturn,
                    resolvedCount    = resolved.Count,
                    windowDays,
                    symbol           = model.Symbol,
                    timeframe        = model.Timeframe.ToString(),
                    modelId          = model.Id,
                }),
                IsActive = true,
            });
        }

        // Queue retrain if none already pending
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
                    triggeredBy      = "MLPredictionPnlWorker",
                    rollingSharpee   = annualisedSharpe,
                    minSharpeFloor   = minSharpe,
                    modelId          = model.Id,
                }),
            });
        }

        await writeCtx.SaveChangesAsync(ct);
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
