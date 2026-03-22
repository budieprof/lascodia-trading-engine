using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes the realized <b>Expected Value (EV) per trade</b> for each active
/// ML model — the single most complete measure of live model utility.
///
/// <b>Formula:</b>
/// <c>EV = P_win × avg_win_pips − P_loss × avg_loss_pips</c>
///
/// where <c>P_win</c> and <c>P_loss</c> are empirical win/loss rates from
/// matched positions over the look-back window, and the pips figures are mean
/// absolute realized P&amp;L values.
///
/// <b>Why not rely on accuracy or reward-to-risk alone:</b>
/// <list type="bullet">
///   <item>Accuracy ignores position sizing asymmetry.</item>
///   <item>Reward-to-risk ignores the win rate.</item>
///   <item>EV combines both into one number that answers directly:
///         "on average, how many pips does this model make per trade?"</item>
/// </list>
///
/// Writes <c>MLEdge:{Symbol}:{Tf}:ExpectedValue</c> to <see cref="EngineConfig"/>
/// for observability.  Fires <see cref="AlertType.MLModelDegraded"/> with reason
/// <c>"ev_negative"</c> when EV &lt;= 0 and <c>"ev_warning"</c> when EV &lt;
/// <c>WarnEvPips</c>.
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLEdge:PollIntervalSeconds</c>       — default 3600 (1 h)</item>
///   <item><c>MLEdge:WindowDays</c>               — look-back, default 30</item>
///   <item><c>MLEdge:MinSamples</c>               — minimum matched positions, default 10</item>
///   <item><c>MLEdge:MatchWindowMinutes</c>        — temporal join tolerance, default 60</item>
///   <item><c>MLEdge:WarnEvPips</c>               — warning floor in pips, default 0.5</item>
///   <item><c>MLEdge:AlertDestination</c>          — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLCalibratedEdgeWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLEdge:PollIntervalSeconds";
    private const string CK_Window      = "MLEdge:WindowDays";
    private const string CK_MinSamples  = "MLEdge:MinSamples";
    private const string CK_MatchWindow = "MLEdge:MatchWindowMinutes";
    private const string CK_WarnEv      = "MLEdge:WarnEvPips";
    private const string CK_AlertDest   = "MLEdge:AlertDestination";

    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<MLCalibratedEdgeWorker>   _logger;

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public MLCalibratedEdgeWorker(
        IServiceScopeFactory               scopeFactory,
        ILogger<MLCalibratedEdgeWorker>    logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Runs hourly by default. Each iteration creates
    /// a fresh DI scope and delegates EV computation to <see cref="CheckAllModelsAsync"/>.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLCalibratedEdgeWorker started.");

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

                await CheckAllModelsAsync(ctx, wCtx, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MLCalibratedEdgeWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLCalibratedEdgeWorker stopping.");
    }

    // ── EV computation core ───────────────────────────────────────────────────

    /// <summary>
    /// Loads configuration and iterates all active models, calling
    /// <see cref="ComputeModelEvAsync"/> for each one. Per-model errors are isolated.
    /// </summary>
    private async Task CheckAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays  = await GetConfigAsync<int>   (readCtx, CK_Window,      30,      ct);
        int    minSamples  = await GetConfigAsync<int>   (readCtx, CK_MinSamples,  10,      ct);
        int    matchWindow = await GetConfigAsync<int>   (readCtx, CK_MatchWindow, 60,      ct);
        double warnEv      = await GetConfigAsync<double>(readCtx, CK_WarnEv,      0.5,     ct);
        string alertDest   = await GetConfigAsync<string>(readCtx, CK_AlertDest,   "ml-ops", ct);

        var cutoff = DateTime.UtcNow.AddDays(-windowDays);

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
                await ComputeModelEvAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    cutoff, minSamples, matchWindow, warnEv, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CalibratedEdge: compute failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Computes the calibrated Expected Value (EV) per trade for a single model
    /// using a temporal join between prediction logs and closed positions, then
    /// writes the result to <see cref="EngineConfig"/> for observability and fires
    /// an alert if EV falls below acceptable thresholds.
    /// </summary>
    /// <remarks>
    /// <b>Calibrated EV computation:</b>
    /// <code>
    ///   pWin   = winCount / matchedCount          (empirical win rate)
    ///   pLoss  = lossCount / matchedCount         (empirical loss rate = 1 - pWin)
    ///   avgWin = mean(winning position P&amp;L)   (in account currency units / pips)
    ///   avgLoss= |mean(losing position P&amp;L)|  (absolute value)
    ///   EV     = pWin × avgWin − pLoss × avgLoss
    /// </code>
    /// EV is the single most complete utility measure because it combines both the
    /// win rate (accuracy) and the profit-loss magnitude (reward-to-risk) into one
    /// number. A positive EV confirms the model is economically additive.
    ///
    /// <b>Alert thresholds:</b>
    /// <list type="bullet">
    ///   <item>EV &lt;= 0: "ev_negative" (critical) — the model is expected to lose money
    ///         per trade on average; it should be suppressed.</item>
    ///   <item>0 &lt; EV &lt; WarnEvPips: "ev_warning" — the model is marginally positive
    ///         but may be absorbing costs (spread, commission) and eroding net P&amp;L.</item>
    /// </list>
    ///
    /// <b>Observability:</b> The computed EV is written to
    /// <c>MLEdge:{symbol}:{timeframe}:ExpectedValue</c> in <see cref="EngineConfig"/>
    /// even when no alert fires, providing a live dashboard metric without needing
    /// a dedicated query endpoint.
    /// </remarks>
    private async Task ComputeModelEvAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                cutoff,
        int                                     minSamples,
        int                                     matchWindowMinutes,
        double                                  warnEvPips,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Fetch prediction timestamps for the temporal join.
        // Only resolved logs (DirectionCorrect != null) are included — unresolved logs
        // have no corresponding position outcome and would dilute the EV estimate.
        var logTimestamps = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId  &&
                        l.DirectionCorrect != null      &&
                        l.PredictedAt      >= cutoff    &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => l.PredictedAt)
            .ToListAsync(ct);

        if (logTimestamps.Count < minSamples) return;

        // Load closed positions for the symbol in the window.
        // Position.OpenedAt is used for the temporal join to match it with the signal time.
        var positions = await readCtx.Set<Position>()
            .Where(p => p.Symbol   == symbol                &&
                        p.Status   == PositionStatus.Closed &&
                        p.OpenedAt >= cutoff                &&
                        !p.IsDeleted)
            .AsNoTracking()
            .Select(p => new { p.OpenedAt, p.RealizedPnL })
            .ToListAsync(ct);

        if (positions.Count == 0) return;

        // ── Temporal join ─────────────────────────────────────────────────────
        // Same approach as MLRewardToRiskWorker: closest-match within the match window.
        var matchTol  = TimeSpan.FromMinutes(matchWindowMinutes);
        var winPnls   = new List<decimal>();
        var lossPnls  = new List<decimal>();

        foreach (var ts in logTimestamps)
        {
            var match = positions
                .Where(p => Math.Abs((p.OpenedAt - ts).TotalMinutes) <= matchTol.TotalMinutes)
                .OrderBy(p => Math.Abs((p.OpenedAt - ts).TotalMinutes))
                .FirstOrDefault();

            if (match is null) continue;

            if (match.RealizedPnL > 0m)
                winPnls.Add(match.RealizedPnL);
            else
                lossPnls.Add(match.RealizedPnL);
        }

        int matched = winPnls.Count + lossPnls.Count;
        if (matched < minSamples) return;

        // ── Calibrated EV formula ─────────────────────────────────────────────
        // Empirical probabilities from the matched sample
        double pWin  = (double)winPnls.Count  / matched;
        double pLoss = (double)lossPnls.Count / matched;

        // Average absolute P&L values; avgLoss is taken as absolute for the formula
        double avgWin  = winPnls.Count  > 0 ? (double)winPnls.Average()             :  0.0;
        double avgLoss = lossPnls.Count > 0 ? Math.Abs((double)lossPnls.Average())  :  0.0;

        // EV = pWin × avgWin − pLoss × avgLoss
        // Positive EV → model is economically beneficial
        // Zero or negative EV → model costs money even if directionally correct
        double ev = pWin * avgWin - pLoss * avgLoss;

        _logger.LogDebug(
            "CalibratedEdge: model {Id} ({Symbol}/{Tf}) — ev={EV:F3} pWin={PW:P1} " +
            "avgWin={AW:F2} avgLoss={AL:F2} matched={M}",
            modelId, symbol, timeframe, ev, pWin, avgWin, avgLoss, matched);

        // ── Persist EV as an observability config key ─────────────────────────
        // This allows dashboards or operators to query the current EV without
        // running a custom SQL query against the prediction log table.
        await UpsertConfigAsync(writeCtx,
            $"MLEdge:{symbol}:{timeframe}:ExpectedValue",
            ev.ToString("F4"), ct);

        // Determine alert condition: negative EV is critical; low-but-positive is warning
        bool evNegative = ev <= 0.0;
        bool evWarn     = ev < warnEvPips;

        // If EV is above the warning floor, no further action is needed
        if (!evWarn) return;

        string reason   = evNegative ? "ev_negative" : "ev_warning";
        string severity = evNegative ? "critical"    : "warning";

        _logger.LogWarning(
            "CalibratedEdge: model {Id} ({Symbol}/{Tf}) — {Reason}: EV={EV:F3} pips " +
            "pWin={PW:P1} avgWin={AW:F2} avgLoss={AL:F2}",
            modelId, symbol, timeframe, reason, ev, pWin, avgWin, avgLoss);

        // Deduplicate alert
        bool alertExists = await readCtx.Set<Alert>()
            .AnyAsync(a => a.Symbol    == symbol                  &&
                           a.AlertType == AlertType.MLModelDegraded &&
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
                reason,
                severity,
                symbol,
                timeframe         = timeframe.ToString(),
                modelId,
                expectedValuePips = ev,
                winRate           = pWin,
                avgWinPips        = avgWin,
                avgLossPips       = avgLoss,
                warnEvPips,
                matchedPositions  = matched,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
    }

    // ── EngineConfig upsert ───────────────────────────────────────────────────

    /// <summary>
    /// Inserts or updates a <see cref="EngineConfig"/> row for the given key.
    /// Used both for the EV observability key and any alert-related config entries.
    /// </summary>
    /// <param name="writeCtx">Write DbContext.</param>
    /// <param name="key">Config key (e.g. <c>MLEdge:EURUSD:H1:ExpectedValue</c>).</param>
    /// <param name="value">String value to persist (four-decimal EV).</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value,         value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow),
                ct);

        if (rows == 0)
        {
            // First write for this symbol/timeframe pair — create the row with
            // descriptive metadata so operators can identify its purpose.
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = ConfigDataType.String,
                Description     = "Expected value per trade (pips). Written by MLCalibratedEdgeWorker.",
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
            await writeCtx.SaveChangesAsync(ct);
        }
    }

    // ── Config helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/>. Returns
    /// <paramref name="defaultValue"/> if the key is absent or unparseable.
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
