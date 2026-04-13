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
/// <para>
/// <b>Purpose:</b> Directional accuracy is a proxy metric — a model can maintain 55%
/// accuracy while systematically losing money if it bets large on wrong predictions and
/// small on correct ones (magnitude miscalibration), or if the wrong predictions coincide
/// with large adverse moves. This worker tracks the actual economic outcome by computing
/// a confidence-weighted, signed return series from resolved prediction logs, then
/// deriving a rolling annualized Sharpe ratio. A Sharpe ratio below the floor triggers
/// an alert and queues a retrain.
/// </para>
///
/// <para>
/// <b>PnL Attribution method:</b>
/// <code>
///   return_i = sign_i × |ActualMagnitudePips_i| × ConfidenceScore_i
///   sign_i   = +1 if DirectionCorrect, −1 otherwise
/// </code>
/// ConfidenceScore acts as a fractional position-size proxy: a high-confidence correct
/// prediction earns more than a low-confidence one; a high-confidence wrong prediction
/// loses proportionally more. This penalizes models that are overconfident on losing trades.
/// </para>
///
/// <para>
/// <b>Polling interval:</b> Configurable via <c>MLPnlMonitor:PollIntervalSeconds</c>;
/// defaults to 10800 seconds (3 hours). The 3-hour cadence reflects that enough new
/// resolved outcomes need to accumulate between checks for the Sharpe estimate to move.
/// </para>
///
/// <para>
/// <b>ML lifecycle contribution:</b> Catches magnitude-calibration drift and P&amp;L
/// degradation that accuracy metrics alone miss. When the Sharpe floor is breached,
/// an <see cref="AlertType.MLModelDegraded"/> alert is raised (deduplicated) and a
/// retrain is queued with diagnostic metadata so the training pipeline can focus on
/// magnitude calibration (e.g. by re-weighting examples or switching to a regression
/// head).
/// </para>
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

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public MLPredictionPnlWorker(
        IServiceScopeFactory           scopeFactory,
        ILogger<MLPredictionPnlWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. On each iteration, creates a fresh DI scope,
    /// reads the poll interval, and delegates P&amp;L monitoring to
    /// <see cref="MonitorPnlAsync"/>. Errors are caught and logged to maintain
    /// loop continuity across transient failures.
    /// </summary>
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

    /// <summary>
    /// Loads config and iterates all active models, delegating per-model P&amp;L
    /// analysis to <see cref="CheckModelPnlAsync"/>. Per-model failures are isolated
    /// so one bad model does not block the rest.
    /// </summary>
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

    /// <summary>
    /// Computes the confidence-weighted, rolling Sharpe ratio for a single model from
    /// resolved prediction logs within the look-back window, and triggers alerts/retrains
    /// when Sharpe falls below the configured floor.
    /// </summary>
    /// <param name="model">The ML model to evaluate.</param>
    /// <param name="readCtx">Read-only EF DbContext.</param>
    /// <param name="writeCtx">Write EF DbContext.</param>
    /// <param name="windowDays">Number of calendar days to look back for resolved predictions.</param>
    /// <param name="minResolved">
    /// Minimum number of resolved logs (with both DirectionCorrect and ActualMagnitudePips
    /// populated) required to compute a meaningful Sharpe ratio.
    /// </param>
    /// <param name="minSharpe">
    /// Sharpe ratio floor. Values below this trigger a degradation alert and retrain.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
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

        // Only fetch logs that have been resolved with both directional outcome
        // and actual magnitude — unresolved logs lack the data needed for P&L simulation.
        var resolved = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId          == model.Id &&
                        l.PredictedAt        >= since    &&
                        l.DirectionCorrect   != null     &&
                        l.ActualMagnitudePips != null    &&
                        !l.IsDeleted)
            .OrderBy(l => l.PredictedAt)
            .Select(l => new
            {
                DirectionCorrect    = l.DirectionCorrect!.Value,
                ActualMagnitudePips = (double)l.ActualMagnitudePips!.Value,
                ConfidenceScore     = (double)l.ConfidenceScore,
            })
            .ToListAsync(ct);

        if (resolved.Count < minResolved)
        {
            _logger.LogDebug(
                "PnlMonitor: {Symbol}/{Tf} model {Id} only {N} resolved with magnitudes (need {Min}) — skip.",
                model.Symbol, model.Timeframe, model.Id, resolved.Count, minResolved);
            return;
        }

        // ── PnL attribution: confidence-weighted signed return series ─────────
        // Each prediction generates a simulated return:
        //   return_i = sign_i × |actualMagnitudePips_i| × clamp(confidence_i, 0.01, 1.0)
        //
        // The confidence weight acts as a fractional bet size:
        //   - A confident correct prediction earns proportionally more.
        //   - A confident wrong prediction loses proportionally more.
        // This penalises magnitude miscalibration: high confidence on losses is more
        // damaging than low confidence on losses.
        var returns = resolved
            .Select(r =>
            {
                double sign   = r.DirectionCorrect ? 1.0 : -1.0;
                // Floor confidence at 0.01 to avoid near-zero position sizing
                double sizing = Math.Clamp(r.ConfidenceScore, 0.01, 1.0);
                return sign * Math.Abs(r.ActualMagnitudePips) * sizing;
            })
            .ToList();

        // ── Annualized Sharpe ratio ───────────────────────────────────────────
        // Sharpe = mean(returns) / std(returns) × sqrt(252)
        // The √252 annualization assumes daily bars; the metric degrades gracefully
        // for intraday models but remains comparable across timeframes.
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

        // If Sharpe is at or above the floor, the model's P&L quality is acceptable.
        if (annualisedSharpe >= minSharpe) return;

        // Sharpe below floor — the model is destroying value on a magnitude-adjusted basis.
        _logger.LogWarning(
            "P&L degradation: {Symbol}/{Tf} model {Id}: rolling Sharpe={Sharpe:F2} " +
            "< floor={Floor:F2} — model may be profitable on direction but losing on magnitude.",
            model.Symbol, model.Timeframe, model.Id, annualisedSharpe, minSharpe);

        // ── Deduplicated alert ────────────────────────────────────────────────
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

        // ── Idempotent retrain queue ──────────────────────────────────────────
        // Queue a retrain with the Sharpe context so the MLTrainingWorker can
        // apply magnitude-aware training adjustments if supported.
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
                FromDate  = DateTime.UtcNow.AddDays(-365),
                ToDate    = DateTime.UtcNow,
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

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/>. Returns
    /// <paramref name="defaultValue"/> if the key is missing or the stored value
    /// cannot be converted to <typeparamref name="T"/>.
    /// </summary>
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
