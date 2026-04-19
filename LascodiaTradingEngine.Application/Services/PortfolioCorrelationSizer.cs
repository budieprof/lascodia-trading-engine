using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Correlation-aware position sizer. Signals sized independently per strategy
/// ignore that two BUYs on highly-correlated pairs (EURUSD + GBPUSD) are
/// effectively one bet at 2× size. This sizer returns a multiplier in (0, 1]
/// that scales a proposed lot size by the "correlation-adjusted independence"
/// across same-direction open positions:
///
///   multiplier = 1 / sqrt(1 + Σ ρᵢ)
///
/// where ρᵢ is the (heuristic) correlation between the new signal and the
/// i-th same-direction open position. This is the standard portfolio-variance
/// shrinkage factor — collapses to 1 when there are no same-direction opens,
/// and scales ~0.7 when one ρ=1 peer is already open.
///
/// Correlations are derived heuristically from shared currency codes rather than
/// a precomputed matrix. EURUSD and GBPUSD share USD as the quote → ρ≈0.6.
/// EURUSD and AUDJPY share nothing → ρ≈0.1. This is crude but directionally
/// correct and avoids a historical-returns matrix dependency for MVP.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class PortfolioCorrelationSizer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PortfolioCorrelationSizer> _logger;

    public PortfolioCorrelationSizer(
        IServiceScopeFactory scopeFactory,
        ILogger<PortfolioCorrelationSizer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Computes a lot-size multiplier in (0, 1] reflecting how much independent
    /// risk capacity remains after accounting for existing same-direction open
    /// positions. Returns 1.0 when no correlated peers are open.
    /// </summary>
    public async Task<decimal> ComputeMultiplierAsync(
        string newSymbol,
        TradeDirection newDirection,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

            // Load open positions — Status == 'Open' or similar live state.
            // The Position entity's Status field varies, so we filter on !IsDeleted
            // and a generous lifetime window rather than status text.
            var opens = await readCtx.GetDbContext()
                .Set<Position>()
                .AsNoTracking()
                .Where(p => p.ClosedAt == null && !p.IsDeleted)
                .Select(p => new { p.Symbol, p.Direction })
                .ToListAsync(ct);

            if (opens.Count == 0)
                return 1.0m;

            // Map Position.Direction (Long/Short) to TradeDirection (Buy/Sell) so
            // same-direction check aligns semantics.
            TradeDirection NormalizePosDirection(PositionDirection d) =>
                d == PositionDirection.Long ? TradeDirection.Buy : TradeDirection.Sell;

            double totalCorrelation = 0.0;
            foreach (var open in opens)
            {
                if (NormalizePosDirection(open.Direction) != newDirection)
                    continue;                      // opposite direction = natural offset
                if (string.Equals(open.Symbol, newSymbol, StringComparison.OrdinalIgnoreCase))
                    totalCorrelation += 1.0;       // same pair = perfect correlation
                else
                    totalCorrelation += HeuristicCorrelation(newSymbol, open.Symbol);
            }

            if (totalCorrelation <= 0.0)
                return 1.0m;

            // Standard portfolio-variance shrinkage: divide by sqrt(1 + Σρ)
            double multiplier = 1.0 / Math.Sqrt(1.0 + totalCorrelation);
            return (decimal)Math.Clamp(multiplier, 0.25, 1.0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PortfolioCorrelationSizer: failed for {Sym}/{Dir} — returning 1.0", newSymbol, newDirection);
            return 1.0m;
        }
    }

    /// <summary>
    /// Heuristic correlation based on shared currency codes. Crude but directionally
    /// correct across G10 FX. Zero when symbols are shorter than 6 chars.
    /// </summary>
    internal static double HeuristicCorrelation(string symbolA, string symbolB)
    {
        if (symbolA.Length < 6 || symbolB.Length < 6) return 0.0;

        string baseA = symbolA[..3].ToUpperInvariant();
        string quoteA = symbolA.Substring(3, 3).ToUpperInvariant();
        string baseB = symbolB[..3].ToUpperInvariant();
        string quoteB = symbolB.Substring(3, 3).ToUpperInvariant();

        bool sharedBase  = baseA == baseB;
        bool sharedQuote = quoteA == quoteB;
        // Inverse quote — e.g. EURUSD vs USDCAD both have USD but on opposite sides
        // of the fraction; returns are negatively correlated on USD moves.
        bool inversePair = baseA == quoteB || quoteA == baseB;

        if (sharedBase && sharedQuote) return 1.0;    // identical pair
        if (sharedBase || sharedQuote) return 0.6;    // strong same-side correlation
        if (inversePair)               return -0.4;   // inverse shared currency
        return 0.1;                                    // unrelated crosses still have some macro co-movement
    }
}
