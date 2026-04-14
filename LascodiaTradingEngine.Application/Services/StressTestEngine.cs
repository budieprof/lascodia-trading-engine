using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Applies stress test scenarios to the current portfolio, computing P&amp;L impact,
/// margin call risk, and per-position attribution. Supports historical replay,
/// hypothetical shocks, and reverse stress testing.
/// </summary>
[RegisterService]
public class StressTestEngine : IStressTestEngine
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly ILogger<StressTestEngine> _logger;

    public StressTestEngine(
        IReadApplicationDbContext readContext,
        ILogger<StressTestEngine> logger)
    {
        _readContext = readContext;
        _logger      = logger;
    }

    public async Task<StressTestResult> RunScenarioAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken)
    {
        return scenario.ScenarioType switch
        {
            StressScenarioType.Historical   => await RunHistoricalAsync(scenario, account, openPositions, cancellationToken),
            StressScenarioType.Hypothetical => RunHypothetical(scenario, account, openPositions),
            StressScenarioType.ReverseStress => await RunReverseStressAsync(scenario, account, openPositions, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario.ScenarioType))
        };
    }

    private async Task<StressTestResult> RunHistoricalAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions,
        CancellationToken ct)
    {
        var def = JsonSerializer.Deserialize<HistoricalShockDef>(scenario.ShockDefinitionJson)
                  ?? throw new InvalidOperationException("Invalid historical shock definition");

        // Compute per-symbol returns during the historical event window
        var impacts = new List<PositionImpact>();
        decimal totalPnl = 0;

        foreach (var position in positions)
        {
            var candles = await _readContext.GetDbContext()
                .Set<Candle>()
                .Where(c => c.Symbol == position.Symbol && c.Timeframe == Timeframe.D1
                         && c.IsClosed && !c.IsDeleted
                         && c.Timestamp >= def.DateFrom && c.Timestamp <= def.DateTo)
                .OrderBy(c => c.Timestamp)
                .ToListAsync(ct);

            if (candles.Count < 2) continue;

            var startPrice = candles.First().Open;
            var endPrice   = candles.Last().Close;
            var returnPct  = startPrice != 0 ? (endPrice - startPrice) / startPrice : 0;

            decimal direction = position.Direction == PositionDirection.Long ? 1m : -1m;
            decimal pnlImpact = position.OpenLots * position.AverageEntryPrice * direction * returnPct;

            totalPnl += pnlImpact;
            impacts.Add(new PositionImpact(position.Id, position.Symbol, pnlImpact));
        }

        return BuildResult(scenario, account, positions, totalPnl, impacts);
    }

    private StressTestResult RunHypothetical(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions)
    {
        var def = JsonSerializer.Deserialize<HypotheticalShockDef>(scenario.ShockDefinitionJson)
                  ?? throw new InvalidOperationException("Invalid hypothetical shock definition");

        var shockMap = def.Shocks.ToDictionary(s => s.Symbol, s => s.PctChange);

        var impacts = new List<PositionImpact>();
        decimal totalPnl = 0;

        foreach (var position in positions)
        {
            if (!shockMap.TryGetValue(position.Symbol, out var pctChange)) continue;

            decimal direction = position.Direction == PositionDirection.Long ? 1m : -1m;
            decimal pnlImpact = position.OpenLots * position.AverageEntryPrice * direction * (pctChange / 100m);

            totalPnl += pnlImpact;
            impacts.Add(new PositionImpact(position.Id, position.Symbol, pnlImpact));
        }

        return BuildResult(scenario, account, positions, totalPnl, impacts);
    }

    /// <summary>
    /// Finds the minimum uniform shock percentage that would cause the target loss.
    /// This method intentionally ignores cross-asset correlations to produce a
    /// conservative worst-case bound: it assumes every position moves adversely by
    /// the same percentage simultaneously. In reality, correlated positions would
    /// partially offset each other (e.g. long EURUSD + short GBPUSD), so the true
    /// minimum shock required would be higher. This pessimistic approach is standard
    /// practice for reverse stress testing because underestimating tail risk is far
    /// more dangerous than overestimating it.
    /// </summary>
    private async Task<StressTestResult> RunReverseStressAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions,
        CancellationToken ct)
    {
        var def = JsonSerializer.Deserialize<ReverseStressDef>(scenario.ShockDefinitionJson)
                  ?? throw new InvalidOperationException("Invalid reverse stress definition");

        // Binary search for the minimum uniform shock that causes the target loss
        decimal targetLoss = account.Equity * (def.TargetLossPct / 100m);
        decimal low = 0m, high = 50m; // Search 0% to 50% shock
        decimal minShock = high;

        for (int iter = 0; iter < 50; iter++)
        {
            decimal mid = (low + high) / 2m;
            decimal estLoss = 0;

            foreach (var pos in positions)
            {
                // Worst-case assumes adverse move for every position:
                // longs lose on a drop, shorts lose on a rally. Use gross notional.
                // Correlations are intentionally ignored — see method summary.
                estLoss += pos.OpenLots * pos.AverageEntryPrice * (mid / 100m);
            }

            if (estLoss >= targetLoss)
            {
                minShock = mid;
                high = mid;
            }
            else
            {
                low = mid;
            }

            if (high - low < 0.01m) break;
        }

        var result = BuildResult(scenario, account, positions, -targetLoss, new List<PositionImpact>());
        result.MinimumShockPct = minShock;
        return result;
    }

    private StressTestResult BuildResult(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> positions,
        decimal stressedPnl,
        List<PositionImpact> impacts)
        => BuildResult(scenario.Id, account, positions, stressedPnl, impacts);

    /// <inheritdoc />
    public Task<StressTestResult> RunCorrelatedReverseStressAsync(
        List<Position> positions,
        TradingAccount account,
        double targetLossPct,
        double[] recentVolatilities,
        double[,] correlationMatrix,
        CancellationToken ct = default)
    {
        int n = positions.Count;
        double targetLoss = (double)account.Equity * (targetLossPct / 100.0);

        // Guard: minimum 3 positions required for meaningful correlation analysis
        if (n < 3 || recentVolatilities.Length != n ||
            correlationMatrix.GetLength(0) != n || correlationMatrix.GetLength(1) != n)
        {
            _logger.LogWarning(
                "CorrelatedReverseStress: insufficient positions ({Count}) or dimension mismatch — " +
                "falling back to uncorrelated reverse stress",
                n);
            return Task.FromResult(RunUncorrelatedFallback(positions, account, targetLossPct, recentVolatilities));
        }

        // Attempt to compute the first principal component via power iteration
        double[]? principalComponent = ComputeFirstPrincipalComponent(correlationMatrix);
        if (principalComponent is null)
        {
            _logger.LogWarning(
                "CorrelatedReverseStress: power iteration did not converge for {N}x{N} matrix — " +
                "falling back to uncorrelated reverse stress",
                n, n);
            return Task.FromResult(RunUncorrelatedFallback(positions, account, targetLossPct, recentVolatilities));
        }

        // Log principal component weights to identify which positions drive portfolio variance
        for (int i = 0; i < n; i++)
        {
            _logger.LogInformation(
                "CorrelatedReverseStress PC1: position {Symbol} (id={Id}) weight={Weight:F4}",
                positions[i].Symbol, positions[i].Id, principalComponent[i]);
        }

        // Binary search for scale factor such that |P&L| = targetLoss
        // The shock vector is: shock_i = PC1_i * sigma_i * scaleFactor
        const double scaleMin = 0.01;
        const double scaleMax = 10.0;
        const int maxIterations = 20;

        double low = scaleMin;
        double high = scaleMax;
        double bestScale = high;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            double mid = (low + high) / 2.0;
            double pnl = ApplyCorrelatedShocks(positions, principalComponent, recentVolatilities, mid);
            double absPnl = Math.Abs(pnl);

            if (absPnl >= targetLoss)
            {
                bestScale = mid;
                high = mid;
            }
            else
            {
                low = mid;
            }

            if (high - low < 0.001) break;
        }

        // Clamp scale factor
        bestScale = Math.Clamp(bestScale, scaleMin, scaleMax);

        // Compute final per-position impacts at the found scale
        var impacts = new List<PositionImpact>();
        double totalPnl = 0;
        for (int i = 0; i < n; i++)
        {
            double shock = principalComponent[i] * recentVolatilities[i] * bestScale;
            double direction = positions[i].Direction == PositionDirection.Long ? 1.0 : -1.0;
            double notional = (double)(positions[i].OpenLots * positions[i].AverageEntryPrice);
            // Worst-case: shock direction is adverse to position direction
            double pnlImpact = -Math.Abs(direction * notional * shock);
            totalPnl += pnlImpact;
            impacts.Add(new PositionImpact(positions[i].Id, positions[i].Symbol, (decimal)pnlImpact));
        }

        // Compare with uncorrelated shock magnitude to detect natural hedges
        double uncorrelatedScale = EstimateUncorrelatedScale(positions, recentVolatilities, targetLoss);
        if (uncorrelatedScale > 0 && bestScale < uncorrelatedScale * 0.5)
        {
            _logger.LogWarning(
                "CorrelatedReverseStress: correlated shock scale ({CorrelatedScale:F4}) is less than " +
                "50% of uncorrelated scale ({UncorrelatedScale:F4}) — natural hedges detected in portfolio",
                bestScale, uncorrelatedScale);
        }

        var result = BuildResult(
            scenarioId: 0, // No StressTestScenario entity; caller sets the scenario context
            account,
            positions,
            (decimal)totalPnl,
            impacts);

        result.MinimumShockPct = (decimal)(bestScale * 100.0);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Computes the first principal component (eigenvector with largest eigenvalue)
    /// of the correlation matrix using power iteration.
    /// Returns null if the iteration does not converge within 50 steps.
    /// </summary>
    internal static double[]? ComputeFirstPrincipalComponent(double[,] correlationMatrix)
    {
        int n = correlationMatrix.GetLength(0);
        if (n == 0) return null;

        // Initialize with uniform vector
        var v = new double[n];
        double initVal = 1.0 / Math.Sqrt(n);
        for (int i = 0; i < n; i++)
            v[i] = initVal;

        const int maxIterations = 50;
        const double convergenceThreshold = 1e-8;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            // Matrix-vector multiply: w = R * v
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j < n; j++)
                    sum += correlationMatrix[i, j] * v[j];
                w[i] = sum;
            }

            // Compute norm
            double norm = 0;
            for (int i = 0; i < n; i++)
                norm += w[i] * w[i];
            norm = Math.Sqrt(norm);

            if (norm < 1e-15) return null; // Degenerate matrix

            // Normalize
            for (int i = 0; i < n; i++)
                w[i] /= norm;

            // Check convergence: ||w - v||
            double diff = 0;
            for (int i = 0; i < n; i++)
                diff += (w[i] - v[i]) * (w[i] - v[i]);

            v = w;

            if (Math.Sqrt(diff) < convergenceThreshold)
                return v;
        }

        // Did not converge within 50 iterations — return null to trigger fallback
        return null;
    }

    /// <summary>
    /// Applies correlated shocks to positions and returns the total portfolio P&amp;L.
    /// shock_i = principalComponent_i * volatility_i * scaleFactor
    /// P&amp;L = sum_i( -|direction_i * notional_i * shock_i| )  (worst-case adverse)
    /// </summary>
    internal static double ApplyCorrelatedShocks(
        List<Position> positions,
        double[] shockVector,
        double[] volatilities,
        double scaleFactor)
    {
        double totalPnl = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            double shock = shockVector[i] * volatilities[i] * scaleFactor;
            double direction = positions[i].Direction == PositionDirection.Long ? 1.0 : -1.0;
            double notional = (double)(positions[i].OpenLots * positions[i].AverageEntryPrice);
            // Worst-case: each position moves adversely
            totalPnl += -Math.Abs(direction * notional * shock);
        }

        return totalPnl;
    }

    /// <summary>
    /// Estimates the uniform shock scale factor for an uncorrelated reverse stress test.
    /// Used to compare against the correlated scale for hedge detection.
    /// </summary>
    private static double EstimateUncorrelatedScale(
        List<Position> positions,
        double[] volatilities,
        double targetLoss)
    {
        // Total adverse exposure per unit scale: sum_i |notional_i * sigma_i|
        double totalExposure = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            double notional = (double)(positions[i].OpenLots * positions[i].AverageEntryPrice);
            totalExposure += Math.Abs(notional * volatilities[i]);
        }

        return totalExposure > 0 ? targetLoss / totalExposure : 0;
    }

    /// <summary>
    /// Fallback when correlated stress test cannot be performed (insufficient positions,
    /// non-convergent eigenvector, or invalid matrix). Applies uniform adverse shocks
    /// scaled by each position's own volatility.
    /// </summary>
    private StressTestResult RunUncorrelatedFallback(
        List<Position> positions,
        TradingAccount account,
        double targetLossPct,
        double[] recentVolatilities)
    {
        double targetLoss = (double)account.Equity * (targetLossPct / 100.0);

        // Binary search for scale factor
        const double scaleMin = 0.01;
        const double scaleMax = 10.0;
        const int maxIterations = 20;

        double low = scaleMin;
        double high = scaleMax;
        double bestScale = high;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            double mid = (low + high) / 2.0;
            double absPnl = 0;

            for (int i = 0; i < positions.Count; i++)
            {
                double vol = i < recentVolatilities.Length ? recentVolatilities[i] : 0.01;
                double notional = (double)(positions[i].OpenLots * positions[i].AverageEntryPrice);
                absPnl += Math.Abs(notional * vol * mid);
            }

            if (absPnl >= targetLoss)
            {
                bestScale = mid;
                high = mid;
            }
            else
            {
                low = mid;
            }

            if (high - low < 0.001) break;
        }

        bestScale = Math.Clamp(bestScale, scaleMin, scaleMax);

        var impacts = new List<PositionImpact>();
        double totalPnl = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            double vol = i < recentVolatilities.Length ? recentVolatilities[i] : 0.01;
            double notional = (double)(positions[i].OpenLots * positions[i].AverageEntryPrice);
            double pnlImpact = -Math.Abs(notional * vol * bestScale);
            totalPnl += pnlImpact;
            impacts.Add(new PositionImpact(positions[i].Id, positions[i].Symbol, (decimal)pnlImpact));
        }

        var result = BuildResult(0, account, positions, (decimal)totalPnl, impacts);
        result.MinimumShockPct = (decimal)(bestScale * 100.0);

        return result;
    }

    private StressTestResult BuildResult(
        long scenarioId,
        TradingAccount account,
        IReadOnlyList<Position> positions,
        decimal stressedPnl,
        List<PositionImpact> impacts)
    {
        var stressedPnlPct = account.Equity > 0 ? stressedPnl / account.Equity * 100m : 0;
        var postStressEquity = account.Equity + stressedPnl;

        var totalMargin = positions.Sum(p => p.OpenLots * p.AverageEntryPrice / 100m);
        var wouldTriggerMarginCall = postStressEquity < totalMargin;

        return new StressTestResult
        {
            StressTestScenarioId  = scenarioId,
            TradingAccountId      = account.Id,
            PortfolioEquity       = account.Equity,
            StressedPnl           = stressedPnl,
            StressedPnlPct        = stressedPnlPct,
            WouldTriggerMarginCall = wouldTriggerMarginCall,
            PositionImpactsJson   = JsonSerializer.Serialize(impacts),
            ExecutedAt            = DateTime.UtcNow
        };
    }

    // ── DTOs for shock definition parsing ──

    private record HistoricalShockDef(DateTime DateFrom, DateTime DateTo);
    private record HypotheticalShockDef(List<SymbolShock> Shocks, decimal SpreadMultiplier = 1);
    private record SymbolShock(string Symbol, decimal PctChange);
    private record ReverseStressDef(decimal TargetLossPct, List<string> SearchSymbols);
    private record PositionImpact(long PositionId, string Symbol, decimal PnlImpact);
}
