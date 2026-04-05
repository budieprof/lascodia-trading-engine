using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes the Kelly Criterion optimal position-sizing fraction (f*) for each active
/// ML model from recent resolved live prediction logs. Suppresses models with negative
/// expected value (f* &lt; 0) and caps the position fraction at 25%. Runs every 24 hours.
/// </summary>
/// <remarks>
/// <b>Kelly Criterion background:</b>
/// The Kelly Criterion (Kelly 1956) is a position-sizing formula that maximises the
/// expected logarithm of wealth, equivalent to maximising the long-run geometric growth
/// rate. For a binary win/loss game:
///   f* = (p × b − q) / b
/// where:
/// <list type="bullet">
///   <item>p = win probability (fraction of trades that are profitable)</item>
///   <item>q = 1 − p = loss probability</item>
///   <item>b = mean_win / mean_loss (win-to-loss magnitude ratio)</item>
/// </list>
///
/// <b>Half-Kelly:</b> Full Kelly produces high volatility because estimation error in
/// p and b causes the actual fraction to overshoot. The worker stores
/// <c>HalfKelly = 0.5 × f*</c> as the recommended position fraction, which halves the
/// theoretical variance while retaining ~75% of expected geometric growth
/// (MacLean et al., 2010).
///
/// <b>Negative EV suppression:</b> When f* &lt; 0 the model has negative expected
/// geometric growth rate. The model is flagged <c>IsSuppressed = true</c> so the signal
/// pipeline ignores its outputs until the next successful retrain.
///
/// <b>Polling interval:</b> 24 hours. Daily computation uses a 60-day live-outcome window
/// for statistical stability while reflecting recent market conditions.
///
/// <b>ML lifecycle contribution:</b> Provides a final risk-adjusted position sizing
/// gate before signals reach the order execution layer by writing a live Kelly cap
/// and suppressing clearly negative-EV models.
/// </remarks>
public sealed class MLKellyFractionWorker : BackgroundService
{
    private const string KellyConfigKeyPrefix = "MLKelly:";
    private const string KellyCapKeySuffix = ":KellyCap";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLKellyFractionWorker> _logger;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per daily computation cycle so scoped EF Core
    /// contexts are correctly disposed after each pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLKellyFractionWorker(IServiceScopeFactory scopeFactory, ILogger<MLKellyFractionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Hosted-service entry point. Executes immediately on startup then re-runs every
    /// 24 hours to recompute Kelly fractions for all active models.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MLKellyFractionWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLKellyFractionWorker error"); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    /// <summary>
    /// Core Kelly computation cycle. Reconstructs realised signed returns from recent
    /// resolved production predictions, then computes and persists the Kelly fraction
    /// and Half-Kelly to <c>MLKellyFractionLog</c>. Models with negative expected
    /// value are suppressed.
    /// </summary>
    /// <remarks>
    /// Live-outcome methodology:
    /// <list type="number">
    ///   <item>
    ///     Load recent resolved <see cref="MLModelPredictionLog"/> rows for the last 60 days.
    ///     Require both <c>DirectionCorrect</c> and <c>ActualMagnitudePips</c>.
    ///   </item>
    ///   <item>
    ///     Convert each resolved prediction into a realised signed return proxy:
    ///     <c>+|ActualMagnitudePips|</c> when the direction was correct,
    ///     <c>-|ActualMagnitudePips|</c> when it was wrong.
    ///   </item>
    ///   <item>
    ///     Compute p, q, b, f* and Half-Kelly. Cap at ±25%.
    ///   </item>
    ///   <item>
    ///     Suppress the model if f* &lt; 0. Persist results to <c>MLKellyFractionLog</c>.
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="ct">Cancellation token forwarded from the host.</param>
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();

