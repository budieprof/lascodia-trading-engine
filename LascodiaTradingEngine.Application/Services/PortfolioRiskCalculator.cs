using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Computes portfolio-level VaR (Value at Risk) and CVaR (Expected Shortfall) using
/// historical simulation. Supports marginal VaR for proposed new positions.
/// </summary>
[RegisterService]
public class PortfolioRiskCalculator : IPortfolioRiskCalculator
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly PortfolioRiskOptions _options;
    private readonly ILogger<PortfolioRiskCalculator> _logger;

    private readonly IStressTestEngine _stressTestEngine;

    public PortfolioRiskCalculator(
        IReadApplicationDbContext readContext,
        PortfolioRiskOptions options,
        IStressTestEngine stressTestEngine,
        ILogger<PortfolioRiskCalculator> logger)
    {
        _readContext      = readContext;
        _options          = options;
        _stressTestEngine = stressTestEngine;
        _logger           = logger;
    }

    public async Task<PortfolioRiskMetrics> ComputeAsync(
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken)
    {
        if (openPositions.Count == 0)
        {
            return PortfolioRiskMetrics.Empty;
        }

        // Gather distinct symbols from open positions
        var symbols = openPositions.Select(p => p.Symbol).Distinct().ToList();

        // Fetch historical daily returns for each symbol
        var returnsBySymbol = new Dictionary<string, List<decimal>>();
        var ctx = _readContext.GetDbContext();

        foreach (var symbol in symbols)
        {
            var candles = await ctx.Set<Candle>()
                .Where(c => c.Symbol == symbol && c.Timeframe == Domain.Enums.Timeframe.D1
                         && c.IsClosed && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(_options.ReturnWindowDays + 1)
                .OrderBy(c => c.Timestamp)
                .Select(c => c.Close)
                .ToListAsync(cancellationToken);

            var returns = new List<decimal>();
            for (int i = 1; i < candles.Count; i++)
            {
                if (candles[i - 1] != 0)
                    returns.Add((candles[i] - candles[i - 1]) / candles[i - 1]);
            }
            returnsBySymbol[symbol] = returns;
        }

        // Compute portfolio P&L scenarios using historical returns
        if (returnsBySymbol.Count == 0 || returnsBySymbol.Values.Any(r => r.Count == 0))
        {
            _logger.LogWarning("No return history available for VaR computation");
            return PortfolioRiskMetrics.Empty;
        }

        int scenarioCount = returnsBySymbol.Values.Min(r => r.Count);
        if (scenarioCount < 10)
        {
            _logger.LogWarning("Insufficient return history ({Count} days) for VaR computation", scenarioCount);
            return PortfolioRiskMetrics.Empty;
        }

        var portfolioPnls = new decimal[scenarioCount];

        foreach (var position in openPositions)
        {
            if (!returnsBySymbol.TryGetValue(position.Symbol, out var returns)) continue;

            // Position P&L = notional × direction × return
            decimal direction = position.Direction == Domain.Enums.PositionDirection.Long ? 1m : -1m;
            decimal notional  = position.OpenLots * position.AverageEntryPrice;

            for (int i = 0; i < scenarioCount && i < returns.Count; i++)
            {
                portfolioPnls[i] += notional * direction * returns[i];
            }
        }

        // Sort P&L scenarios ascending (worst to best)
        Array.Sort(portfolioPnls);

        // VaR: the loss at the given percentile
        decimal var95  = -GetPercentile(portfolioPnls, 1.0m - _options.VaRConfidence95);
        decimal var99  = -GetPercentile(portfolioPnls, 1.0m - _options.VaRConfidence99);

        // CVaR: average of losses beyond VaR
        decimal cvar95 = ComputeCVaR(portfolioPnls, 1.0m - _options.VaRConfidence95);
        decimal cvar99 = ComputeCVaR(portfolioPnls, 1.0m - _options.VaRConfidence99);

        // Stressed VaR: compute from named stress test scenarios if available, else worst 1% historical
        decimal stressedVaR = await ComputeStressedVaRAsync(account, openPositions, portfolioPnls, cancellationToken);

        // Correlation concentration: Herfindahl index of position weights
        decimal totalNotional = openPositions.Sum(p => p.OpenLots * p.AverageEntryPrice);
        decimal herfindahl = 0;
        if (totalNotional > 0)
        {
            foreach (var position in openPositions)
            {
                var weight = position.OpenLots * position.AverageEntryPrice / totalNotional;
                herfindahl += weight * weight;
            }
        }

        // Monte Carlo VaR (if enabled via options)
        decimal mcVaR95 = 0, mcVaR99 = 0, mcCVaR95 = 0;
        if (_options.MonteCarloSimulations > 0)
        {
            (mcVaR95, mcVaR99, mcCVaR95) = ComputeMonteCarloVaR(returnsBySymbol, openPositions, symbols);
        }

        // EVT tail-risk VaR via Generalized Pareto Distribution
        var losses = portfolioPnls.Select(p => (double)(-p)).ToArray();
        var (evtVaR95, evtVaR99, evtCVaR99, gpdShape, gpdScale) =
            ComputeEvtVaR(losses, (double)var95, (double)var99);

        // Use the most conservative (largest) VaR estimate across all methods
        decimal finalVaR95 = Math.Max(var95, Math.Max(mcVaR95, (decimal)evtVaR95));
        decimal finalVaR99 = Math.Max(var99, Math.Max(mcVaR99, (decimal)evtVaR99));

        return new PortfolioRiskMetrics(
            finalVaR95, finalVaR99, cvar95, cvar99, stressedVaR, herfindahl,
            mcVaR95, mcVaR99, mcCVaR95,
            (decimal)evtVaR95, (decimal)evtVaR99, (decimal)evtCVaR99,
            (decimal)gpdShape, (decimal)gpdScale);
    }

    public async Task<MarginalVaRResult> ComputeMarginalAsync(
        TradeSignal proposedSignal,
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken)
    {
        // Current portfolio VaR
        var currentMetrics = await ComputeAsync(account, openPositions, cancellationToken);

        // Create synthetic position from proposed signal
        var synthetic = new Position
        {
            Symbol     = proposedSignal.Symbol,
            Direction  = proposedSignal.Direction == Domain.Enums.TradeDirection.Buy
                ? Domain.Enums.PositionDirection.Long
                : Domain.Enums.PositionDirection.Short,
            OpenLots       = proposedSignal.SuggestedLotSize,
            AverageEntryPrice = proposedSignal.EntryPrice
        };

        // Portfolio + proposed position VaR
        var expandedPositions = openPositions.Append(synthetic).ToList();
        var postTradeMetrics  = await ComputeAsync(account, expandedPositions, cancellationToken);

        var marginalVaR = postTradeMetrics.VaR95 - currentMetrics.VaR95;
        var wouldBreach = account.Equity > 0
            && postTradeMetrics.VaR95 / account.Equity * 100m > _options.MaxVaR95Pct;

        return new MarginalVaRResult(marginalVaR, postTradeMetrics.VaR95, wouldBreach);
    }

    private static decimal GetPercentile(decimal[] sortedValues, decimal percentile)
    {
        if (sortedValues.Length == 0) return 0;
        decimal idx = percentile * (sortedValues.Length - 1);
        int lo = Math.Clamp((int)Math.Floor(idx), 0, sortedValues.Length - 1);
        int hi = Math.Min(lo + 1, sortedValues.Length - 1);
        decimal frac = idx - lo;
        return sortedValues[lo] * (1m - frac) + sortedValues[hi] * frac;
    }

    private static decimal ComputeCVaR(decimal[] sortedPnls, decimal tailPct)
    {
        int tailCount = Math.Max(1, (int)Math.Floor(tailPct * sortedPnls.Length));
        decimal sum = 0;
        for (int i = 0; i < tailCount; i++)
            sum += sortedPnls[i];
        return -(sum / tailCount);
    }

    /// <summary>
    /// Computes Monte Carlo VaR/CVaR by generating correlated random return scenarios
    /// using Cholesky decomposition of the Pearson correlation matrix.
    /// </summary>
    private (decimal VaR95, decimal VaR99, decimal CVaR95) ComputeMonteCarloVaR(
        Dictionary<string, List<decimal>> returnsBySymbol,
        IReadOnlyList<Position> openPositions,
        List<string> symbols)
    {
        int n = symbols.Count;
        int simulations = _options.MonteCarloSimulations;

        // 1. Estimate mean return and std dev per symbol
        var means  = new double[n];
        var stdDevs = new double[n];
        for (int i = 0; i < n; i++)
        {
            var returns = returnsBySymbol[symbols[i]];
            double mean = returns.Average(r => (double)r);
            double variance = returns.Sum(r => ((double)r - mean) * ((double)r - mean)) / Math.Max(returns.Count - 1, 1);
            means[i]   = mean;
            stdDevs[i] = Math.Sqrt(variance);
        }

        // 2. Compute Pearson correlation matrix
        var corrMatrix = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            corrMatrix[i, i] = 1.0;
            for (int j = i + 1; j < n; j++)
            {
                var r1 = returnsBySymbol[symbols[i]];
                var r2 = returnsBySymbol[symbols[j]];
                int len = Math.Min(r1.Count, r2.Count);
                if (len < 2)
                {
                    corrMatrix[i, j] = 0;
                    corrMatrix[j, i] = 0;
                    continue;
                }
                double corr = PearsonCorrelationDouble(
                    r1.TakeLast(len).Select(x => (double)x).ToArray(),
                    r2.TakeLast(len).Select(x => (double)x).ToArray());
                if (double.IsNaN(corr))
                {
                    _logger.LogWarning(
                        "Zero variance detected for {SymbolA}/{SymbolB} — excluded from correlation matrix",
                        symbols[i], symbols[j]);
                    corr = 0;
                }
                corrMatrix[i, j] = corr;
                corrMatrix[j, i] = corr;
            }
        }

        // 3. Cholesky decomposition: L such that L*L^T = corrMatrix
        var choleskyL = CholeskyDecomposition(corrMatrix, n);
        if (choleskyL is null)
        {
            // Ridge-regularize: add small diagonal to make the matrix positive-definite
            _logger.LogWarning("Monte Carlo VaR: Cholesky decomposition failed — applying ridge regularization (0.01 diagonal)");
            for (int i = 0; i < n; i++)
                corrMatrix[i, i] += 0.01;

            choleskyL = CholeskyDecomposition(corrMatrix, n);
            if (choleskyL is null)
            {
                _logger.LogWarning("Monte Carlo VaR: Cholesky decomposition still failed after ridge regularization, skipping");
                return (0, 0, 0);
            }
        }

        // 4. Generate N correlated random return scenarios and compute portfolio P&L
        var rng = _options.MonteCarloSeed.HasValue ? new Random(_options.MonteCarloSeed.Value) : Random.Shared;
        var simulatedPnls = new decimal[simulations];

        // Pre-compute symbol index lookup and position notionals to avoid O(N) lookups per simulation
        var symbolIndex = new Dictionary<string, int>(n);
        for (int i = 0; i < n; i++)
            symbolIndex[symbols[i]] = i;

        var positionData = openPositions
            .Select(p => (
                SymbolIdx: symbolIndex.TryGetValue(p.Symbol, out var idx) ? idx : -1,
                Direction: p.Direction == Domain.Enums.PositionDirection.Long ? 1m : -1m,
                Notional: p.OpenLots * p.AverageEntryPrice))
            .Where(p => p.SymbolIdx >= 0)
            .ToArray();

        for (int sim = 0; sim < simulations; sim++)
        {
            // Generate n independent standard normal variates
            var z = new double[n];
            for (int i = 0; i < n; i++)
                z[i] = BoxMullerNormal(rng);

            // Correlate: x = L * z
            var correlatedReturns = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j <= i; j++)
                    sum += choleskyL[i, j] * z[j];
                // Scale by std dev and shift by mean
                correlatedReturns[i] = means[i] + stdDevs[i] * sum;
            }

            // 5. Compute portfolio P&L for this scenario
            decimal scenarioPnl = 0;
            foreach (var (symbolIdx, direction, notional) in positionData)
            {
                scenarioPnl += notional * direction * (decimal)correlatedReturns[symbolIdx];
            }
            simulatedPnls[sim] = scenarioPnl;
        }

        // 6. Sort and extract VaR/CVaR from the simulated distribution
        Array.Sort(simulatedPnls);

        decimal var95  = -GetPercentile(simulatedPnls, 1.0m - _options.VaRConfidence95);
        decimal var99  = -GetPercentile(simulatedPnls, 1.0m - _options.VaRConfidence99);
        decimal cvar95 = ComputeCVaR(simulatedPnls, 1.0m - _options.VaRConfidence95);

        return (var95, var99, cvar95);
    }

    /// <summary>
    /// Computes stressed VaR by loading active stress test scenarios from the database,
    /// applying their shock definitions via <see cref="IStressTestEngine"/>, and returning
    /// the worst-case loss. Falls back to the historical 1st percentile if no scenarios exist.
    /// </summary>
    private async Task<decimal> ComputeStressedVaRAsync(
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        decimal[] sortedHistoricalPnls,
        CancellationToken cancellationToken)
    {
        // Fallback: worst 1% of historical scenarios
        decimal historicalStressedVaR = -GetPercentile(sortedHistoricalPnls, 0.01m);

        try
        {
            var ctx = _readContext.GetDbContext();
            var activeScenarios = await ctx.Set<StressTestScenario>()
                .Where(s => s.IsActive && !s.IsDeleted)
                .ToListAsync(cancellationToken);

            if (activeScenarios.Count == 0)
                return historicalStressedVaR;

            decimal worstCaseLoss = historicalStressedVaR;

            foreach (var scenario in activeScenarios)
            {
                try
                {
                    var result = await _stressTestEngine.RunScenarioAsync(scenario, account, openPositions, cancellationToken);
                    // StressedPnl is negative for losses; stressed VaR is a positive loss figure
                    decimal scenarioLoss = -result.StressedPnl;
                    if (scenarioLoss > worstCaseLoss)
                        worstCaseLoss = scenarioLoss;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "StressedVaR: failed to run scenario {ScenarioId} ({Name}), skipping",
                        scenario.Id, scenario.Name);
                }
            }

            return worstCaseLoss;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StressedVaR: failed to load scenarios, using historical fallback");
            return historicalStressedVaR;
        }
    }

    /// <summary>
    /// Cholesky decomposition of a symmetric positive-definite matrix.
    /// Returns the lower-triangular matrix L such that A = L * L^T, or null if not positive-definite.
    /// </summary>
    private static double[,]? CholeskyDecomposition(double[,] matrix, int n)
    {
        var L = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double sum = 0;
                for (int k = 0; k < j; k++)
                    sum += L[i, k] * L[j, k];

                if (i == j)
                {
                    double diag = matrix[i, i] - sum;
                    if (diag <= 0) return null; // Not positive-definite
                    L[i, j] = Math.Sqrt(diag);
                }
                else
                {
                    L[i, j] = (matrix[i, j] - sum) / L[j, j];
                }
            }
        }
        return L;
    }

    /// <summary>
    /// Generates a standard normal variate using the Box-Muller transform.
    /// </summary>
    private static double BoxMullerNormal(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(); // Uniform(0,1] — avoid log(0)
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Extreme Value Theory (EVT) — Generalized Pareto Distribution tail fit
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes VaR using Extreme Value Theory (Generalized Pareto Distribution)
    /// fitted to the tail of the loss distribution. Captures fat-tail risk that
    /// historical and Monte Carlo methods underestimate.
    /// Returns (EvtVaR95, EvtVaR99, EvtCVaR99, GpdShape, GpdScale).
    /// Falls back to historical VaR if EVT fit fails (insufficient exceedances, invalid parameters).
    /// </summary>
    internal (double EvtVaR95, double EvtVaR99, double EvtCVaR99, double GpdShape, double GpdScale)
        ComputeEvtVaR(double[] losses, double historicalVaR95, double historicalVaR99)
    {
        const int MinExceedances = 20;
        const double ThresholdPercentile = 0.90;
        const double MaxEvtToHistoricalRatio = 5.0;
        const double CapEvtToHistoricalRatio = 3.0;

        // Sort losses ascending to compute threshold
        var sortedLosses = (double[])losses.Clone();
        Array.Sort(sortedLosses);

        int n = sortedLosses.Length;
        if (n < MinExceedances + 10)
        {
            _logger.LogDebug("EVT VaR: insufficient data ({Count} observations), falling back to historical", n);
            return (historicalVaR95, historicalVaR99, historicalVaR99, 0, 0);
        }

        // Threshold at the 90th percentile of losses
        double threshold = GetPercentileDouble(sortedLosses, ThresholdPercentile);

        // Extract exceedances (losses strictly above threshold)
        var exceedances = sortedLosses.Where(l => l > threshold).Select(l => l - threshold).ToArray();
        int nu = exceedances.Length;

        if (nu < MinExceedances)
        {
            _logger.LogDebug(
                "EVT VaR: only {Count} exceedances above threshold {Threshold:F6} (need {Min}), falling back to historical",
                nu, threshold, MinExceedances);
            return (historicalVaR95, historicalVaR99, historicalVaR99, 0, 0);
        }

        // Fit GPD parameters via maximum likelihood (grid search + refinement)
        var (xi, sigma, fitSuccess) = FitGpd(exceedances);

        if (!fitSuccess)
        {
            _logger.LogWarning("EVT VaR: GPD fit failed, falling back to historical VaR");
            return (historicalVaR95, historicalVaR99, historicalVaR99, 0, 0);
        }

        // Compute EVT VaR at 95% and 99% confidence
        double evtVaR95 = GpdQuantile(0.95, xi, sigma, threshold, n, nu);
        double evtVaR99 = GpdQuantile(0.99, xi, sigma, threshold, n, nu);
        double evtCVaR99 = GpdCVaR(0.99, xi, sigma, threshold, n, nu);

        // Guard rail: cap absurd estimates from poor fits
        evtVaR95 = ApplyEvtCap(evtVaR95, historicalVaR95, MaxEvtToHistoricalRatio, CapEvtToHistoricalRatio, "95%");
        evtVaR99 = ApplyEvtCap(evtVaR99, historicalVaR99, MaxEvtToHistoricalRatio, CapEvtToHistoricalRatio, "99%");
        evtCVaR99 = ApplyEvtCap(evtCVaR99, historicalVaR99, MaxEvtToHistoricalRatio, CapEvtToHistoricalRatio, "CVaR99");

        // Ensure non-negative
        evtVaR95 = Math.Max(0, evtVaR95);
        evtVaR99 = Math.Max(0, evtVaR99);
        evtCVaR99 = Math.Max(0, evtCVaR99);

        _logger.LogDebug(
            "EVT VaR computed: VaR95={VaR95:F4}, VaR99={VaR99:F4}, CVaR99={CVaR99:F4}, xi={Xi:F4}, sigma={Sigma:F6}, threshold={Threshold:F6}, exceedances={Nu}",
            evtVaR95, evtVaR99, evtCVaR99, xi, sigma, threshold, nu);

        return (evtVaR95, evtVaR99, evtCVaR99, xi, sigma);
    }

    /// <summary>
    /// Fits Generalized Pareto Distribution parameters (xi, sigma) to exceedance data
    /// using maximum likelihood estimation via grid search with refinement.
    /// </summary>
    internal static (double Xi, double Sigma, bool Success) FitGpd(double[] exceedances)
    {
        const double XiMin = -0.5;
        const double XiMax = 1.0;
        const double XiStep = 0.05;
        const int SigmaGridPoints = 20;
        const double SigmaMinFraction = 0.001;

        int nu = exceedances.Length;
        if (nu == 0) return (0, 0, false);

        double exceedStd = StandardDeviation(exceedances);
        double exceedMean = exceedances.Average();
        double sigmaMax = Math.Max(3.0 * exceedStd, exceedMean * 3.0);
        double sigmaMin = Math.Max(SigmaMinFraction, exceedMean * 0.01);

        double bestXi = 0;
        double bestSigma = exceedStd;
        double bestLogLik = double.NegativeInfinity;

        // Phase 1: Coarse grid search
        for (double xi = XiMin; xi <= XiMax; xi += XiStep)
        {
            for (int si = 0; si < SigmaGridPoints; si++)
            {
                double sigma = sigmaMin + (sigmaMax - sigmaMin) * si / (SigmaGridPoints - 1);
                double logLik = GpdLogLikelihood(exceedances, xi, sigma);
                if (logLik > bestLogLik)
                {
                    bestLogLik = logLik;
                    bestXi = xi;
                    bestSigma = sigma;
                }
            }
        }

        // Phase 2: Nelder-Mead simplex refinement around the best grid point
        (bestXi, bestSigma, bestLogLik) = NelderMeadGpd(exceedances, bestXi, bestSigma, bestLogLik);

        // Validate parameters
        if (bestXi < XiMin || bestXi > XiMax)
            return (0, 0, false);

        if (bestSigma <= 0)
            return (0, 0, false);

        if (double.IsNaN(bestLogLik) || double.IsInfinity(bestLogLik))
            return (0, 0, false);

        return (bestXi, bestSigma, true);
    }

    /// <summary>
    /// Nelder-Mead simplex optimization for GPD log-likelihood.
    /// Operates in the (xi, sigma) parameter space.
    /// </summary>
    private static (double Xi, double Sigma, double LogLik) NelderMeadGpd(
        double[] exceedances, double initXi, double initSigma, double initLogLik)
    {
        const int MaxIterations = 200;
        const double Tolerance = 1e-8;
        const double Alpha = 1.0;  // reflection
        const double Gamma = 2.0;  // expansion
        const double Rho = 0.5;    // contraction
        const double SigmaS = 0.5; // shrink

        // Initialize simplex: 3 vertices for 2 parameters
        var vertices = new (double xi, double sigma, double logLik)[3];
        vertices[0] = (initXi, initSigma, initLogLik);
        vertices[1] = (initXi + 0.05, initSigma, GpdLogLikelihood(exceedances, initXi + 0.05, initSigma));
        vertices[2] = (initXi, initSigma * 1.1, GpdLogLikelihood(exceedances, initXi, initSigma * 1.1));

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            // Sort: we maximize, so best = highest log-likelihood
            Array.Sort(vertices, (a, b) => b.logLik.CompareTo(a.logLik));

            // Check convergence
            double range = vertices[0].logLik - vertices[2].logLik;
            if (range < Tolerance) break;

            // Centroid of all points except worst
            double cXi = (vertices[0].xi + vertices[1].xi) / 2.0;
            double cSigma = (vertices[0].sigma + vertices[1].sigma) / 2.0;

            // Reflection
            double rXi = cXi + Alpha * (cXi - vertices[2].xi);
            double rSigma = cSigma + Alpha * (cSigma - vertices[2].sigma);
            rSigma = Math.Max(rSigma, 1e-10);
            double rLogLik = GpdLogLikelihood(exceedances, rXi, rSigma);

            if (rLogLik > vertices[0].logLik)
            {
                // Expansion
                double eXi = cXi + Gamma * (rXi - cXi);
                double eSigma = cSigma + Gamma * (rSigma - cSigma);
                eSigma = Math.Max(eSigma, 1e-10);
                double eLogLik = GpdLogLikelihood(exceedances, eXi, eSigma);
                vertices[2] = eLogLik > rLogLik ? (eXi, eSigma, eLogLik) : (rXi, rSigma, rLogLik);
            }
            else if (rLogLik > vertices[1].logLik)
            {
                vertices[2] = (rXi, rSigma, rLogLik);
            }
            else
            {
                // Contraction
                double worstXi = rLogLik > vertices[2].logLik ? rXi : vertices[2].xi;
                double worstSigma = rLogLik > vertices[2].logLik ? rSigma : vertices[2].sigma;

                double contrXi = cXi + Rho * (worstXi - cXi);
                double contrSigma = Math.Max(cSigma + Rho * (worstSigma - cSigma), 1e-10);
                double contrLogLik = GpdLogLikelihood(exceedances, contrXi, contrSigma);

                if (contrLogLik > vertices[2].logLik)
                {
                    vertices[2] = (contrXi, contrSigma, contrLogLik);
                }
                else
                {
                    // Shrink toward best
                    for (int i = 1; i < 3; i++)
                    {
                        vertices[i].xi = vertices[0].xi + SigmaS * (vertices[i].xi - vertices[0].xi);
                        vertices[i].sigma = Math.Max(vertices[0].sigma + SigmaS * (vertices[i].sigma - vertices[0].sigma), 1e-10);
                        vertices[i].logLik = GpdLogLikelihood(exceedances, vertices[i].xi, vertices[i].sigma);
                    }
                }
            }
        }

        Array.Sort(vertices, (a, b) => b.logLik.CompareTo(a.logLik));
        return (vertices[0].xi, vertices[0].sigma, vertices[0].logLik);
    }

    /// <summary>
    /// GPD log-likelihood: L(xi,sigma) = -N*ln(sigma) - (1+1/xi) * sum(ln(1 + xi*x_i/sigma))
    /// For xi near zero, uses the exponential distribution log-likelihood.
    /// </summary>
    internal static double GpdLogLikelihood(double[] exceedances, double xi, double sigma)
    {
        if (sigma <= 0) return double.NegativeInfinity;

        int nu = exceedances.Length;

        // Near-zero xi: exponential distribution
        if (Math.Abs(xi) < 1e-8)
        {
            double logLik = -nu * Math.Log(sigma);
            for (int i = 0; i < nu; i++)
                logLik -= exceedances[i] / sigma;
            return logLik;
        }

        double logLik2 = -nu * Math.Log(sigma);
        double factor = 1.0 + 1.0 / xi;

        for (int i = 0; i < nu; i++)
        {
            double z = 1.0 + xi * exceedances[i] / sigma;
            if (z <= 0) return double.NegativeInfinity; // Invalid: outside GPD support
            logLik2 -= factor * Math.Log(z);
        }

        return logLik2;
    }

    /// <summary>
    /// Computes the VaR quantile from the fitted GPD tail model.
    /// VaR_alpha = u + (sigma/xi) * [((n/nu) * (1-alpha))^(-xi) - 1]
    /// </summary>
    internal static double GpdQuantile(double alpha, double xi, double sigma, double threshold, int n, int nu)
    {
        if (nu <= 0 || n <= 0) return threshold;

        double tailProb = (1.0 - alpha);
        double ratio = (double)n / nu;

        // Near-zero xi: exponential tail — limit of (sigma/xi)*[((n/nu)*(1-alpha))^(-xi) - 1]
        // as xi→0 is -sigma * ln((n/nu)*(1-alpha))
        if (Math.Abs(xi) < 1e-8)
        {
            double arg = ratio * tailProb;
            return arg > 0 ? threshold - sigma * Math.Log(arg) : threshold;
        }

        double exponent = Math.Pow(ratio * tailProb, -xi);
        return threshold + (sigma / xi) * (exponent - 1.0);
    }

    /// <summary>
    /// Computes CVaR (Expected Shortfall) from the fitted GPD tail model.
    /// CVaR_alpha = VaR_alpha / (1 - xi) + (sigma - xi * u) / (1 - xi)
    /// Valid only when xi &lt; 1.
    /// </summary>
    internal static double GpdCVaR(double alpha, double xi, double sigma, double threshold, int n, int nu)
    {
        if (xi >= 1.0)
        {
            // Infinite mean case: return VaR as lower bound
            return GpdQuantile(alpha, xi, sigma, threshold, n, nu);
        }

        double var = GpdQuantile(alpha, xi, sigma, threshold, n, nu);
        double excess = var - threshold;

        // CVaR = VaR/(1-xi) + (sigma - xi*threshold)/(1-xi)
        // Equivalently: CVaR = (VaR + sigma - xi*threshold) / (1-xi)
        // But more standard: ES = VaR/(1-xi) + (sigma - xi*u)/(1-xi)
        // where u is the threshold (already subtracted in excess formulation)
        // For the full-scale VaR: ES_alpha = VaR_alpha/(1-xi) + (sigma - xi*threshold)/(1-xi)
        return var / (1.0 - xi) + (sigma - xi * threshold) / (1.0 - xi);
    }

    /// <summary>
    /// Caps EVT VaR estimates that are unreasonably large relative to historical VaR,
    /// which indicates a poor GPD fit.
    /// </summary>
    private double ApplyEvtCap(double evtValue, double historicalValue, double maxRatio, double capRatio, string label)
    {
        if (historicalValue <= 0) return evtValue;

        double ratio = evtValue / historicalValue;
        if (ratio > maxRatio)
        {
            double capped = historicalValue * capRatio;
            _logger.LogWarning(
                "EVT {Label} is {Ratio:F1}x historical ({EvtValue:F4} vs {HistValue:F4}), capping at {CapRatio:F0}x = {Capped:F4}",
                label, ratio, evtValue, historicalValue, capRatio, capped);
            return capped;
        }
        return evtValue;
    }

    /// <summary>
    /// Computes the percentile of a sorted double array using linear interpolation.
    /// </summary>
    private static double GetPercentileDouble(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        double idx = percentile * (sortedValues.Length - 1);
        int lo = Math.Clamp((int)Math.Floor(idx), 0, sortedValues.Length - 1);
        int hi = Math.Min(lo + 1, sortedValues.Length - 1);
        double frac = idx - lo;
        return sortedValues[lo] * (1.0 - frac) + sortedValues[hi] * frac;
    }

    /// <summary>
    /// Computes sample standard deviation of a double array.
    /// </summary>
    private static double StandardDeviation(double[] values)
    {
        if (values.Length <= 1) return 0;
        double mean = values.Average();
        double sumSq = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSq / (values.Length - 1));
    }

    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pearson correlation for double arrays (used by Monte Carlo path).
    /// </summary>
    private static double PearsonCorrelationDouble(double[] x, double[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n == 0) return 0;

        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += x[i];
            sumY  += y[i];
            sumXY += x[i] * y[i];
            sumX2 += x[i] * x[i];
            sumY2 += y[i] * y[i];
        }

        double varX = n * sumX2 - sumX * sumX;
        double varY = n * sumY2 - sumY * sumY;
        if (varX <= 0 || varY <= 0)
        {
            // Zero-variance series cannot produce meaningful correlation — return NaN
            // to signal callers that this pair should be excluded from the correlation matrix.
            return double.NaN;
        }

        double denominator = Math.Sqrt(varX * varY);
        return denominator == 0 ? 0 : (n * sumXY - sumX * sumY) / denominator;
    }
}
