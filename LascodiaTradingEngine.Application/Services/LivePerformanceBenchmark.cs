using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Computes live-vs-backtest performance haircut ratios by comparing in-sample screening
/// metrics against the latest live <see cref="StrategyPerformanceSnapshot"/> for each
/// qualifying active strategy. These haircuts are used to deflate backtest expectations
/// for newly generated strategies so the screening engine does not over-promote candidates
/// whose in-sample results are unlikely to replicate in live trading.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ILivePerformanceBenchmark))]
public class LivePerformanceBenchmark : ILivePerformanceBenchmark
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly IWriteApplicationDbContext _writeContext;
    private readonly ILogger<LivePerformanceBenchmark> _logger;

    /// <summary>Minimum qualifying strategies required before computing non-neutral haircuts.</summary>
    private const int MinQualifyingStrategies = 5;

    /// <summary>Minimum closed trades in the performance snapshot window.</summary>
    private const int MinWindowTrades = 30;

    // ── EngineConfig keys ────────────────────────────────────────────────────
    private const string KeyWinRateHaircut      = "LiveBenchmark:WinRateHaircut";
    private const string KeyProfitFactorHaircut = "LiveBenchmark:ProfitFactorHaircut";
    private const string KeySharpeHaircut       = "LiveBenchmark:SharpeHaircut";
    private const string KeyDrawdownInflation   = "LiveBenchmark:DrawdownInflation";
    private const string KeySampleCount         = "LiveBenchmark:SampleCount";

    public LivePerformanceBenchmark(
        IReadApplicationDbContext readContext,
        IWriteApplicationDbContext writeContext,
        ILogger<LivePerformanceBenchmark> logger)
    {
        _readContext  = readContext;
        _writeContext = writeContext;
        _logger       = logger;
    }

    /// <inheritdoc />
    public async Task<HaircutRatios> ComputeHaircutsAsync(CancellationToken ct)
    {
        try
        {
            var readDb = _readContext.GetDbContext();

            // 1. Query strategies where LifecycleStage >= Active, have ScreeningMetricsJson, not deleted
            var strategies = await readDb.Set<Strategy>()
                .AsNoTracking()
                .Where(s => s.LifecycleStage == StrategyLifecycleStage.Active
                         && s.ScreeningMetricsJson != null
                         && s.ScreeningMetricsJson != "")
                .Select(s => new { s.Id, s.ScreeningMetricsJson })
                .ToListAsync(ct);

            if (strategies.Count == 0)
            {
                _logger.LogDebug("LivePerformanceBenchmark: no active strategies with screening metrics found");
                return HaircutRatios.Neutral;
            }

            var strategyIds = strategies.Select(s => s.Id).ToList();

            // 2. For each strategy, load their latest StrategyPerformanceSnapshot where WindowTrades >= MinWindowTrades
            var latestSnapshots = await readDb.Set<StrategyPerformanceSnapshot>()
                .AsNoTracking()
                .Where(snap => strategyIds.Contains(snap.StrategyId)
                            && snap.WindowTrades >= MinWindowTrades)
                .GroupBy(snap => snap.StrategyId)
                .Select(g => g.OrderByDescending(snap => snap.EvaluatedAt).First())
                .ToListAsync(ct);

            // 3-4. Compute per-strategy ratios
            var winRateRatios      = new List<double>();
            var profitFactorRatios = new List<double>();
            var sharpeRatios       = new List<double>();
            var drawdownRatios     = new List<double>();

            foreach (var snapshot in latestSnapshots)
            {
                var strategy = strategies.FirstOrDefault(s => s.Id == snapshot.StrategyId);
                if (strategy is null) continue;

                var metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson);
                if (metrics is null) continue;

                // IS metrics (backtest)
                var isWinRate      = metrics.IsWinRate;
                var isProfitFactor = metrics.IsProfitFactor;
                var isSharpe       = metrics.IsSharpeRatio;
                var isMaxDD        = metrics.IsMaxDrawdownPct;

                // Live metrics from snapshot
                var liveWinRate      = (double)snapshot.WinRate;
                var liveProfitFactor = (double)snapshot.ProfitFactor;
                var liveSharpe       = (double)snapshot.SharpeRatio;
                var liveMaxDD        = (double)snapshot.MaxDrawdownPct;

                // Compute ratios — only include if ALL 4 IS metrics are > 0 to keep lists aligned
                if (isWinRate > 0 && isProfitFactor > 0 && isSharpe > 0 && isMaxDD > 0)
                {
                    winRateRatios.Add(liveWinRate / isWinRate);
                    profitFactorRatios.Add(liveProfitFactor / isProfitFactor);
                    sharpeRatios.Add(liveSharpe / isSharpe);
                    drawdownRatios.Add(liveMaxDD / isMaxDD);
                }
            }

            // 5-6. Take median of each ratio across qualifying strategies
            if (winRateRatios.Count < MinQualifyingStrategies)
            {
                _logger.LogDebug(
                    "LivePerformanceBenchmark: only {Count} qualifying strategies (need {Min}), returning Neutral",
                    winRateRatios.Count, MinQualifyingStrategies);
                return HaircutRatios.Neutral;
            }

            var haircuts = new HaircutRatios(
                WinRateHaircut:      Median(winRateRatios),
                ProfitFactorHaircut: Median(profitFactorRatios),
                SharpeHaircut:       Median(sharpeRatios),
                DrawdownInflation:   Median(drawdownRatios),
                SampleCount:         winRateRatios.Count);

            // 7. Persist to EngineConfig keys
            try
            {
                var writeDb = _writeContext.GetDbContext();
                await UpsertConfigAsync(writeDb, KeyWinRateHaircut,      haircuts.WinRateHaircut.ToString("F6"),      ct);
                await UpsertConfigAsync(writeDb, KeyProfitFactorHaircut, haircuts.ProfitFactorHaircut.ToString("F6"), ct);
                await UpsertConfigAsync(writeDb, KeySharpeHaircut,       haircuts.SharpeHaircut.ToString("F6"),       ct);
                await UpsertConfigAsync(writeDb, KeyDrawdownInflation,   haircuts.DrawdownInflation.ToString("F6"),   ct);
                await UpsertConfigAsync(writeDb, KeySampleCount,         haircuts.SampleCount.ToString(),             ct);
                await writeDb.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LivePerformanceBenchmark: failed to persist haircut ratios to EngineConfig");
            }

            _logger.LogInformation(
                "LivePerformanceBenchmark: computed haircuts from {Count} strategies — WR={WR:F4}, PF={PF:F4}, Sharpe={S:F4}, DD={DD:F4}",
                haircuts.SampleCount, haircuts.WinRateHaircut, haircuts.ProfitFactorHaircut,
                haircuts.SharpeHaircut, haircuts.DrawdownInflation);

            return haircuts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LivePerformanceBenchmark: failed to compute haircuts, returning Neutral");
            return HaircutRatios.Neutral;
        }
    }

    /// <inheritdoc />
    public async Task<HaircutRatios> GetCachedHaircutsAsync(CancellationToken ct)
    {
        try
        {
            var readDb = _readContext.GetDbContext();

            var keys = new[] { KeyWinRateHaircut, KeyProfitFactorHaircut, KeySharpeHaircut, KeyDrawdownInflation, KeySampleCount };

            var configs = await readDb.Set<EngineConfig>()
                .AsNoTracking()
                .Where(c => keys.Contains(c.Key))
                .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

            if (configs.Count < keys.Length)
                return HaircutRatios.Neutral;

            if (!double.TryParse(configs[KeyWinRateHaircut], out var wr)
                || !double.TryParse(configs[KeyProfitFactorHaircut], out var pf)
                || !double.TryParse(configs[KeySharpeHaircut], out var sharpe)
                || !double.TryParse(configs[KeyDrawdownInflation], out var dd)
                || !int.TryParse(configs[KeySampleCount], out var count))
            {
                return HaircutRatios.Neutral;
            }

            return new HaircutRatios(wr, pf, sharpe, dd, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LivePerformanceBenchmark: failed to read cached haircuts, returning Neutral");
            return HaircutRatios.Neutral;
        }
    }

    /// <inheritdoc />
    public async Task<HaircutRatios> ComputeBootstrappedHaircutsAsync(CancellationToken ct)
    {
        try
        {
            var db = _readContext.GetDbContext();
            // Load strategies with both IS and OOS metrics from ScreeningMetrics
            var strategies = await db.Set<Strategy>()
                .Where(s => !s.IsDeleted && s.Name.StartsWith("Auto-")
                         && s.ScreeningMetricsJson != null && s.ScreeningMetricsJson != "")
                .Select(s => s.ScreeningMetricsJson)
                .ToListAsync(ct);

            var wrRatios = new List<double>();
            var pfRatios = new List<double>();
            var shRatios = new List<double>();
            var ddRatios = new List<double>();

            foreach (var json in strategies)
            {
                var m = ScreeningMetrics.FromJson(json);
                if (m == null) continue;
                // All 4 IS metrics must be > 0 and all 4 OOS metrics must be > 0
                if (m.IsWinRate <= 0 || m.IsProfitFactor <= 0 || m.IsSharpeRatio <= 0 || m.IsMaxDrawdownPct <= 0) continue;
                if (m.OosWinRate <= 0 || m.OosProfitFactor <= 0 || m.OosSharpeRatio <= 0 || m.OosMaxDrawdownPct <= 0) continue;

                wrRatios.Add(m.OosWinRate / m.IsWinRate);
                pfRatios.Add(m.OosProfitFactor / m.IsProfitFactor);
                shRatios.Add(m.OosSharpeRatio / m.IsSharpeRatio);
                ddRatios.Add(m.OosMaxDrawdownPct / m.IsMaxDrawdownPct);
            }

            if (wrRatios.Count < 5)
                return HaircutRatios.Neutral;

            return new HaircutRatios(
                WinRateHaircut: Median(wrRatios),
                ProfitFactorHaircut: Median(pfRatios),
                SharpeHaircut: Median(shRatios),
                DrawdownInflation: Median(ddRatios),
                SampleCount: -wrRatios.Count);  // Negative = bootstrapped
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LivePerformanceBenchmark: bootstrapped haircut computation failed");
            return HaircutRatios.Neutral;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 1.0;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[mid - 1] + values[mid]) / 2.0
            : values[mid];
    }

    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        string key,
        string value,
        CancellationToken ct)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(writeCtx, key, value, dataType: LascodiaTradingEngine.Domain.Enums.ConfigDataType.Decimal, ct: ct);
}
