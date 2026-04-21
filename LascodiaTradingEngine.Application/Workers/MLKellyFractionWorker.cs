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
    private const string CK_PollSecs = "MLKellyFraction:PollIntervalSeconds";
    private const string CK_WindowDays = "MLKellyFraction:WindowDays";
    private const string CK_MinUsableSamples = "MLKellyFraction:MinUsableSamples";
    private const string CK_MinWins = "MLKellyFraction:MinWins";
    private const string CK_MinLosses = "MLKellyFraction:MinLosses";
    private const string CK_MaxAbsKelly = "MLKellyFraction:MaxAbsKelly";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLKellyFractionWorker> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises the worker with its DI scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per daily computation cycle so scoped EF Core
    /// contexts are correctly disposed after each pass.
    /// </param>
    /// <param name="logger">Structured logger scoped to this worker type.</param>
    public MLKellyFractionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLKellyFractionWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            int pollSeconds = 86_400;
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "MLKellyFractionWorker error"); }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();
                pollSeconds = await GetConfigAsync(readCtx, CK_PollSecs, pollSeconds, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "MLKellyFractionWorker: failed to read poll interval; using default.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(60, pollSeconds)), stoppingToken);
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
    internal Task RunOnceAsync(CancellationToken ct = default) => RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var readCtx  = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb   = readCtx.GetDbContext();
        var writeDb  = writeCtx.GetDbContext();
        var config = await LoadConfigAsync(readDb, ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-config.WindowDays);

        // Exclude meta-learners and MAML initialisers — they do not emit direct live trade signals.
        var models = await readDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive && !m.IsDeleted && !m.IsMetaLearner && !m.IsMamlInitializer
                         && m.ModelBytes != null)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        foreach (var model in models)
        {
            string configKey = $"{KellyConfigKeyPrefix}{model.Symbol}:{model.Timeframe}:{model.Id}{KellyCapKeySuffix}";

            // ── Load resolved prediction logs for the last 60 days ───────────
            var logs = await readDb.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(l => l.MLModelId == model.Id
                         && !l.IsDeleted
                         && l.ModelRole == ModelRole.Champion
                         && l.TradeSignal.MLModelId == model.Id
                         && l.DirectionCorrect != null
                         && l.OutcomeRecordedAt != null
                         && l.OutcomeRecordedAt >= cutoff)
                .OrderByDescending(l => l.OutcomeRecordedAt)
                .ToListAsync(ct);

            if (logs.Count < config.MinUsableSamples)
            {
                await PersistNeutralKellyStateAsync(
                    writeDb, model, configKey, logs.Count, 0, 0, 0, 0,
                    "InsufficientResolvedSamples", now, ct);
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
                         && p.OpenLots > 0m
                         && !p.IsDeleted)
                .Select(p => new
                {
                    p.OpenOrderId,
                    NormalizedNetPnl = (p.RealizedPnL + p.Swap - p.Commission) / p.OpenLots
                })
                .ToListAsync(ct);

            var orderToPnl = orderPositionPnl
                .Where(x => x.OpenOrderId.HasValue)
                .GroupBy(x => x.OpenOrderId!.Value)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.NormalizedNetPnl));

            // ── Compute Kelly from actual P&L where available, fall back to magnitude ──
            var wins   = new List<double>();
            var losses = new List<double>();
            int pnlBasedCount = 0;

            foreach (var log in logs)
            {
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

                    if (hasPnl && TryClassifyEconomicOutcome(totalPnl, out var pnlOutcome))
                    {
                        AddOutcome(wins, losses, pnlOutcome);
                        pnlBasedCount++;
                        continue;
                    }
                }

                if (TryClassifyFallbackOutcome(log, out var fallbackOutcome))
                    AddOutcome(wins, losses, fallbackOutcome);
            }

            int usableSamples = wins.Count + losses.Count;
            if (usableSamples < config.MinUsableSamples ||
                wins.Count < config.MinWins ||
                losses.Count < config.MinLosses)
            {
                await PersistNeutralKellyStateAsync(
                    writeDb, model, configKey, logs.Count, usableSamples,
                    wins.Count, losses.Count, pnlBasedCount,
                    "InsufficientUsableSamples", now, ct);
                continue;
            }

            // Kelly formula: f* = (p × b − q) / b
            double p     = (double)wins.Count / (wins.Count + losses.Count);
            double q     = 1 - p;
            double b     = wins.Average() / (losses.Average() + 1e-8);
            double fStar = (p * b - q) / (b + 1e-8);

            // Half-Kelly: conservative sizing that halves variance while retaining
            // most of the geometric growth benefit. Capped at ±25% of account equity.
            double halfKelly = Math.Clamp(0.5 * fStar, -config.MaxAbsKelly, config.MaxAbsKelly);

            bool negEv = fStar < 0;
            double deployedKellyCap = Math.Max(0.0, halfKelly);

            writeDb.Set<MLKellyFractionLog>().Add(new MLKellyFractionLog
            {
                MLModelId     = model.Id,
                Symbol        = model.Symbol,
                Timeframe     = model.Timeframe.ToString(),
                KellyFraction = Math.Clamp(fStar, -config.MaxAbsKelly, config.MaxAbsKelly),
                HalfKelly     = halfKelly,
                WinRate       = p,
                WinLossRatio  = b,
                NegativeEV    = negEv,
                TotalResolvedSamples = logs.Count,
                UsableSamples = usableSamples,
                WinCount = wins.Count,
                LossCount = losses.Count,
                PnlBasedSamples = pnlBasedCount,
                IsReliable = true,
                Status = "Computed",
                ComputedAt    = now
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
                usableSamples > 0 ? (double)pnlBasedCount / usableSamples : 0.0);
        }
    }

    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: LascodiaTradingEngine.Domain.Enums.ConfigDataType.Decimal, ct: ct);

    private static async Task<KellyRuntimeConfig> LoadConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext readDb,
        CancellationToken ct)
    {
        int windowDays = await GetConfigAsync(readDb, CK_WindowDays, 60, ct);
        int minUsable = await GetConfigAsync(readDb, CK_MinUsableSamples, 30, ct);
        int minWins = await GetConfigAsync(readDb, CK_MinWins, 5, ct);
        int minLosses = await GetConfigAsync(readDb, CK_MinLosses, 5, ct);
        double maxAbsKelly = await GetConfigAsync(readDb, CK_MaxAbsKelly, 0.25, ct);

        return new KellyRuntimeConfig(
            WindowDays: Math.Clamp(windowDays, 1, 365),
            MinUsableSamples: Math.Clamp(minUsable, 2, 10_000),
            MinWins: Math.Clamp(minWins, 1, 10_000),
            MinLosses: Math.Clamp(minLosses, 1, 10_000),
            MaxAbsKelly: double.IsFinite(maxAbsKelly)
                ? Math.Clamp(maxAbsKelly, 0.001, 1.0)
                : 0.25);
    }

    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext db,
        string key,
        T defaultValue,
        CancellationToken ct)
    {
        var entry = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);

        if (entry?.Value is null) return defaultValue;

        try
        {
            return (T)Convert.ChangeType(
                entry.Value,
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static bool TryClassifyEconomicOutcome(decimal pnl, out KellyOutcome outcome)
    {
        outcome = default;
        double magnitude = (double)Math.Abs(pnl);
        if (magnitude <= 0.0 || !double.IsFinite(magnitude))
            return false;

        outcome = new KellyOutcome(pnl > 0m, magnitude);
        return true;
    }

    internal static bool TryClassifyFallbackOutcome(MLModelPredictionLog log, out KellyOutcome outcome)
    {
        outcome = default;
        if (log.ActualMagnitudePips is null)
            return false;

        double magnitude = (double)Math.Abs(log.ActualMagnitudePips.Value);
        if (magnitude <= 0.0 || !double.IsFinite(magnitude))
            return false;

        bool isWin = log.WasProfitable ?? (log.DirectionCorrect == true);
        outcome = new KellyOutcome(isWin, magnitude);
        return true;
    }

    private static void AddOutcome(List<double> wins, List<double> losses, KellyOutcome outcome)
    {
        if (outcome.IsWin) wins.Add(outcome.Magnitude);
        else losses.Add(outcome.Magnitude);
    }

    private static async Task PersistNeutralKellyStateAsync(
        Microsoft.EntityFrameworkCore.DbContext writeDb,
        MLModel                                 model,
        string                                  configKey,
        int                                     totalResolvedSamples,
        int                                     usableSamples,
        int                                     winCount,
        int                                     lossCount,
        int                                     pnlBasedSamples,
        string                                  status,
        DateTime                                computedAt,
        CancellationToken                       ct)
    {
        _ = configKey;
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
            TotalResolvedSamples = totalResolvedSamples,
            UsableSamples = usableSamples,
            WinCount = winCount,
            LossCount = lossCount,
            PnlBasedSamples = pnlBasedSamples,
            IsReliable = false,
            Status = status,
            ComputedAt    = computedAt
        });

        await writeDb.SaveChangesAsync(ct);
    }

    internal readonly record struct KellyOutcome(bool IsWin, double Magnitude);

    internal sealed record KellyRuntimeConfig(
        int WindowDays,
        int MinUsableSamples,
        int MinWins,
        int MinLosses,
        double MaxAbsKelly);
}
