using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes the realized reward-to-risk (RR) ratio for each active ML model
/// from matched closed positions over a rolling look-back window.
///
/// <b>Why RR matters independently of accuracy:</b>
/// <list type="bullet">
///   <item>A model with 60 % accuracy but RR = 0.4 (wins average 4 pips, losses 10 pips)
///         produces expected return of 0.6 × 4 − 0.4 × 10 = −1.6 pips per trade
///         — a systematically losing strategy despite above-random accuracy.</item>
///   <item><see cref="MLExcessReturnWorker"/> detects <i>correct-but-unprofitable</i>
///         signals; this worker measures the <i>size asymmetry</i> between wins and
///         losses, which is distinct — a trade can be directionally correct, modestly
///         profitable, but still too small relative to losses.</item>
/// </list>
///
/// <b>Algorithm:</b>
/// <list type="number">
///   <item>Temporally join resolved prediction logs to closed positions (same symbol,
///         position opened within <c>MatchWindowMinutes</c> of the signal).</item>
///   <item>Separate matched positions into wins (<c>RealizedPnL &gt; 0</c>) and
///         losses (<c>RealizedPnL &lt;= 0</c>).</item>
///   <item>Compute <c>RR = mean_win_pnl / |mean_loss_pnl|</c>. If there are no
///         losses in the window, RR is reported as ∞ (not an alert condition).</item>
///   <item>Alert with reason <c>"reward_to_risk_critical"</c> when RR &lt;
///         <c>CriticalRRThreshold</c> (default 0.5), and <c>"reward_to_risk_warning"</c>
///         when RR &lt; <c>WarnRRThreshold</c> (default 0.8).</item>
/// </list>
///
/// Configuration keys (read from <see cref="EngineConfig"/>):
/// <list type="bullet">
///   <item><c>MLRR:PollIntervalSeconds</c>             — default 3600 (1 h)</item>
///   <item><c>MLRR:WindowDays</c>                      — look-back, default 30</item>
///   <item><c>MLRR:MinSamples</c>                      — minimum matched positions, default 10</item>
///   <item><c>MLRR:MatchWindowMinutes</c>              — temporal join tolerance, default 60</item>
///   <item><c>MLRR:WarnRRThreshold</c>                 — warning floor, default 0.8</item>
///   <item><c>MLRR:CriticalRRThreshold</c>             — critical floor, default 0.5</item>
///   <item><c>MLRR:AlertDestination</c>                — default "ml-ops"</item>
/// </list>
/// </summary>
public sealed class MLRewardToRiskWorker : BackgroundService
{
    private const string CK_PollSecs    = "MLRR:PollIntervalSeconds";
    private const string CK_Window      = "MLRR:WindowDays";
    private const string CK_MinSamples  = "MLRR:MinSamples";
    private const string CK_MatchWindow = "MLRR:MatchWindowMinutes";
    private const string CK_WarnRR      = "MLRR:WarnRRThreshold";
    private const string CK_CritRR      = "MLRR:CriticalRRThreshold";
    private const string CK_AlertDest   = "MLRR:AlertDestination";

    private readonly IServiceScopeFactory            _scopeFactory;
    private readonly ILogger<MLRewardToRiskWorker>   _logger;

    /// <summary>
    /// Initializes the worker.
    /// </summary>
    /// <param name="scopeFactory">Per-iteration DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public MLRewardToRiskWorker(
        IServiceScopeFactory             scopeFactory,
        ILogger<MLRewardToRiskWorker>    logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Background service main loop. Runs hourly by default, creating a fresh scope
    /// per iteration and delegating to <see cref="CheckAllModelsAsync"/>.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLRewardToRiskWorker started.");

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
                _logger.LogError(ex, "MLRewardToRiskWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSecs), stoppingToken);
        }

