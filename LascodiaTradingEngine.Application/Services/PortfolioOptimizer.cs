using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Portfolio-level position sizing and risk allocation across all open/pending positions.
/// Applies half-Kelly allocation with correlation-aware limits to prevent concentrated exposure.
/// </summary>
/// <remarks>
/// Called by <c>SignalOrderBridgeWorker</c> before order creation to adjust lot sizes.
/// <list type="bullet">
///   <item>Computes per-symbol Kelly fraction from the model's confidence and historical win rate.</item>
///   <item>Applies portfolio-level Kelly cap: sum of all position allocations ≤ _opts.MaxPortfolioKelly.</item>
///   <item>Applies correlation penalty: correlated pairs share a group budget.</item>
///   <item>Returns an adjusted lot size (may be zero = skip trade).</item>
/// </list>
/// </remarks>
public interface IPortfolioOptimizer
{
    /// <summary>
    /// Computes the adjusted lot size for a new trade, given the portfolio context.
    /// Returns 0 if the trade should be skipped due to portfolio constraints.
    /// </summary>
    Task<decimal> OptimizeLotSizeAsync(
        string           symbol,
        TradeDirection   direction,
        decimal          suggestedLotSize,
        decimal          confidence,
        decimal          accountEquity,
        CancellationToken ct);
}

[RegisterService]
public sealed class PortfolioOptimizer : IPortfolioOptimizer
{
    private readonly PortfolioOptimizerOptions _opts;
    private readonly CorrelationGroupOptions _corrOpts;
    private readonly IReadApplicationDbContext _readDb;
    private readonly ILogger<PortfolioOptimizer> _logger;

    public PortfolioOptimizer(
        IReadApplicationDbContext readDb,
        ILogger<PortfolioOptimizer> logger,
        PortfolioOptimizerOptions opts,
        CorrelationGroupOptions corrOpts)
    {
        _readDb   = readDb;
        _logger   = logger;
        _opts     = opts;
        _corrOpts = corrOpts;
    }

    public async Task<decimal> OptimizeLotSizeAsync(
        string           symbol,
        TradeDirection   direction,
        decimal          suggestedLotSize,
        decimal          confidence,
        decimal          accountEquity,
        CancellationToken ct)
    {
        if (accountEquity <= 0 || suggestedLotSize <= 0)
            return 0;

        var ctx = _readDb.GetDbContext();

        // ── 1. Load open positions ─────────────────────────────────────────
        var openPositions = await ctx.Set<Position>()
            .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
            .AsNoTracking()
            .Select(p => new { p.Symbol, p.OpenLots, p.Direction })
            .ToListAsync(ct);

        // ── 2. Half-Kelly fraction for this trade ──────────────────────────
        // f = (2p − 1) × 0.5 where p = confidence (calibrated probability)
        double p = Math.Clamp((double)confidence, 0.0, 1.0);
        double kellyFraction = Math.Max(0, (2 * p - 1) * 0.5);
        kellyFraction = Math.Min(kellyFraction, _opts.MaxPerTradeKelly);

        // ── 3. Current portfolio exposure ──────────────────────────────────
        double totalExposure = openPositions.Sum(pos => (double)pos.OpenLots);
        double portfolioBudgetRemaining = _opts.MaxPortfolioKelly - (totalExposure / (double)accountEquity);

        if (portfolioBudgetRemaining <= 0)
        {
            _logger.LogInformation(
                "PortfolioOptimizer: portfolio Kelly budget exhausted ({Total:P1} >= {Max:P1}) — skipping {Symbol}",
                totalExposure / (double)accountEquity, _opts.MaxPortfolioKelly, symbol);
            return 0;
        }

        // ── 4. Correlation group budget ────────────────────────────────────
        var group = FindCorrelationGroup(symbol.ToUpperInvariant());
        if (group is not null)
        {
            double groupExposure = openPositions
                .Where(pos => group.Contains(pos.Symbol.ToUpperInvariant()))
                .Sum(pos => (double)pos.OpenLots);

            double groupBudgetRemaining = _opts.CorrelationGroupCap - (groupExposure / (double)accountEquity);

            if (groupBudgetRemaining <= 0)
            {
                _logger.LogInformation(
                    "PortfolioOptimizer: correlation group [{Group}] budget exhausted — skipping {Symbol}",
                    string.Join(",", group), symbol);
                return 0;
            }

            // Cap Kelly by group remaining budget
            kellyFraction = Math.Min(kellyFraction, groupBudgetRemaining);
        }

        // ── 5. Cap by portfolio remaining budget ───────────────────────────
        kellyFraction = Math.Min(kellyFraction, portfolioBudgetRemaining);

        // ── 6. Convert to lot size ─────────────────────────────────────────
        decimal adjustedLots = (decimal)(kellyFraction * (double)accountEquity / 100_000.0); // standard lot = 100K
        adjustedLots = Math.Min(adjustedLots, suggestedLotSize); // never exceed the strategy's suggestion
        adjustedLots = Math.Round(adjustedLots, 2);

        if (adjustedLots < (decimal)_opts.MinLotSize)
        {
            _logger.LogDebug(
                "PortfolioOptimizer: adjusted lot size {Lots} below minimum — skipping {Symbol}",
                adjustedLots, symbol);
            return 0;
        }

        _logger.LogInformation(
            "PortfolioOptimizer: {Symbol} kelly={Kelly:P1} suggested={Suggested} adjusted={Adjusted}",
            symbol, kellyFraction, suggestedLotSize, adjustedLots);

        return adjustedLots;
    }

    private string[]? FindCorrelationGroup(string symbol)
    {
        foreach (var group in _corrOpts.Groups)
            if (group.Contains(symbol))
                return group;
        return null;
    }
}
