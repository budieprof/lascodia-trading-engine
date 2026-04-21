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
/// ML model from recent resolved served-champion prediction logs. Suppresses models with
/// negative conservative Kelly value and caps the position fraction. Runs every 24 hours by default.
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
/// <c>HalfKelly = 0.5 × conservative f*</c> as the recommended position fraction.
/// The conservative value uses a Bayesian lower-bound win rate, sample-size shrinkage
/// toward zero, and outlier-capped payoff magnitudes.
///
/// <b>Negative EV suppression:</b> When the conservative f* &lt; 0 the model is flagged
/// <c>IsSuppressed = true</c> so the signal pipeline ignores its outputs until the
/// suppression gates clear.
///
/// <b>Polling interval:</b> 24 hours by default. Daily computation uses a 60-day
/// live-outcome window by default, both hot-configurable via <c>EngineConfig</c>.
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
    private const string CK_PriorTrades = "MLKellyFraction:PriorTrades";
    private const string CK_WinRateLowerBoundZ = "MLKellyFraction:WinRateLowerBoundZ";
    private const string CK_OutlierPercentile = "MLKellyFraction:OutlierPercentile";
    private const string CK_MaxOutcomeMagnitude = "MLKellyFraction:MaxOutcomeMagnitude";

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
    /// served champion predictions, then computes and persists raw/conservative Kelly
    /// fractions and Half-Kelly to <c>MLKellyFractionLog</c>. Models with negative
    /// conservative expected value are suppressed.
    /// </summary>
    /// <remarks>
    /// Live-outcome methodology:
    /// <list type="number">
    ///   <item>
    ///     Load recent resolved served-champion <see cref="MLModelPredictionLog"/> rows.
    ///   </item>
    ///   <item>
    ///     Prefer closed-position P&amp;L normalized to risk multiple when stop-loss
    ///     and contract specs are available; otherwise fall back to P&amp;L per lot or
    ///     prediction-log profitability/magnitude.
    ///   </item>
    ///   <item>
    ///     Apply outlier caps, Bayesian win-rate lower bound, shrinkage, then compute
    ///     raw and conservative f* plus Half-Kelly.
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
            var symbols = logs.Select(l => l.Symbol).Distinct().ToList();

            var signalSnapshots = await readDb.Set<TradeSignal>()
                .AsNoTracking()
                .Where(s => signalIds.Contains(s.Id) && !s.IsDeleted)
                .Select(s => new SignalRiskSnapshot(s.Id, s.Symbol, s.EntryPrice, s.StopLoss))
                .ToDictionaryAsync(s => s.Id, ct);

            var contractSizes = await readDb.Set<CurrencyPair>()
                .AsNoTracking()
                .Where(c => !c.IsDeleted && symbols.Contains(c.Symbol))
                .Select(c => new { c.Symbol, c.ContractSize })
                .ToDictionaryAsync(c => c.Symbol, c => c.ContractSize, ct);

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
                .Select(p => new OrderPositionOutcome(
                    p.OpenOrderId!.Value,
                    p.Symbol,
                    p.RealizedPnL + p.Swap - p.Commission,
                    p.OpenLots))
                .ToListAsync(ct);

            var orderToPositionOutcomes = orderPositionPnl
                .GroupBy(x => x.OpenOrderId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // ── Compute Kelly from actual P&L where available, fall back to magnitude ──
            var wins   = new List<double>();
            var losses = new List<double>();
            int pnlBasedCount = 0;
            int riskMultipleCount = 0;

            foreach (var log in logs)
            {
                // Prefer actual position-level P&L (net of costs) when available
                if (signalToOrderIds.TryGetValue(log.TradeSignalId, out var orderIds))
                {
                    decimal totalPnl = 0m;
                    decimal totalRiskAmount = 0m;
                    decimal totalPnlPerLot = 0m;
                    bool hasPnl = false;
                    foreach (var oid in orderIds)
                    {
                        if (!orderToPositionOutcomes.TryGetValue(oid, out var positionOutcomes))
                            continue;

                        foreach (var positionOutcome in positionOutcomes)
                        {
                            totalPnl += positionOutcome.NetPnl;
                            totalPnlPerLot += positionOutcome.NetPnl / positionOutcome.OpenLots;

                            if (signalSnapshots.TryGetValue(log.TradeSignalId, out var signalSnapshot) &&
                                contractSizes.TryGetValue(positionOutcome.Symbol, out var contractSize) &&
                                TryResolveRiskAmount(signalSnapshot, contractSize, positionOutcome.OpenLots, out var riskAmount))
                            {
                                totalRiskAmount += riskAmount;
                            }

                            hasPnl = true;
                        }
                    }

                    if (hasPnl && TryClassifyEconomicOutcome(totalPnl, totalPnlPerLot, totalRiskAmount, out var pnlOutcome))
                    {
                        AddOutcome(wins, losses, pnlOutcome);
                        pnlBasedCount++;
                        if (pnlOutcome.NormalizationMode == NormalizationModes.RiskMultiple)
                            riskMultipleCount++;
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

            var clipped = ApplyOutcomeCap(wins, losses, config);

            // Kelly formula: f* = (p × b − q) / b. The deployed value uses a
            // Bayesian lower-bound win rate and sample-size shrinkage so marginal
            // edges size toward zero instead of overreacting to noisy live samples.
            double p     = (double)wins.Count / usableSamples;
            double q     = 1 - p;
            double b     = clipped.Wins.Average() / (clipped.Losses.Average() + 1e-8);
            double rawFStar = (p * b - q) / (b + 1e-8);
            double conservativeP = ComputeConservativeWinRate(wins.Count, usableSamples, config);
            double conservativeQ = 1 - conservativeP;
            double shrinkage = ComputeShrinkage(usableSamples, config);
            double fStar = shrinkage * ((conservativeP * b - conservativeQ) / (b + 1e-8));

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
                RawKellyFraction = Math.Clamp(rawFStar, -config.MaxAbsKelly, config.MaxAbsKelly),
                HalfKelly     = halfKelly,
                WinRate       = p,
                WinLossRatio  = b,
                ConservativeWinRate = conservativeP,
                ShrinkageFactor = shrinkage,
                OutlierCap = clipped.Cap,
                NormalizationMode = riskMultipleCount > 0
                    ? NormalizationModes.RiskMultiple
                    : NormalizationModes.PnlPerLot,
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
                "MLKellyFractionWorker: {S}/{T} rawF*={Raw:F4} f*={F:F4} halfKelly={H:F4} winRate={P:F3} pLcb={PLcb:F3} b={B:F3} shrink={Shrink:F3} negEV={N} pnlBased={PnlPct:P0}",
                model.Symbol, model.Timeframe, rawFStar, fStar, halfKelly, p, conservativeP, b, shrinkage, negEv,
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
        double priorTrades = await GetConfigAsync(readDb, CK_PriorTrades, 20.0, ct);
        double winRateLowerBoundZ = await GetConfigAsync(readDb, CK_WinRateLowerBoundZ, 1.0, ct);
        double outlierPercentile = await GetConfigAsync(readDb, CK_OutlierPercentile, 0.95, ct);
        double maxOutcomeMagnitude = await GetConfigAsync(readDb, CK_MaxOutcomeMagnitude, 10.0, ct);

        return new KellyRuntimeConfig(
            WindowDays: Math.Clamp(windowDays, 1, 365),
            MinUsableSamples: Math.Clamp(minUsable, 2, 10_000),
            MinWins: Math.Clamp(minWins, 1, 10_000),
            MinLosses: Math.Clamp(minLosses, 1, 10_000),
            MaxAbsKelly: double.IsFinite(maxAbsKelly)
                ? Math.Clamp(maxAbsKelly, 0.001, 1.0)
                : 0.25,
            PriorTrades: double.IsFinite(priorTrades)
                ? Math.Clamp(priorTrades, 0.0, 1_000.0)
                : 20.0,
            WinRateLowerBoundZ: double.IsFinite(winRateLowerBoundZ)
                ? Math.Clamp(winRateLowerBoundZ, 0.0, 3.0)
                : 1.0,
            OutlierPercentile: double.IsFinite(outlierPercentile)
                ? Math.Clamp(outlierPercentile, 0.50, 1.0)
                : 0.95,
            MaxOutcomeMagnitude: double.IsFinite(maxOutcomeMagnitude)
                ? Math.Clamp(maxOutcomeMagnitude, 0.001, 1_000_000.0)
                : 10.0);
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

    private static bool TryResolveRiskAmount(
        SignalRiskSnapshot signal,
        decimal contractSize,
        decimal lots,
        out decimal riskAmount)
    {
        riskAmount = 0m;
        if (!signal.StopLoss.HasValue || signal.EntryPrice <= 0m || signal.StopLoss.Value <= 0m ||
            contractSize <= 0m || lots <= 0m)
            return false;

        riskAmount = Math.Abs(signal.EntryPrice - signal.StopLoss.Value) * contractSize * lots;
        return riskAmount > 0m;
    }

    internal static bool TryClassifyEconomicOutcome(
        decimal pnl,
        decimal pnlPerLot,
        decimal riskAmount,
        out KellyOutcome outcome)
    {
        outcome = default;
        bool hasRisk = riskAmount > 0m;
        double magnitude = hasRisk
            ? (double)Math.Abs(pnl / riskAmount)
            : (double)Math.Abs(pnlPerLot);
        if (magnitude <= 0.0 || !double.IsFinite(magnitude))
            return false;

        outcome = new KellyOutcome(
            pnl > 0m,
            magnitude,
            hasRisk ? NormalizationModes.RiskMultiple : NormalizationModes.PnlPerLot);
        return true;
    }

    internal static bool TryClassifyEconomicOutcome(decimal pnl, out KellyOutcome outcome)
        => TryClassifyEconomicOutcome(pnl, pnl, 0m, out outcome);

    internal static bool TryClassifyFallbackOutcome(MLModelPredictionLog log, out KellyOutcome outcome)
    {
        outcome = default;
        if (log.ActualMagnitudePips is null)
            return false;

        double magnitude = (double)Math.Abs(log.ActualMagnitudePips.Value);
        if (magnitude <= 0.0 || !double.IsFinite(magnitude))
            return false;

        bool isWin = log.WasProfitable ?? (log.DirectionCorrect == true);
        outcome = new KellyOutcome(isWin, magnitude, NormalizationModes.FallbackMagnitude);
        return true;
    }

    private static void AddOutcome(List<double> wins, List<double> losses, KellyOutcome outcome)
    {
        if (outcome.IsWin) wins.Add(outcome.Magnitude);
        else losses.Add(outcome.Magnitude);
    }

    internal static CappedOutcomes ApplyOutcomeCap(
        IReadOnlyList<double> wins,
        IReadOnlyList<double> losses,
        KellyRuntimeConfig config)
    {
        var all = wins.Concat(losses)
            .Where(v => double.IsFinite(v) && v > 0.0)
            .OrderBy(v => v)
            .ToArray();
        if (all.Length == 0)
            return new CappedOutcomes(wins.ToArray(), losses.ToArray(), config.MaxOutcomeMagnitude);

        int index = Math.Clamp(
            (int)Math.Ceiling(config.OutlierPercentile * all.Length) - 1,
            0,
            all.Length - 1);
        double cap = Math.Min(all[index], config.MaxOutcomeMagnitude);

        return new CappedOutcomes(
            wins.Select(w => Math.Min(w, cap)).ToArray(),
            losses.Select(l => Math.Min(l, cap)).ToArray(),
            cap);
    }

    internal static double ComputeConservativeWinRate(int wins, int total, KellyRuntimeConfig config)
    {
        if (total <= 0) return 0.0;

        double prior = config.PriorTrades;
        double posteriorN = total + prior;
        double posteriorMean = (wins + 0.5 * prior) / posteriorN;
        double posteriorVariance = posteriorMean * (1.0 - posteriorMean) / Math.Max(posteriorN + 1.0, 1.0);

        return Math.Clamp(
            posteriorMean - config.WinRateLowerBoundZ * Math.Sqrt(Math.Max(0.0, posteriorVariance)),
            0.0,
            1.0);
    }

    internal static double ComputeShrinkage(int total, KellyRuntimeConfig config)
    {
        if (total <= 0) return 0.0;
        return total / (total + config.PriorTrades);
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
            RawKellyFraction = 0.0,
            HalfKelly     = 0.0,
            WinRate       = 0.5,
            WinLossRatio  = 1.0,
            ConservativeWinRate = 0.5,
            ShrinkageFactor = 0.0,
            OutlierCap = 0.0,
            NormalizationMode = NormalizationModes.Unknown,
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

    internal static class NormalizationModes
    {
        internal const string Unknown = "Unknown";
        internal const string RiskMultiple = "RiskMultiple";
        internal const string PnlPerLot = "PnlPerLot";
        internal const string FallbackMagnitude = "FallbackMagnitude";
    }

    internal readonly record struct KellyOutcome(bool IsWin, double Magnitude, string NormalizationMode);

    internal sealed record CappedOutcomes(double[] Wins, double[] Losses, double Cap);

    private sealed record SignalRiskSnapshot(long Id, string Symbol, decimal EntryPrice, decimal? StopLoss);

    private sealed record OrderPositionOutcome(long OpenOrderId, string Symbol, decimal NetPnl, decimal OpenLots);

    internal sealed record KellyRuntimeConfig(
        int WindowDays,
        int MinUsableSamples,
        int MinWins,
        int MinLosses,
        double MaxAbsKelly,
        double PriorTrades,
        double WinRateLowerBoundZ,
        double OutlierPercentile,
        double MaxOutcomeMagnitude);
}