        // Exclude meta-learners and MAML initialisers — they do not emit direct live trade signals.
        var models = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer
                     && m.ModelBytes != null)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            string configKey = $"{KellyConfigKeyPrefix}{model.Symbol}:{model.Timeframe}:{model.Id}{KellyCapKeySuffix}";

            // ── Load resolved prediction logs for the last 60 days ───────────
            var logs = await readDb.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(l => l.MLModelId == model.Id
                         && !l.IsDeleted
                         && l.DirectionCorrect != null
                         && l.OutcomeRecordedAt != null
                         && l.OutcomeRecordedAt >= DateTime.UtcNow.AddDays(-60))
                .OrderByDescending(l => l.OutcomeRecordedAt)
                .ToListAsync(ct);

            if (logs.Count < 30)
            {
                await PersistNeutralKellyStateAsync(writeDb, model, configKey, ct);
                continue;
            }

            // ── Join predictions to actual position P&L via TradeSignal → Order → Position ──
            var signalIds = logs
                .Select(l => l.TradeSignalId)
                .Distinct()
                .ToList();

            // Map: TradeSignalId → list of OrderIds
            var signalOrderMap = await readDb.Set<Order>()
                .AsNoTracking()
                .Where(o => o.TradeSignalId != null && signalIds.Contains(o.TradeSignalId!.Value) && !o.IsDeleted)
                .Select(o => new { o.TradeSignalId, o.Id })
                .ToListAsync(ct);

            var signalToOrderIds = signalOrderMap
                .GroupBy(x => x.TradeSignalId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToHashSet());

            // Map: OrderId → closed Position P&L (net of swap + commission)
            var allOrderIds = signalToOrderIds.Values.SelectMany(x => x).Distinct().ToList();

            var orderPositionPnl = await readDb.Set<Position>()
                .AsNoTracking()
                .Where(p => p.Status == PositionStatus.Closed
                         && p.OpenOrderId != null
                         && allOrderIds.Contains(p.OpenOrderId!.Value)
                         && !p.IsDeleted)
                .Select(p => new { p.OpenOrderId, NetPnl = p.RealizedPnL + p.Swap - p.Commission })
                .ToListAsync(ct);

            var orderToPnl = orderPositionPnl
                .Where(x => x.OpenOrderId.HasValue)
                .GroupBy(x => x.OpenOrderId!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.NetPnl));

            // ── Compute Kelly from actual P&L where available, fall back to magnitude ──
            var wins   = new List<double>();
            var losses = new List<double>();
            int pnlBasedCount = 0;

            foreach (var log in logs)
            {
                double returnValue;

                // Prefer actual position-level P&L (net of costs) when available
                if (signalToOrderIds.TryGetValue(log.TradeSignalId, out var orderIds))
                {
                    decimal totalPnl = 0m;
                    bool hasPnl = false;
                    foreach (var oid in orderIds)
                    {
                        if (orderToPnl.TryGetValue(oid, out var pnl))
                        {
                            totalPnl += pnl;
                            hasPnl = true;
                        }
                    }

                    if (hasPnl)
                    {
                        returnValue = (double)Math.Abs(totalPnl);
                        if (returnValue <= 0.0) continue;

                        if (totalPnl > 0) wins.Add(returnValue);
                        else               losses.Add(returnValue);
                        pnlBasedCount++;
                        continue;
                    }
                }

                // Fallback: use ActualMagnitudePips when no position link exists
                if (log.ActualMagnitudePips is null) continue;
                returnValue = (double)Math.Abs(log.ActualMagnitudePips.Value);
                if (returnValue <= 0.0) continue;

                if (log.DirectionCorrect == true) wins.Add(returnValue);
                else                               losses.Add(returnValue);
            }

            // Require at least one win and one loss to compute a meaningful b ratio.
            if (wins.Count == 0 || losses.Count == 0)
            {
                await PersistNeutralKellyStateAsync(writeDb, model, configKey, ct);
                continue;
            }

            // Kelly formula: f* = (p × b − q) / b
            double p     = (double)wins.Count / (wins.Count + losses.Count);
            double q     = 1 - p;
            double b     = wins.Average() / (losses.Average() + 1e-8);
            double fStar = (p * b - q) / (b + 1e-8);

            // Half-Kelly: conservative sizing that halves variance while retaining
            // most of the geometric growth benefit. Capped at ±25% of account equity.
            double halfKelly = Math.Clamp(0.5 * fStar, -0.25, 0.25);

            bool negEv = fStar < 0;
            double deployedKellyCap = Math.Max(0.0, halfKelly);

            writeDb.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
            {
                MLModelId     = model.Id,
                Symbol        = model.Symbol,
                Timeframe     = model.Timeframe.ToString(),
                KellyFraction = Math.Clamp(fStar, -0.25, 0.25),
                HalfKelly     = halfKelly,
                WinRate       = p,
                WinLossRatio  = b,
                NegativeEV    = negEv,
                ComputedAt    = DateTime.UtcNow
            });

            await UpsertConfigAsync(
                writeDb,
                configKey,
                deployedKellyCap.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                ct);

            var tracked = await writeDb.Set<MLModel>().FindAsync(new object[] { model.Id }, ct);
            if (negEv)
            {
                if (tracked != null) tracked.IsSuppressed = true;
            }

            await writeDb.SaveChangesAsync(ct);

            if (!negEv && tracked?.IsSuppressed == true &&
                await MLSuppressionStateHelper.CanLiftSuppressionAsync(writeDb, tracked, ct))
            {
                tracked.IsSuppressed = false;
                await writeDb.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "MLKellyFractionWorker: {S}/{T} f*={F:F4} halfKelly={H:F4} winRate={P:F3} b={B:F3} negEV={N} pnlBased={PnlPct:P0}",
                model.Symbol, model.Timeframe, fStar, halfKelly, p, b, negEv,
                logs.Count > 0 ? (double)pnlBasedCount / logs.Count : 0.0);
        }
    }

    private static async Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
    {
        int rows = await writeCtx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Value, value)
                .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow), ct);

        if (rows == 0)
        {
            writeCtx.Set<EngineConfig>().Add(new EngineConfig
            {
                Key             = key,
                Value           = value,
                DataType        = Domain.Enums.ConfigDataType.Decimal,
                Description     = "Daily Kelly sizing cap written by MLKellyFractionWorker.",
                IsHotReloadable = true,
                LastUpdatedAt   = DateTime.UtcNow,
            });
        }
    }

    private static async Task PersistNeutralKellyStateAsync(
        Microsoft.EntityFrameworkCore.DbContext writeDb,
        MLModel                                 model,
        string                                  configKey,
        CancellationToken                       ct)
    {
        writeDb.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
        {
            MLModelId     = model.Id,
            Symbol        = model.Symbol,
            Timeframe     = model.Timeframe.ToString(),
            KellyFraction = 0.0,
            HalfKelly     = 0.0,
            WinRate       = 0.5,
            WinLossRatio  = 1.0,
            NegativeEV    = false,
            ComputedAt    = DateTime.UtcNow
        });

        await UpsertConfigAsync(writeDb, configKey, "1.0000", ct);
        await writeDb.SaveChangesAsync(ct);

        var tracked = await writeDb.Set<MLModel>().FindAsync(new object[] { model.Id }, ct);
        if (tracked?.IsSuppressed == true &&
            await MLSuppressionStateHelper.CanLiftSuppressionAsync(writeDb, tracked, ct))
        {
            tracked.IsSuppressed = false;
            await writeDb.SaveChangesAsync(ct);
        }
    }
}
