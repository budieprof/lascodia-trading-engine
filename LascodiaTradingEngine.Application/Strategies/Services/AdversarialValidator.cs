using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Strategies.Services;

/// <summary>
/// Result of adversarial stress-testing: a strategy is run against a baseline backtest
/// plus a series of synthetic scenarios that simulate realistic-but-rare events the
/// production system will eventually encounter. The promotion gate consumes
/// <see cref="Passed"/> directly; <see cref="Diagnostics"/> drives operator dashboards.
/// </summary>
public sealed record AdversarialValidationResult(
    bool                                       Passed,
    decimal                                    BaselineSharpe,
    IReadOnlyDictionary<string, decimal>       ScenarioSharpes,
    decimal                                    WorstCaseSharpe,
    decimal                                    SharpeDegradationPct,
    IReadOnlyList<string>                      Diagnostics);

public interface IAdversarialValidator
{
    Task<AdversarialValidationResult> ValidateAsync(
        long strategyId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken ct);
}

/// <summary>
/// Runs the strategy through a battery of adversarial scenarios on top of the
/// existing CPCV / TCA stack. Each scenario perturbs the same baseline candle
/// stream in a different way, and the strategy's Sharpe is recomputed on each
/// perturbed run. The strategy passes when the worst-case Sharpe stays above
/// the configurable floor and the degradation from baseline is bounded.
///
/// <para><b>Scenarios shipped:</b></para>
/// <list type="bullet">
/// <item><description><b>SlippageSpike</b> — 10× normal slippage to simulate broker outage / illiquid open.</description></item>
/// <item><description><b>NewsShock</b> — periodic gap insertions on candle opens (volatility cluster).</description></item>
/// <item><description><b>SpreadBlowout</b> — 5× normal spread to simulate cross-pair contagion.</description></item>
/// <item><description><b>RegimeFlip</b> — first-half / second-half partition Sharpe gap (hidden regime split).</description></item>
/// </list>
///
/// <para>
/// Scenarios are deliberately deterministic given the same candle window so that
/// the gate decision is reproducible. They are NOT a Monte Carlo replacement —
/// they are a worst-case envelope check.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IAdversarialValidator))]
public sealed class AdversarialValidator : IAdversarialValidator
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly IBacktestEngine _backtestEngine;
    private readonly IBacktestOptionsSnapshotBuilder _optionsSnapshotBuilder;
    private readonly ITcaCostModelProvider? _tcaProvider;
    private readonly ILogger<AdversarialValidator>? _logger;

    private const int     MinCandlesForAdversarial    = 500;
    private const decimal InitialBalance              = 10_000m;
    private const decimal SlippageSpikeMultiplier     = 10m;
    private const decimal SpreadBlowoutMultiplier     = 5m;

    public AdversarialValidator(
        IReadApplicationDbContext readCtx,
        IBacktestEngine backtestEngine,
        IBacktestOptionsSnapshotBuilder optionsSnapshotBuilder,
        ITcaCostModelProvider? tcaProvider = null,
        ILogger<AdversarialValidator>? logger = null)
    {
        _readCtx                = readCtx;
        _backtestEngine         = backtestEngine;
        _optionsSnapshotBuilder = optionsSnapshotBuilder;
        _tcaProvider            = tcaProvider;
        _logger                 = logger;
    }

    public async Task<AdversarialValidationResult> ValidateAsync(
        long strategyId, DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();
        var strategy = await db.Set<Strategy>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == strategyId && !s.IsDeleted, ct);
        if (strategy is null)
            return Empty(passed: false, diagnostic: $"Strategy {strategyId} not found");

        var candles = await db.Set<Candle>().AsNoTracking()
            .Where(c => c.Symbol == strategy.Symbol
                     && c.Timeframe == strategy.Timeframe
                     && c.IsClosed && !c.IsDeleted
                     && c.Timestamp >= fromDate && c.Timestamp <= toDate)
            .OrderBy(c => c.Timestamp)
            .ToListAsync(ct);
        if (candles.Count < MinCandlesForAdversarial)
            return Empty(passed: false, diagnostic: $"Only {candles.Count} candles (need ≥ {MinCandlesForAdversarial})");

        var snapshot = await _optionsSnapshotBuilder.BuildAsync(db, strategy.Symbol, ct);
        var baseline = snapshot.ToOptions();
        if (_tcaProvider is not null)
            baseline.TcaProfile = await _tcaProvider.GetAsync(strategy.Symbol, ct);

        // ── Baseline ─────────────────────────────────────────────────────────
        decimal baselineSharpe = await SafeRunSharpeAsync(strategy, candles, baseline, ct, "baseline");

        var scenarios = new Dictionary<string, decimal>();
        scenarios["SlippageSpike"]  = await SafeRunSharpeAsync(strategy, candles, ApplySlippageSpike(baseline),  ct, "slippage_spike");
        scenarios["SpreadBlowout"]  = await SafeRunSharpeAsync(strategy, candles, ApplySpreadBlowout(baseline),  ct, "spread_blowout");
        scenarios["NewsShock"]      = await SafeRunSharpeAsync(strategy, ApplyNewsShockCandles(candles), baseline, ct, "news_shock");
        scenarios["RegimeFlip"]     = ComputeRegimeFlipSharpeGap(strategy, candles, baseline, ct);

        decimal worst = scenarios.Values.DefaultIfEmpty(0m).Min();
        decimal degradationPct = baselineSharpe > 0
            ? (baselineSharpe - worst) / baselineSharpe * 100m
            : 0m;

        // ── Pass criteria ───────────────────────────────────────────────────
        // Worst-case Sharpe ≥ 0  AND  degradation from baseline ≤ 60%.
        // A strategy whose Sharpe collapses entirely under stress was edge-fragile
        // and will surrender PnL on the first real broker hiccup or news event.
        bool worstSurvives = worst >= 0m;
        bool degradationBounded = degradationPct <= 60m;
        bool passed = worstSurvives && degradationBounded;

        var diagnostics = new List<string>
        {
            $"Baseline Sharpe={baselineSharpe:F3}",
            $"Worst-case Sharpe={worst:F3}",
            $"Degradation={degradationPct:F1}%",
        };
        diagnostics.AddRange(scenarios.Select(kv => $"  {kv.Key}={kv.Value:F3}"));

        return new AdversarialValidationResult(
            Passed:               passed,
            BaselineSharpe:       baselineSharpe,
            ScenarioSharpes:      scenarios,
            WorstCaseSharpe:      worst,
            SharpeDegradationPct: degradationPct,
            Diagnostics:          diagnostics);
    }

    // ── Scenario constructors ────────────────────────────────────────────

    private static BacktestOptions ApplySlippageSpike(BacktestOptions baseline)
    {
        return new BacktestOptions
        {
            SlippagePriceUnits   = baseline.SlippagePriceUnits * SlippageSpikeMultiplier,
            CommissionPerLot     = baseline.CommissionPerLot,
            SwapPerLotPerDay     = baseline.SwapPerLotPerDay,
            ContractSize         = baseline.ContractSize,
            PipSizeInPriceUnits  = baseline.PipSizeInPriceUnits,
            SpreadPriceUnits     = baseline.SpreadPriceUnits,
            SpreadFunction       = baseline.SpreadFunction,
            GapSlippagePct       = baseline.GapSlippagePct,
            FillRatio            = baseline.FillRatio,
            PositionSizer        = baseline.PositionSizer,
            TcaProfile           = baseline.TcaProfile,
        };
    }

    private static BacktestOptions ApplySpreadBlowout(BacktestOptions baseline)
    {
        return new BacktestOptions
        {
            SlippagePriceUnits   = baseline.SlippagePriceUnits,
            CommissionPerLot     = baseline.CommissionPerLot,
            SwapPerLotPerDay     = baseline.SwapPerLotPerDay,
            ContractSize         = baseline.ContractSize,
            PipSizeInPriceUnits  = baseline.PipSizeInPriceUnits,
            SpreadPriceUnits     = baseline.SpreadPriceUnits * SpreadBlowoutMultiplier,
            SpreadFunction       = baseline.SpreadFunction,
            GapSlippagePct       = baseline.GapSlippagePct,
            FillRatio            = baseline.FillRatio,
            PositionSizer        = baseline.PositionSizer,
            TcaProfile           = baseline.TcaProfile,
        };
    }

    /// <summary>
    /// Mutates every Nth candle to inject a synthetic 100-pip gap on open. Simulates
    /// economic-release shock without requiring real news data — the goal is to test
    /// whether the strategy bleeds out when faced with periodic volatility clusters.
    /// </summary>
    private static IReadOnlyList<Candle> ApplyNewsShockCandles(IReadOnlyList<Candle> baseline)
    {
        const int   ShockEveryNBars   = 240;   // ~1×/day on H1
        const decimal ShockPipSize    = 0.01m; // 100 pips on a 5-decimal pair
        var copy = new List<Candle>(baseline.Count);
        for (int i = 0; i < baseline.Count; i++)
        {
            var b = baseline[i];
            if (i > 0 && i % ShockEveryNBars == 0)
            {
                // Alternate up/down shocks to avoid biasing direction.
                decimal shock = (i % (ShockEveryNBars * 2) == 0) ? ShockPipSize : -ShockPipSize;
                copy.Add(new Candle
                {
                    Symbol    = b.Symbol,
                    Timeframe = b.Timeframe,
                    Timestamp = b.Timestamp,
                    Open      = b.Open + shock,
                    High      = b.High + shock,
                    Low       = b.Low  + shock,
                    Close     = b.Close + shock,
                    Volume    = b.Volume * 3,  // shocks come with volume
                    IsClosed  = b.IsClosed,
                });
            }
            else copy.Add(b);
        }
        return copy;
    }

    /// <summary>
    /// Runs the backtest on the first half and the second half of the candle window
    /// independently, returns the lower of the two Sharpes. A strategy whose edge
    /// is concentrated in one half (regime split) gets penalised here even if its
    /// total Sharpe looks fine.
    /// </summary>
    private decimal ComputeRegimeFlipSharpeGap(
        Strategy strategy, IReadOnlyList<Candle> candles, BacktestOptions options, CancellationToken ct)
    {
        int mid = candles.Count / 2;
        var firstHalf  = candles.Take(mid).ToList();
        var secondHalf = candles.Skip(mid).ToList();
        if (firstHalf.Count < 100 || secondHalf.Count < 100) return 0m;

        decimal s1 = SafeRunSharpeAsync(strategy, firstHalf,  options, ct, "regime_first").GetAwaiter().GetResult();
        decimal s2 = SafeRunSharpeAsync(strategy, secondHalf, options, ct, "regime_second").GetAwaiter().GetResult();
        return Math.Min(s1, s2);
    }

    private async Task<decimal> SafeRunSharpeAsync(
        Strategy strategy, IReadOnlyList<Candle> candles, BacktestOptions options,
        CancellationToken ct, string scenarioTag)
    {
        try
        {
            var result = await _backtestEngine.RunAsync(strategy, candles, InitialBalance, ct, options);
            return result.SharpeRatio;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "AdversarialValidator: scenario '{Scenario}' failed for strategy {Id} — counted as 0 Sharpe",
                scenarioTag, strategy.Id);
            return 0m;
        }
    }

    private static AdversarialValidationResult Empty(bool passed, string diagnostic) => new(
        Passed:               passed,
        BaselineSharpe:       0m,
        ScenarioSharpes:      new Dictionary<string, decimal>(),
        WorstCaseSharpe:      0m,
        SharpeDegradationPct: 0m,
        Diagnostics:          new[] { diagnostic });
}
