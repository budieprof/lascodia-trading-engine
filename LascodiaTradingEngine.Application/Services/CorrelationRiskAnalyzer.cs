using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Computes rolling Pearson correlation matrix from daily returns and evaluates
/// portfolio concentration risk using the Herfindahl index on correlated exposure.
/// </summary>
[RegisterService]
public class CorrelationRiskAnalyzer : ICorrelationRiskAnalyzer
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly ILogger<CorrelationRiskAnalyzer> _logger;

    public CorrelationRiskAnalyzer(
        IReadApplicationDbContext readContext,
        ILogger<CorrelationRiskAnalyzer> logger)
    {
        _readContext = readContext;
        _logger      = logger;
    }

    public async Task<CorrelationRiskResult> EvaluateAsync(
        TradeSignal proposedSignal,
        IReadOnlyList<Position> openPositions,
        int correlationWindowDays,
        decimal maxConcentrationThreshold,
        CancellationToken cancellationToken)
    {
        if (openPositions.Count == 0)
        {
            return new CorrelationRiskResult(0, 0, string.Empty, false);
        }

        var symbols = openPositions.Select(p => p.Symbol)
            .Append(proposedSignal.Symbol)
            .Distinct()
            .ToList();

        var correlationMatrix = await GetCorrelationMatrixAsync(symbols, correlationWindowDays, cancellationToken);

        // Compute Herfindahl index on correlated exposure groups
        decimal maxCorrelation = 0;
        string  mostCorrelatedPair = string.Empty;

        foreach (var kvp in correlationMatrix)
        {
            if (Math.Abs(kvp.Value) > Math.Abs(maxCorrelation))
            {
                maxCorrelation     = kvp.Value;
                mostCorrelatedPair = kvp.Key;
            }
        }

        // Simplified Herfindahl: treat highly correlated positions as one group
        decimal totalNotional = openPositions.Sum(p => p.OpenLots * p.AverageEntryPrice)
                              + proposedSignal.SuggestedLotSize * proposedSignal.EntryPrice;

        if (totalNotional == 0)
            return new CorrelationRiskResult(0, maxCorrelation, mostCorrelatedPair, false);

        // Group positions by correlated clusters (correlation > 0.7 = same group)
        var groups = new Dictionary<string, decimal>();
        foreach (var pos in openPositions)
        {
            string groupKey = pos.Symbol;
            // Check if this symbol is correlated with an existing group
            foreach (var existingGroup in groups.Keys.ToList())
            {
                var pairKey = GetPairKey(pos.Symbol, existingGroup);
                if (correlationMatrix.TryGetValue(pairKey, out var corr) && Math.Abs(corr) > 0.7m)
                {
                    groupKey = existingGroup;
                    break;
                }
            }
            groups.TryGetValue(groupKey, out var existing);
            groups[groupKey] = existing + pos.OpenLots * pos.AverageEntryPrice;
        }

        // Add proposed signal
        string signalGroup = proposedSignal.Symbol;
        foreach (var existingGroup in groups.Keys.ToList())
        {
            var pairKey = GetPairKey(proposedSignal.Symbol, existingGroup);
            if (correlationMatrix.TryGetValue(pairKey, out var corr) && Math.Abs(corr) > 0.7m)
            {
                signalGroup = existingGroup;
                break;
            }
        }
        groups.TryGetValue(signalGroup, out var signalExisting);
        groups[signalGroup] = signalExisting + proposedSignal.SuggestedLotSize * proposedSignal.EntryPrice;

        // Herfindahl index
        decimal herfindahl = 0;
        foreach (var group in groups.Values)
        {
            var weight = group / totalNotional;
            herfindahl += weight * weight;
        }

        bool breached = herfindahl > maxConcentrationThreshold;

        if (breached)
        {
            _logger.LogWarning(
                "CorrelationRisk: Herfindahl={HHI:F4} exceeds threshold {Threshold:F4} after adding {Symbol}",
                herfindahl, maxConcentrationThreshold, proposedSignal.Symbol);
        }

        return new CorrelationRiskResult(herfindahl, maxCorrelation, mostCorrelatedPair, breached);
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetCorrelationMatrixAsync(
        IReadOnlyList<string> symbols,
        int windowDays,
        CancellationToken cancellationToken)
    {
        var ctx = _readContext.GetDbContext();

        // Fetch daily returns per symbol
        var returnsBySymbol = new Dictionary<string, List<decimal>>();
        foreach (var symbol in symbols)
        {
            var closes = await ctx.Set<Candle>()
                .Where(c => c.Symbol == symbol && c.Timeframe == Timeframe.D1
                         && c.IsClosed && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(windowDays + 1)
                .OrderBy(c => c.Timestamp)
                .Select(c => c.Close)
                .ToListAsync(cancellationToken);

            var returns = new List<decimal>();
            for (int i = 1; i < closes.Count; i++)
            {
                if (closes[i - 1] != 0)
                    returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
            }
            returnsBySymbol[symbol] = returns;
        }

        // Compute pairwise Pearson correlation
        var matrix = new Dictionary<string, decimal>();

        for (int i = 0; i < symbols.Count; i++)
        {
            for (int j = i + 1; j < symbols.Count; j++)
            {
                if (!returnsBySymbol.TryGetValue(symbols[i], out var r1) ||
                    !returnsBySymbol.TryGetValue(symbols[j], out var r2))
                    continue;

                int n = Math.Min(r1.Count, r2.Count);
                if (n < 10) continue;

                var corr = PearsonCorrelation(r1.TakeLast(n).ToArray(), r2.TakeLast(n).ToArray());
                matrix[GetPairKey(symbols[i], symbols[j])] = corr;
            }
        }

        return matrix;
    }

    public async Task<Dictionary<long, decimal>> ComputeMCTRAsync(
        IReadOnlyList<Position> positions,
        IReadOnlyDictionary<string, decimal> correlationMatrix,
        int windowDays,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, decimal>();
        if (positions.Count == 0) return result;

        var symbols = positions.Select(p => p.Symbol).Distinct().ToList();
        var ctx = _readContext.GetDbContext();

        // Fetch daily returns and compute std dev per symbol
        var returnsBySymbol = new Dictionary<string, List<decimal>>();
        var stdDevBySymbol  = new Dictionary<string, decimal>();

        foreach (var symbol in symbols)
        {
            var closes = await ctx.Set<Candle>()
                .Where(c => c.Symbol == symbol && c.Timeframe == Timeframe.D1
                         && c.IsClosed && !c.IsDeleted)
                .OrderByDescending(c => c.Timestamp)
                .Take(windowDays + 1)
                .OrderBy(c => c.Timestamp)
                .Select(c => c.Close)
                .ToListAsync(cancellationToken);

            var returns = new List<decimal>();
            for (int i = 1; i < closes.Count; i++)
            {
                if (closes[i - 1] != 0)
                    returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
            }
            returnsBySymbol[symbol] = returns;

            if (returns.Count > 1)
            {
                double mean = returns.Average(r => (double)r);
                double variance = returns.Sum(r => ((double)r - mean) * ((double)r - mean)) / (returns.Count - 1);
                stdDevBySymbol[symbol] = (decimal)Math.Sqrt(variance);
            }
            else
            {
                stdDevBySymbol[symbol] = 0;
            }
        }

        // Build covariance matrix: Cov(i,j) = Corr(i,j) * StdDev(i) * StdDev(j)
        // Compute portfolio return series (weighted sum of position returns)
        decimal totalNotional = positions.Sum(p => p.OpenLots * p.AverageEntryPrice);
        if (totalNotional == 0)
        {
            foreach (var p in positions)
                result[p.Id] = 0;
            return result;
        }

        // Position weights
        var weights = new Dictionary<long, decimal>();
        foreach (var p in positions)
            weights[p.Id] = p.OpenLots * p.AverageEntryPrice / totalNotional;

        // Compute portfolio variance: Var(P) = sum_i sum_j w_i * w_j * Cov(i,j)
        // where w is aggregated weight per symbol (multiple positions can share a symbol)
        // Use absolute (unsigned) weights for MCTR computation. Directional netting
        // of long and short positions in the same symbol would understate the true
        // marginal contribution to risk — a hedged pair still contributes basis risk.
        var symbolWeights = new Dictionary<string, decimal>();
        foreach (var p in positions)
        {
            symbolWeights.TryGetValue(p.Symbol, out var existing);
            decimal direction = p.Direction == PositionDirection.Long ? 1m : -1m;
            symbolWeights[p.Symbol] = existing + Math.Abs(direction * weights[p.Id]);
        }

        decimal portfolioVariance = 0;
        for (int i = 0; i < symbols.Count; i++)
        {
            for (int j = 0; j < symbols.Count; j++)
            {
                decimal corr = 1m;
                if (i != j)
                {
                    var pairKey = GetPairKey(symbols[i], symbols[j]);
                    correlationMatrix.TryGetValue(pairKey, out corr);
                }

                decimal cov = corr * stdDevBySymbol.GetValueOrDefault(symbols[i])
                                    * stdDevBySymbol.GetValueOrDefault(symbols[j]);
                decimal wi = symbolWeights.GetValueOrDefault(symbols[i]);
                decimal wj = symbolWeights.GetValueOrDefault(symbols[j]);
                portfolioVariance += wi * wj * cov;
            }
        }

        decimal portfolioSigma = portfolioVariance > 0 ? (decimal)Math.Sqrt((double)portfolioVariance) : 0;

        if (portfolioVariance == 0)
        {
            foreach (var p in positions)
                result[p.Id] = 0;
            return result;
        }

        // For each position, compute beta_i = Cov(position_i, portfolio) / Var(portfolio)
        // Cov(position_i, portfolio) = sum_j (w_j * Cov(i,j)) where i is position's symbol
        foreach (var position in positions)
        {
            decimal covWithPortfolio = 0;
            decimal direction = position.Direction == PositionDirection.Long ? 1m : -1m;

            foreach (var sym in symbols)
            {
                decimal corr = position.Symbol == sym ? 1m : 0m;
                if (position.Symbol != sym)
                {
                    var pairKey = GetPairKey(position.Symbol, sym);
                    correlationMatrix.TryGetValue(pairKey, out corr);
                }

                decimal cov = corr * stdDevBySymbol.GetValueOrDefault(position.Symbol)
                                    * stdDevBySymbol.GetValueOrDefault(sym);
                covWithPortfolio += symbolWeights.GetValueOrDefault(sym) * cov;
            }

            decimal beta = covWithPortfolio / portfolioVariance;
            decimal mctr = direction * beta * portfolioSigma;
            result[position.Id] = mctr;
        }

        return result;
    }

    private static string GetPairKey(string a, string b)
        => string.Compare(a, b, StringComparison.Ordinal) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    private static decimal PearsonCorrelation(decimal[] x, decimal[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n == 0) return 0;

        decimal sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += x[i];
            sumY  += y[i];
            sumXY += x[i] * y[i];
            sumX2 += x[i] * x[i];
            sumY2 += y[i] * y[i];
        }

        double varX = (double)(n * sumX2 - sumX * sumX);
        double varY = (double)(n * sumY2 - sumY * sumY);

        // Guard against floating-point underflow producing negative variance
        if (varX <= 0 || varY <= 0) return 0;

        decimal denominator = (decimal)Math.Sqrt(varX * varY);
        return denominator == 0 ? 0 : (n * sumXY - sumX * sumY) / denominator;
    }
}
