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

        return new PortfolioRiskMetrics(var95, var99, cvar95, cvar99, stressedVaR, herfindahl, mcVaR95, mcVaR99, mcCVaR95);
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
        var index = (int)Math.Floor(percentile * (sortedValues.Length - 1));
        index = Math.Clamp(index, 0, sortedValues.Length - 1);
        return sortedValues[index];
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
                corrMatrix[i, j] = corr;
                corrMatrix[j, i] = corr;
            }
        }

        // 3. Cholesky decomposition: L such that L*L^T = corrMatrix
        var choleskyL = CholeskyDecomposition(corrMatrix, n);
        if (choleskyL is null)
        {
            _logger.LogWarning("Monte Carlo VaR: Cholesky decomposition failed (non-positive-definite matrix), skipping");
            return (0, 0, 0);
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
        if (varX <= 0 || varY <= 0) return 0;

        double denominator = Math.Sqrt(varX * varY);
        return denominator == 0 ? 0 : (n * sumXY - sumX * sumY) / denominator;
    }
}
