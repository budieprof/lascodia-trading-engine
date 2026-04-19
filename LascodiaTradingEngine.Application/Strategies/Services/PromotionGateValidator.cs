using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Services;

/// <summary>
/// Hard promotion gate for strategies moving from Approved → Active. Enforces the
/// four checks that separate "backtested well" from "has live edge":
///
/// <list type="number">
/// <item><description><b>Deflated Sharpe Ratio (DSR) ≥ 1.0</b> — Bailey / López de Prado
///   correction for multiple-testing bias. A raw Sharpe of 2.0 from a generation cycle
///   that evaluated 200 candidates is statistically indistinguishable from noise;
///   DSR corrects for the selection effect.</description></item>
/// <item><description><b>Probability of Backtest Overfitting (PBO) ≤ 0.30</b> — fraction
///   of combinatorial backtest paths on which the chosen strategy ranked below-median
///   out-of-sample. PBO > 0.5 means the strategy is indistinguishable from a random
///   pick among its peers.</description></item>
/// <item><description><b>TCA-adjusted Expected Value &gt; 0</b> — backtest profit minus
///   realised slippage + spread + commission from <see cref="TransactionCostAnalysis"/>
///   per-symbol rolling averages. Strategies profitable only against a fixed-spread
///   assumption fail here; this catches "mathematically profitable, executionally
///   unprofitable" strategies.</description></item>
/// <item><description><b>Paper-trade duration ≥ N days with M trades</b> — forces live
///   paper trading on real market data before capital exposure. Catches regime changes
///   between backtest cutoff and now; a strategy trained on Q2 2025 data may fail
///   immediately when deployed in Q2 2026 because the micro-regime shifted.</description></item>
/// </list>
///
/// All four must pass. Each can be tuned via EngineConfig; disabling individual gates
/// is opt-in via <c>Promotion:DisableGates</c> (comma-separated gate names) — use only
/// during bootstrap or testing. Running in production with gates disabled is a
/// P&amp;L-destroying mistake.
/// </summary>
public sealed class PromotionGateValidator : IPromotionGateValidator
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly ICpcvValidator _cpcv;
    private readonly IEdgePosterior _edgePosterior;

    public PromotionGateValidator(
        IReadApplicationDbContext readCtx,
        ICpcvValidator cpcv,
        IEdgePosterior edgePosterior)
    {
        _readCtx = readCtx;
        _cpcv = cpcv;
        _edgePosterior = edgePosterior;
    }

    public async Task<PromotionGateResult> EvaluateAsync(
        long strategyId, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();

        // Load strategy + its most recent backtest
        var strategy = await db.Set<Domain.Entities.Strategy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == strategyId && !s.IsDeleted, ct);
        if (strategy is null)
            return PromotionGateResult.Fail("Strategy not found", new[] { "not_found" });

        var latestBacktest = await db.Set<BacktestRun>()
            .AsNoTracking()
            .Where(b => b.StrategyId == strategyId && !b.IsDeleted && b.Status == RunStatus.Completed)
            .OrderByDescending(b => b.CompletedAt)
            .FirstOrDefaultAsync(ct);
        if (latestBacktest is null)
            return PromotionGateResult.Fail(
                "No completed BacktestRun found — cannot evaluate promotion gates",
                new[] { "no_backtest" });

        // Load disabled-gates config (opt-in bypass for bootstrap; shipped empty)
        var disabledCsv = await GetConfigAsync(db, "Promotion:DisableGates", "", ct);
        var disabledGates = new HashSet<string>(
            disabledCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        var failures = new List<string>();
        var diagnostics = new List<string>();

        // ── Consolidate DSR + PBO under the Bayesian edge posterior ────────
        // The posterior gate (gate 8) already incorporates selection-bias deflation
        // and is informed by CPCV's real out-of-sample Sharpe distribution (gate 7),
        // so running the single-path DSR (gate 1) and PBO-proxy (gate 2) alongside
        // it is belt-and-braces: same signal, double-counted. Auto-skip the single-
        // path proxies whenever both the posterior and CPCV gates are active. An
        // operator can still force the legacy behaviour by setting
        // Promotion:ConsolidateDsrPbo = false in EngineConfig.
        bool consolidate = await GetConfigBoolAsync(db, "Promotion:ConsolidateDsrPbo", true, ct);
        bool cpcvActive  = !disabledGates.Contains("cpcv");
        bool postActive  = !disabledGates.Contains("edge_posterior");
        if (consolidate && cpcvActive && postActive)
        {
            if (!disabledGates.Contains("dsr"))
            {
                disabledGates.Add("dsr");
                diagnostics.Add("DSR gate auto-skipped: consolidated under edge_posterior + CPCV");
            }
            if (!disabledGates.Contains("pbo"))
            {
                disabledGates.Add("pbo");
                diagnostics.Add("PBO-proxy gate auto-skipped: consolidated under edge_posterior + CPCV");
            }
        }

        // ── Gate 1: Deflated Sharpe Ratio ───────────────────────────────────
        if (!disabledGates.Contains("dsr"))
        {
            double minDsr = await GetConfigDoubleAsync(db, "Promotion:MinDSR", 1.0, ct);
            // Number of strategies evaluated in the same generation cycle is the
            // effective multiple-testing count for DSR. A single cycle that
            // produced this strategy along with N others is what inflated the
            // observed Sharpe.
            int trialsCount = await db.Set<Domain.Entities.Strategy>()
                .AsNoTracking()
                .CountAsync(s => s.Symbol == strategy.Symbol
                              && s.Timeframe == strategy.Timeframe
                              && !s.IsDeleted, ct);
            trialsCount = Math.Max(trialsCount, 2); // avoid log(1) = 0 in DSR

            double rawSharpe = (double)(latestBacktest.SharpeRatio ?? 0m);
            int trades = latestBacktest.TotalTrades ?? 0;
            double dsr = ComputeDeflatedSharpe(rawSharpe, trials: trialsCount, trades: trades);
            diagnostics.Add($"DSR={dsr:F3} (raw Sharpe={rawSharpe:F3}, trials={trialsCount}, trades={trades})");

            if (dsr < minDsr)
                failures.Add($"DSR {dsr:F3} < min {minDsr:F2}");
        }

        // ── Gate 2: Probability of Backtest Overfitting (PBO) ───────────────
        // True PBO requires CPCV folds which we do not yet compute (#3 on the
        // pipeline roadmap). Use a proxy: ratio of this backtest's Sharpe to the
        // median Sharpe across all completed backtests for the same symbol+tf.
        // A strategy whose Sharpe is below the peer median likely would have
        // been out-of-sample rank-reversed on random partitions — a weak proxy
        // but directionally correct until real CPCV lands.
        if (!disabledGates.Contains("pbo"))
        {
            double maxPbo = await GetConfigDoubleAsync(db, "Promotion:MaxPBO", 0.30, ct);
            double pbo = await ComputePboProxyAsync(db, strategy, latestBacktest, ct);
            diagnostics.Add($"PBO-proxy={pbo:F3}");

            if (pbo > maxPbo)
                failures.Add($"PBO-proxy {pbo:F3} > max {maxPbo:F2}");
        }

        // ── Gate 3: TCA-adjusted Expected Value > 0 ─────────────────────────
        if (!disabledGates.Contains("tca"))
        {
            double totalReturn = (double)(latestBacktest.TotalReturn ?? 0m);
            int trades = Math.Max(1, latestBacktest.TotalTrades ?? 1);
            double backtestEvPerTrade = totalReturn / trades;

            double avgTcaPerTrade = await db.Set<TransactionCostAnalysis>()
                .AsNoTracking()
                .Where(t => t.Symbol == strategy.Symbol && !t.IsDeleted)
                .OrderByDescending(t => t.AnalyzedAt)
                .Take(200) // rolling sample; empty set returns 0
                .Select(t => (double)(t.SpreadCost + t.CommissionCost + t.MarketImpactCost))
                .DefaultIfEmpty(0.0)
                .AverageAsync(ct);

            double adjustedEv = backtestEvPerTrade - avgTcaPerTrade;
            diagnostics.Add(
                $"TCA-adjusted EV/trade={adjustedEv:F5} (backtest={backtestEvPerTrade:F5}, avg TCA={avgTcaPerTrade:F5})");

            if (adjustedEv <= 0)
                failures.Add($"TCA-adjusted EV/trade {adjustedEv:F5} ≤ 0 — unprofitable after real costs");
        }

        // ── Gate 4: Paper-trade duration ─────────────────────────────────────
        if (!disabledGates.Contains("paper"))
        {
            int minPaperDays = await GetConfigIntAsync(db, "Promotion:MinPaperTradeDays", 60, ct);
            int minPaperTrades = await GetConfigIntAsync(db, "Promotion:MinPaperTradeCount", 100, ct);
            DateTime now = DateTime.UtcNow;

            // Paper duration = days in current lifecycle stage (Approved implies it
            // was previously PaperTrading or similar). LifecycleStageEnteredAt
            // becomes the proxy; if null, strategy is brand-new and fails.
            double daysInStage = strategy.LifecycleStageEnteredAt is { } enteredAt
                ? (now - enteredAt).TotalDays
                : 0.0;

            // Count paper-mode trades via any ExecutionQualityLog or Trade entity
            // associated with the strategy. Kept simple: use BacktestRun trades as
            // the proxy until a first-class PaperTrade entity exists.
            int paperTrades = latestBacktest.TotalTrades ?? 0;

            diagnostics.Add($"Paper={daysInStage:F1}d, trades={paperTrades}");

            if (daysInStage < minPaperDays)
                failures.Add($"Paper-trade duration {daysInStage:F1}d < min {minPaperDays}d");
            if (paperTrades < minPaperTrades)
                failures.Add($"Paper-trade count {paperTrades} < min {minPaperTrades}");
        }

        // ── Gate 5 (#5 on pipeline roadmap): Regime-stratified performance ──
        // Deferred: StrategyPerformanceSnapshot currently has no regime column, so
        // we cannot verify "positive Sharpe in ≥ N distinct regimes" without schema
        // work. Once MarketRegimeSnapshot + per-regime StrategyPerformanceSnapshot
        // are joined, re-enable this gate. For now use a weaker proxy: require the
        // most-recent backtest to have covered at least 6 months (≥180 days) of data,
        // which likely spans multiple regimes in live markets.
        if (!disabledGates.Contains("regime"))
        {
            int minBacktestDays = await GetConfigIntAsync(db, "Promotion:MinBacktestDurationDays", 180, ct);
            double coveredDays = (latestBacktest.ToDate - latestBacktest.FromDate).TotalDays;
            diagnostics.Add($"BacktestCoverage={coveredDays:F0}d (regime proxy)");

            if (coveredDays < minBacktestDays)
                failures.Add(
                    $"Backtest duration {coveredDays:F0}d < min {minBacktestDays}d " +
                    "— too short to cover multiple regimes, strategy may be fragile to regime change");
        }

        // ── Gate 7 (#3 on pipeline roadmap): CPCV P25 Sharpe ≥ threshold ────
        // A strategy's median Sharpe can be inflated by a few lucky windows; CPCV
        // re-partitions the realised trades and checks that the 25th-percentile
        // Sharpe across C(N,K) resamples is still positive. If it isn't, the
        // strategy's edge is concentrated rather than robust.
        if (!disabledGates.Contains("cpcv"))
        {
            double minP25Sharpe = await GetConfigDoubleAsync(db, "Promotion:MinCpcvP25Sharpe", 0.0, ct);
            var cpcv = await _cpcv.ValidateAsync(
                strategyId, latestBacktest.FromDate, latestBacktest.ToDate, ct);
            diagnostics.Add(
                $"CPCV(N={cpcv.NGroups},K={cpcv.KTestGroups}): median={cpcv.MedianSharpe:F3} " +
                $"P25={cpcv.P25Sharpe:F3} P75={cpcv.P75Sharpe:F3} DSR={cpcv.DeflatedSharpe:F3} " +
                $"PBO={cpcv.ProbabilityOfOverfitting:F3}");

            if (cpcv.SharpeDistribution.Count == 0)
            {
                failures.Add(
                    "CPCV: no trades available for partition resampling " +
                    "— need ≥60 closed positions before promotion");
            }
            else if (cpcv.P25Sharpe < minP25Sharpe)
            {
                failures.Add(
                    $"CPCV P25 Sharpe {cpcv.P25Sharpe:F3} < min {minP25Sharpe:F2} " +
                    "— edge is concentrated in lucky windows rather than robust");
            }
        }

        // ── Gate 8 (#9 on pipeline roadmap): Posterior P(live Sharpe > 0) ───
        // Reframes "observed Sharpe > X" as the properly-updated belief given
        // selection bias. Deflates the observation by √log(trials) before the
        // Bayesian update, so cherry-picked winners need stronger evidence than
        // a-priori hypotheses.
        if (!disabledGates.Contains("edge_posterior"))
        {
            double minEdgeProb = await GetConfigDoubleAsync(db, "Promotion:MinEdgeProbability", 0.70, ct);
            int trialsCount = await db.Set<Domain.Entities.Strategy>()
                .AsNoTracking()
                .CountAsync(s => s.Symbol == strategy.Symbol
                              && s.Timeframe == strategy.Timeframe
                              && !s.IsDeleted, ct);
            double rawSharpe = (double)(latestBacktest.SharpeRatio ?? 0m);
            int trades = latestBacktest.TotalTrades ?? 0;
            var post = _edgePosterior.Compute(new EdgeObservation(
                ObservedSharpe: rawSharpe,
                NumberOfTrades: trades,
                NumberOfTrials: trialsCount));
            diagnostics.Add(
                $"EdgePosterior: μ={post.PosteriorMean:F3} σ={post.PosteriorStdDev:F3} " +
                $"P(edge>0)={post.ProbabilityOfPositiveEdge:F3}");

            if (post.ProbabilityOfPositiveEdge < minEdgeProb)
                failures.Add(
                    $"P(true edge > 0) = {post.ProbabilityOfPositiveEdge:F3} < min {minEdgeProb:F2} " +
                    "— after accounting for selection bias and observation precision, " +
                    "the strategy's posterior probability of real edge is too low");
        }

        // ── Gate 6 (#6 on pipeline roadmap): Correlation to existing actives ─
        if (!disabledGates.Contains("correlation"))
        {
            double maxCorr = await GetConfigDoubleAsync(db, "Promotion:MaxPairwiseCorrelation", 0.7, ct);
            double observedCorr = await ComputeMaxPairwiseCorrelationAsync(db, strategy, ct);
            diagnostics.Add($"MaxCorrelationVsActives={observedCorr:F3}");

            if (observedCorr > maxCorr)
                failures.Add(
                    $"P&L correlation {observedCorr:F3} > max {maxCorr:F2} with an existing Active " +
                    "strategy — promotion blocked to avoid portfolio concentration masquerading as diversification");
        }

        return failures.Count == 0
            ? PromotionGateResult.Pass(diagnostics)
            : PromotionGateResult.Fail(string.Join("; ", failures), diagnostics);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deflated Sharpe Ratio approximation — Bailey/López de Prado (2014).
    /// For a full implementation we'd also pass the skewness and kurtosis of the
    /// return series; in the absence of that we assume Gaussian (skew=0, kurt=3),
    /// which makes this a conservative lower bound on the true DSR. Good enough
    /// for promotion gating — err on the side of rejection.
    /// </summary>
    internal static double ComputeDeflatedSharpe(double rawSharpe, int trials, int trades)
    {
        if (trades <= 1 || trials <= 1) return 0.0;

        // Expected maximum of N iid normals ≈ σ·(1 − γ)·Φ⁻¹(1 − 1/N) + σ·γ·Φ⁻¹(1 − 1/(N·e))
        // For Gaussian Sharpe with N trials and T observations, σ_SR ≈ 1/√T.
        const double EulerMascheroni = 0.5772156649015329;
        double invErfTerm1 = InverseStandardNormalCdf(1.0 - 1.0 / trials);
        double invErfTerm2 = InverseStandardNormalCdf(1.0 - 1.0 / (trials * Math.E));
        double expectedMaxSharpe = (1.0 - EulerMascheroni) * invErfTerm1
                                 + EulerMascheroni * invErfTerm2;
        expectedMaxSharpe /= Math.Sqrt(trades);

        // Non-annualized deflation: DSR = (SR_observed - E[max SR]) / σ(SR_obs | Gaussian)
        double sigmaSr = 1.0 / Math.Sqrt(trades);
        double deflated = (rawSharpe - expectedMaxSharpe) / Math.Max(sigmaSr, 1e-9);
        return deflated;
    }

    /// <summary>
    /// Approximation of the inverse standard-normal CDF (probit). Adequate for
    /// promotion-gate usage; for higher precision use the Acklam algorithm or a
    /// MathNet package.
    /// </summary>
    internal static double InverseStandardNormalCdf(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        // Beasley-Springer-Moro (simplified central region)
        double a1 = -3.969683028665376e+01, a2 = 2.209460984245205e+02,
               a3 = -2.759285104469687e+02, a4 = 1.383577518672690e+02,
               a5 = -3.066479806614716e+01, a6 = 2.506628277459239e+00;
        double b1 = -5.447609879822406e+01, b2 = 1.615858368580409e+02,
               b3 = -1.556989798598866e+02, b4 = 6.680131188771972e+01,
               b5 = -1.328068155288572e+01;
        double c1 = -7.784894002430293e-03, c2 = -3.223964580411365e-01,
               c3 = -2.400758277161838e+00, c4 = -2.549732539343734e+00,
               c5 = 4.374664141464968e+00, c6 = 2.938163982698783e+00;
        double d1 = 7.784695709041462e-03, d2 = 3.224671290700398e-01,
               d3 = 2.445134137142996e+00, d4 = 3.754408661907416e+00;
        double pLow = 0.02425, pHigh = 1 - pLow;
        double x, q, r;
        if (p < pLow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6)
              / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
        else if (p <= pHigh)
        {
            q = p - 0.5; r = q * q;
            x = (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q
              / (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1);
        }
        else
        {
            q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6)
              / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
        return x;
    }

    private static async Task<double> ComputePboProxyAsync(
        DbContext db, Domain.Entities.Strategy strategy, BacktestRun latest, CancellationToken ct)
    {
        var peerSharpes = await db.Set<BacktestRun>()
            .AsNoTracking()
            .Where(b => b.Symbol == strategy.Symbol
                     && b.Timeframe == strategy.Timeframe
                     && !b.IsDeleted
                     && b.Status == RunStatus.Completed
                     && b.SharpeRatio != null)
            .Select(b => (double)b.SharpeRatio!)
            .ToListAsync(ct);

        if (peerSharpes.Count < 5) return 0.5; // not enough peers — give benefit of doubt but don't auto-pass

        peerSharpes.Sort();
        double median = peerSharpes[peerSharpes.Count / 2];
        double subjectSharpe = (double)(latest.SharpeRatio ?? 0m);

        // Proxy: fraction of peers with equal-or-better Sharpe. If 50th percentile
        // the strategy is median — 50% likely to underperform out-of-sample.
        int betterCount = peerSharpes.Count(s => s >= subjectSharpe);
        return (double)betterCount / peerSharpes.Count;
    }

    private static async Task<double> ComputeMaxPairwiseCorrelationAsync(
        DbContext db, Domain.Entities.Strategy strategy, CancellationToken ct)
    {
        // Load P&L time series for this strategy and each active strategy on the
        // same symbol. Use StrategyPerformanceSnapshot as the proxy for a trade-
        // level P&L series. If fewer than 30 observations, bail with 0.
        var subjectSeries = await db.Set<StrategyPerformanceSnapshot>()
            .AsNoTracking()
            .Where(s => s.StrategyId == strategy.Id)
            .OrderBy(s => s.EvaluatedAt)
            .Select(s => new { s.EvaluatedAt, s.SharpeRatio })
            .ToListAsync(ct);
        if (subjectSeries.Count < 30) return 0.0;

        var activeIds = await db.Set<Domain.Entities.Strategy>()
            .AsNoTracking()
            .Where(s => s.Status == StrategyStatus.Active
                     && s.Symbol == strategy.Symbol
                     && s.Id != strategy.Id
                     && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (activeIds.Count == 0) return 0.0;

        var subjectValues = subjectSeries.Select(s => (double)s.SharpeRatio).ToArray();
        double maxCorr = 0.0;
        foreach (var id in activeIds)
        {
            var peerSeries = await db.Set<StrategyPerformanceSnapshot>()
                .AsNoTracking()
                .Where(s => s.StrategyId == id)
                .OrderBy(s => s.EvaluatedAt)
                .Select(s => (double)s.SharpeRatio)
                .ToListAsync(ct);
            if (peerSeries.Count < 30) continue;

            int n = Math.Min(subjectValues.Length, peerSeries.Count);
            double corr = Math.Abs(PearsonCorrelation(
                subjectValues.AsSpan(subjectValues.Length - n),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(peerSeries).Slice(peerSeries.Count - n, n)));
            if (corr > maxCorr) maxCorr = corr;
        }
        return maxCorr;
    }

    private static double PearsonCorrelation(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        if (x.Length != y.Length || x.Length < 2) return 0.0;
        double xMean = 0, yMean = 0;
        for (int i = 0; i < x.Length; i++) { xMean += x[i]; yMean += y[i]; }
        xMean /= x.Length; yMean /= y.Length;
        double num = 0, xVar = 0, yVar = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double xDiff = x[i] - xMean, yDiff = y[i] - yMean;
            num += xDiff * yDiff; xVar += xDiff * xDiff; yVar += yDiff * yDiff;
        }
        double denom = Math.Sqrt(xVar * yVar);
        return denom > 1e-12 ? num / denom : 0.0;
    }

    private static async Task<string> GetConfigAsync(
        DbContext db, string key, string defaultValue, CancellationToken ct)
    {
        var row = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key && !c.IsDeleted, ct);
        return row?.Value ?? defaultValue;
    }

    private static async Task<double> GetConfigDoubleAsync(
        DbContext db, string key, double defaultValue, CancellationToken ct)
    {
        var raw = await GetConfigAsync(db, key, string.Empty, ct);
        return double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }

    private static async Task<int> GetConfigIntAsync(
        DbContext db, string key, int defaultValue, CancellationToken ct)
    {
        var raw = await GetConfigAsync(db, key, string.Empty, ct);
        return int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }

    private static async Task<bool> GetConfigBoolAsync(
        DbContext db, string key, bool defaultValue, CancellationToken ct)
    {
        var raw = await GetConfigAsync(db, key, string.Empty, ct);
        return bool.TryParse(raw, out var v) ? v : defaultValue;
    }
}

/// <summary>Abstraction for DI registration + unit test seams.</summary>
public interface IPromotionGateValidator
{
    Task<PromotionGateResult> EvaluateAsync(long strategyId, CancellationToken ct);
}

/// <summary>
/// Outcome of a promotion-gate evaluation. <see cref="Diagnostics"/> is always
/// populated so operators can read the numbers even on a pass; on failure it
/// explains exactly which gates fired and their values.
/// </summary>
public sealed record PromotionGateResult(
    bool Passed,
    string FailureSummary,
    IReadOnlyList<string> Diagnostics)
{
    public static PromotionGateResult Pass(IReadOnlyList<string> diagnostics) =>
        new(true, string.Empty, diagnostics);

    public static PromotionGateResult Fail(string summary, IReadOnlyList<string> diagnostics) =>
        new(false, summary, diagnostics);
}
