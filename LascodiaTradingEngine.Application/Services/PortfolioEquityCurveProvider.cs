using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Builds an aggregate portfolio equity curve from closed positions of active strategies
/// and computes Sharpe ratios for portfolio-aware strategy generation decisions.
/// The marginal Sharpe calculation lets the screening engine measure whether a candidate
/// strategy would improve or degrade the existing portfolio's risk-adjusted returns.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IPortfolioEquityCurveProvider))]
public class PortfolioEquityCurveProvider : IPortfolioEquityCurveProvider
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly ILogger<PortfolioEquityCurveProvider> _logger;

    /// <summary>Starting equity for the synthetic portfolio equity curve.</summary>
    private const decimal StartingEquity = 10_000m;

    /// <summary>Minimum data points for a meaningful Sharpe calculation.</summary>
    private const int MinDataPoints = 10;

    /// <summary>Annualisation factor (trading days per year).</summary>
    private const double AnnualisationFactor = 252.0;

    public PortfolioEquityCurveProvider(
        IReadApplicationDbContext readContext,
        ILogger<PortfolioEquityCurveProvider> logger)
    {
        _readContext = readContext;
        _logger      = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(DateTime Date, decimal Equity)>> GetPortfolioEquityCurveAsync(
        int lookbackDays, CancellationToken ct)
    {
        try
        {
            var readDb   = _readContext.GetDbContext();
            var cutoff   = DateTime.UtcNow.AddDays(-lookbackDays);

            // Load active strategy IDs
            var activeStrategyIds = await readDb.Set<Strategy>()
                .AsNoTracking()
                .Where(s => s.LifecycleStage >= StrategyLifecycleStage.Active)
                .Select(s => s.Id)
                .ToListAsync(ct);

            if (activeStrategyIds.Count == 0)
            {
                _logger.LogDebug("PortfolioEquityCurveProvider: no active strategies found");
                return Array.Empty<(DateTime, decimal)>();
            }

            // Load all closed positions for active strategies within the lookback window.
            // Position does not have a direct StrategyId, so we join through Order.
            var dailyPnL = await readDb.Set<Order>()
                .AsNoTracking()
                .Where(o => activeStrategyIds.Contains(o.StrategyId))
                .Join(
                    readDb.Set<Position>().AsNoTracking()
                        .Where(p => p.Status == PositionStatus.Closed
                                 && p.ClosedAt != null
                                 && p.ClosedAt >= cutoff),
                    order => order.Id,
                    position => position.OpenOrderId,
                    (order, position) => new { position.ClosedAt, position.RealizedPnL })
                .GroupBy(x => x.ClosedAt!.Value.Date)
                .Select(g => new { Date = g.Key, DailyPnL = g.Sum(x => x.RealizedPnL) })
                .OrderBy(x => x.Date)
                .ToListAsync(ct);

            if (dailyPnL.Count == 0)
            {
                _logger.LogDebug("PortfolioEquityCurveProvider: no closed positions in lookback window");
                return Array.Empty<(DateTime, decimal)>();
            }

            // Build cumulative equity curve
            var curve  = new List<(DateTime Date, decimal Equity)>(dailyPnL.Count + 1);
            var equity = StartingEquity;

            // Seed with starting equity on the day before the first data point
            curve.Add((dailyPnL[0].Date.AddDays(-1), equity));

            foreach (var day in dailyPnL)
            {
                equity += day.DailyPnL;
                curve.Add((day.Date, equity));
            }

            // Gap interpolation: fill weekday gaps with carry-forward equity
            if (curve.Count >= 2)
            {
                var interpolated = new List<(DateTime Date, decimal Equity)>();
                var currentDate = curve[0].Date;
                decimal lastEquity = curve[0].Equity;
                int rawIdx = 0;

                while (currentDate <= curve[^1].Date)
                {
                    // Advance rawIdx past any raw entries that match or precede currentDate
                    // (handles weekend seed points that the interpolation loop skips)
                    while (rawIdx < curve.Count && curve[rawIdx].Date <= currentDate)
                    {
                        lastEquity = curve[rawIdx].Equity;
                        rawIdx++;
                    }

                    // Only emit weekday entries
                    if (currentDate.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                        interpolated.Add((currentDate, lastEquity));

                    currentDate = currentDate.AddDays(1);
                }
                curve = interpolated;
            }

            // Minimum density validation
            const int MinEquityCurvePoints = 20;
            if (curve.Count < MinEquityCurvePoints)
                return Array.Empty<(DateTime, decimal)>();

            return curve;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PortfolioEquityCurveProvider: failed to build portfolio equity curve");
            return Array.Empty<(DateTime, decimal)>();
        }
    }

    /// <inheritdoc />
    public decimal ComputePortfolioSharpe(IReadOnlyList<(DateTime Date, decimal Equity)> curve)
    {
        if (curve.Count < MinDataPoints)
            return 0m;

        try
        {
            var returns = ComputeDailyReturns(curve);
            return CalculateAnnualisedSharpe(returns);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PortfolioEquityCurveProvider: failed to compute portfolio Sharpe");
            return 0m;
        }
    }

    /// <inheritdoc />
    public decimal ComputeMarginalSharpe(
        IReadOnlyList<(DateTime Date, decimal Equity)> portfolioCurve,
        IReadOnlyList<BacktestTrade> candidateTrades,
        decimal initialBalance,
        int activeStrategyCount = 10)
    {
        try
        {
            if (portfolioCurve.Count < MinDataPoints || candidateTrades.Count == 0)
                return 0m;

            // 1. Build candidate daily PnL from BacktestTrade list
            var candidateDailyPnL = candidateTrades
                .GroupBy(t => t.ExitTime.Date)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.PnL));

            // Count active strategies (approximation: number of portfolio curve points implies at least 1)
            var portfolioDates = portfolioCurve.Select(p => p.Date).ToHashSet();
            var allDates = portfolioDates
                .Union(candidateDailyPnL.Keys)
                .OrderBy(d => d)
                .ToList();

            // Build a lookup for portfolio equity by date
            var portfolioByDate = portfolioCurve.ToDictionary(p => p.Date, p => p.Equity);

            // Use the caller-supplied active strategy count for proper allocation weighting.
            activeStrategyCount = Math.Max(1, activeStrategyCount);

            // 2. Merge portfolio equity with candidate PnL
            var combinedCurve = new List<(DateTime Date, decimal Equity)>();
            decimal lastPortfolioEquity = portfolioCurve.Count > 0 ? portfolioCurve[0].Equity : StartingEquity;
            decimal candidateCumPnL = 0m;
            decimal scaleFactor = 1.0m / (activeStrategyCount + 1);

            foreach (var date in allDates)
            {
                if (portfolioByDate.TryGetValue(date, out var pEquity))
                    lastPortfolioEquity = pEquity;

                if (candidateDailyPnL.TryGetValue(date, out var cPnL))
                    candidateCumPnL += cPnL;

                var combinedEquity = lastPortfolioEquity + candidateCumPnL * scaleFactor;
                combinedCurve.Add((date, combinedEquity));
            }

            if (combinedCurve.Count < MinDataPoints)
                return 0m;

            // 3. Compute combined Sharpe from merged curve
            var combinedSharpe  = CalculateAnnualisedSharpe(ComputeDailyReturns(combinedCurve));
            var portfolioSharpe = ComputePortfolioSharpe(portfolioCurve);

            return combinedSharpe - portfolioSharpe;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PortfolioEquityCurveProvider: failed to compute marginal Sharpe");
            return 0m;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<double> ComputeDailyReturns(IReadOnlyList<(DateTime Date, decimal Equity)> curve)
    {
        var returns = new List<double>(curve.Count - 1);

        for (int i = 1; i < curve.Count; i++)
        {
            var prev = curve[i - 1].Equity;
            if (prev == 0m) continue;
            returns.Add((double)(curve[i].Equity - prev) / (double)prev);
        }

        return returns;
    }

    private static decimal CalculateAnnualisedSharpe(List<double> returns)
    {
        if (returns.Count < MinDataPoints)
            return 0m;

        double mean   = returns.Average();
        double sumSq  = returns.Sum(r => (r - mean) * (r - mean));
        double stddev = Math.Sqrt(sumSq / (returns.Count - 1));

        if (stddev < 1e-12)
            return 0m;

        double sharpe = mean / stddev * Math.Sqrt(AnnualisationFactor);
        return (decimal)sharpe;
    }
}