        _logger.LogInformation("MLRewardToRiskWorker stopping.");
    }

    // ── Detection core ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads configuration and iterates all active models, delegating the per-model
    /// RR calculation to <see cref="CheckModelAsync"/>. Per-model errors are isolated
    /// so a single DB error does not abort the entire iteration.
    /// </summary>
    private async Task CheckAllModelsAsync(
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        int    windowDays    = await GetConfigAsync<int>   (readCtx, CK_Window,      30,      ct);
        int    minSamples    = await GetConfigAsync<int>   (readCtx, CK_MinSamples,  10,      ct);
        int    matchWindow   = await GetConfigAsync<int>   (readCtx, CK_MatchWindow, 60,      ct);
        double warnRR        = await GetConfigAsync<double>(readCtx, CK_WarnRR,      0.8,     ct);
        double critRR        = await GetConfigAsync<double>(readCtx, CK_CritRR,      0.5,     ct);
        string alertDest     = await GetConfigAsync<string>(readCtx, CK_AlertDest,   "ml-ops", ct);

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
                await CheckModelAsync(
                    model.Id, model.Symbol, model.Timeframe,
                    cutoff, minSamples, matchWindow, warnRR, critRR, alertDest,
                    readCtx, writeCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RewardToRisk: check failed for model {Id} ({Symbol}/{Tf}) — skipping.",
                    model.Id, model.Symbol, model.Timeframe);
            }
        }
    }

    /// <summary>
    /// Computes the realized reward-to-risk ratio for a single model by performing a
    /// temporal join between prediction log timestamps and closed positions, then
    /// separating matched positions into wins and losses to compute mean values.
    /// </summary>
    /// <remarks>
    /// <b>Temporal join rationale:</b> Prediction logs record when a signal was scored
    /// (<c>PredictedAt</c>); positions record when a trade was opened (<c>OpenedAt</c>).
    /// A signal scored at T should correspond to a position opened within approximately
    /// <paramref name="matchWindowMinutes"/> minutes of T, accounting for broker latency
    /// and order queue delay. The closest-match approach (sort by absolute time difference,
    /// take first) avoids double-counting while tolerating variable execution delays.
    ///
    /// <b>RR calculation:</b>
    /// <code>
    ///   RR = mean(winning position PnL) / |mean(losing position PnL)|
    /// </code>
    /// Values above 1.0 mean winners are larger than losers on average.
    /// Values below the warning threshold (default 0.8) indicate the model's losses
    /// are disproportionately large relative to wins. Values below the critical threshold
    /// (default 0.5) indicate a materially loss-making size asymmetry.
    /// When there are no matched losses in the window, RR is reported as +∞ and no alert fires.
    /// </remarks>
    private async Task CheckModelAsync(
        long                                    modelId,
        string                                  symbol,
        Timeframe                               timeframe,
        DateTime                                cutoff,
        int                                     minSamples,
        int                                     matchWindowMinutes,
        double                                  warnRRThreshold,
        double                                  critRRThreshold,
        string                                  alertDest,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        CancellationToken                       ct)
    {
        // Load only the prediction timestamps for the temporal join — the full log
        // row is not needed since position P&L comes from the Position table directly.
        var logs = await readCtx.Set<MLModelPredictionLog>()
            .Where(l => l.MLModelId        == modelId  &&
                        l.DirectionCorrect != null      &&
                        l.PredictedAt      >= cutoff    &&
                        !l.IsDeleted)
            .AsNoTracking()
            .Select(l => l.PredictedAt)
            .ToListAsync(ct);

        if (logs.Count < minSamples) return;

        // Load all closed positions for the symbol in the window.
        // Closed positions have a known RealizedPnL which is used to determine win/loss.
        var positions = await readCtx.Set<Position>()
            .Where(p => p.Symbol    == symbol                &&
                        p.Status    == PositionStatus.Closed &&
                        p.OpenedAt  >= cutoff                &&
                        !p.IsDeleted)
            .AsNoTracking()
            .Select(p => new { p.OpenedAt, p.RealizedPnL })
            .ToListAsync(ct);

        if (positions.Count == 0) return;

        // ── Temporal join ─────────────────────────────────────────────────────
        // For each resolved prediction timestamp, find the closest position opened
        // within the match window. Positions not matched to any prediction are excluded.
        var matchTol = TimeSpan.FromMinutes(matchWindowMinutes);
        var winPnls  = new List<decimal>();
        var lossPnls = new List<decimal>();

        foreach (var logTs in logs)
        {
            var match = positions
                .Where(p => Math.Abs((p.OpenedAt - logTs).TotalMinutes) <= matchTol.TotalMinutes)
                .OrderBy(p => Math.Abs((p.OpenedAt - logTs).TotalMinutes))
                .FirstOrDefault();

            if (match is null) continue;

            // Separate into wins (positive P&L) and losses (zero or negative P&L).
            // Note: breakeven trades (PnL == 0) are categorised as losses to be conservative.
            if (match.RealizedPnL > 0m)
                winPnls.Add(match.RealizedPnL);
            else
                lossPnls.Add(match.RealizedPnL);
        }

        int matchedCount = winPnls.Count + lossPnls.Count;
        if (matchedCount < minSamples) return;

        double meanWin  = winPnls.Count  > 0 ? (double)winPnls.Average()  :  0.0;
        double meanLoss = lossPnls.Count > 0 ? (double)lossPnls.Average() :  0.0; // negative value

        // ── Reward-to-Risk ratio ──────────────────────────────────────────────
        // RR = mean_win / |mean_loss|
        // Special case: if there are no losses, RR is +Infinity — this is a healthy
        // condition (all trades profitable) and should not trigger an alert.
        double rr = lossPnls.Count > 0 && Math.Abs(meanLoss) > 1e-9
            ? meanWin / Math.Abs(meanLoss)
            : double.PositiveInfinity;

        _logger.LogDebug(
            "RewardToRisk: model {Id} ({Symbol}/{Tf}) — matched={M} wins={W} losses={L} " +
            "avgWin={AW:F2} avgLoss={AL:F2} RR={RR:F2}",
            modelId, symbol, timeframe, matchedCount,
            winPnls.Count, lossPnls.Count, meanWin, meanLoss, rr);

        // No losses in the window — no adverse RR condition to report
        if (double.IsPositiveInfinity(rr)) return;

        // ── Alert severity determination ──────────────────────────────────────
        // Two levels: warning (RR < warnRRThreshold) and critical (RR < critRRThreshold).
        // Critical takes precedence and uses a distinct alert reason for triage.
        bool critAlert = rr < critRRThreshold;
        bool warnAlert = rr < warnRRThreshold;

        if (!warnAlert) return;

        string severity = critAlert ? "critical" : "warning";
        string reason   = critAlert ? "reward_to_risk_critical" : "reward_to_risk_warning";

        _logger.LogWarning(
            "RewardToRisk: model {Id} ({Symbol}/{Tf}) — RR={RR:F2} ({Severity}) " +
            "avgWin={AW:F2} avgLoss={AL:F2} matched={M}",
            modelId, symbol, timeframe, rr, severity, meanWin, meanLoss, matchedCount);

        // Deduplicate: only one active MLModelDegraded alert per symbol at a time
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
                timeframe             = timeframe.ToString(),
                modelId,
                rewardToRisk          = rr,
                warnRRThreshold,
                critRRThreshold,
                meanWinPnl            = meanWin,
                meanLossPnl           = meanLoss,
                winCount              = winPnls.Count,
                lossCount             = lossPnls.Count,
                matchedPositions      = matchedCount,
            }),
            IsActive = true,
        });

        await writeCtx.SaveChangesAsync(ct);
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
