using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Per-symbol execution cost profile derived from realised TCA data. Feed this into
/// the backtester so strategy P&amp;L is computed against the same slippage / spread /
/// commission that live fills actually incur — closing the "great in backtest, bleeds
/// in live" gap that kills most retail quant systems.
///
/// Source: rolling <see cref="TransactionCostAnalysis"/> observations per symbol.
/// A symbol with no TCA data (e.g. newly added instrument) returns a conservative
/// default profile rather than zero, so a strategy can't accidentally backtest as
/// if fills were free.
/// </summary>
public sealed record SymbolCostProfile(
    string Symbol,
    decimal AvgSpreadCostInPrice,
    decimal AvgCommissionCostInAccountCcy,
    decimal AvgMarketImpactInPrice,
    int    SampleSize,
    bool   IsDefault);

/// <summary>
/// Abstraction consumed by the backtester. Returns a realised cost profile per
/// symbol — the backtester deducts these on every simulated trade, making the
/// backtest Sharpe directly comparable to the live Sharpe.
/// </summary>
public interface ITcaCostModelProvider
{
    Task<SymbolCostProfile> GetAsync(string symbol, CancellationToken ct);
}

public sealed class TcaCostModelProvider : ITcaCostModelProvider
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly IMemoryCache _cache;

    private const int    RollingWindowDays   = 30;
    private const int    MinSamplesForRealProfile = 30;
    private const string CacheKeyPrefix      = "TcaCostModel:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Conservative default when no TCA data exists yet. ~1 pip for major FX pairs
    /// encoded as raw price units (0.0001 for 4-decimal, 0.01 for JPY crosses is
    /// caller-adjusted). Chosen to be *pessimistic* so untested strategies look
    /// worse in backtest, not better — fails safe.
    /// </summary>
    private static readonly SymbolCostProfile ConservativeDefault = new(
        Symbol: "DEFAULT",
        AvgSpreadCostInPrice: 0.0001m,
        AvgCommissionCostInAccountCcy: 0.00007m,
        AvgMarketImpactInPrice: 0.00002m,
        SampleSize: 0,
        IsDefault: true);

    public TcaCostModelProvider(IReadApplicationDbContext readCtx, IMemoryCache cache)
    {
        _readCtx = readCtx;
        _cache   = cache;
    }

    public async Task<SymbolCostProfile> GetAsync(string symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return ConservativeDefault with { Symbol = symbol ?? string.Empty };

        var cacheKey = CacheKeyPrefix + symbol;
        if (_cache.TryGetValue<SymbolCostProfile>(cacheKey, out var cached) && cached is not null)
            return cached;

        var cutoff = DateTime.UtcNow.AddDays(-RollingWindowDays);
        var db     = _readCtx.GetDbContext();
        var samples = await db
            .Set<TransactionCostAnalysis>()
            .AsNoTracking()
            .Where(t => t.Symbol == symbol && !t.IsDeleted && t.AnalyzedAt >= cutoff)
            .Select(t => new { t.SpreadCost, t.CommissionCost, t.MarketImpactCost })
            .ToListAsync(ct);

        SymbolCostProfile profile;
        if (samples.Count < MinSamplesForRealProfile)
        {
            profile = await BuildSymbolSpecDefaultAsync(db, symbol, samples.Count, ct);
        }
        else
        {
            profile = new SymbolCostProfile(
                Symbol: symbol,
                AvgSpreadCostInPrice: samples.Average(s => s.SpreadCost),
                AvgCommissionCostInAccountCcy: samples.Average(s => s.CommissionCost),
                AvgMarketImpactInPrice: samples.Average(s => s.MarketImpactCost),
                SampleSize: samples.Count,
                IsDefault: false);
        }

        _cache.Set(cacheKey, profile, CacheTtl);
        return profile;
    }

    /// <summary>
    /// Seed a starter cost profile from the symbol's broker-reported spec when TCA
    /// samples haven't accumulated yet. Takes the broker's reported spread (points ×
    /// 10^-DecimalPlaces = price units) over the global 1-pip guess so a 0.1-pip major
    /// doesn't look as expensive to backtest as a 5-pip exotic. Commission and market
    /// impact stay at conservative constants — there's no symbol-level broker hint
    /// for those, and under-estimating them is the failure mode we want to avoid.
    /// </summary>
    private async Task<SymbolCostProfile> BuildSymbolSpecDefaultAsync(
        DbContext db, string symbol, int sampleCount, CancellationToken ct)
    {
        var pair = await db
            .Set<Domain.Entities.CurrencyPair>()
            .AsNoTracking()
            .Where(p => p.Symbol == symbol && !p.IsDeleted)
            .Select(p => new { p.SpreadPoints, p.DecimalPlaces })
            .FirstOrDefaultAsync(ct);

        if (pair is null || pair.SpreadPoints <= 0 || pair.DecimalPlaces <= 0)
            return ConservativeDefault with { Symbol = symbol, SampleSize = sampleCount, IsDefault = true };

        // Spread in price units = broker-reported points × 10^-DecimalPlaces.
        decimal pointSize  = (decimal)Math.Pow(10, -pair.DecimalPlaces);
        decimal spreadCost = (decimal)pair.SpreadPoints * pointSize;

        return ConservativeDefault with
        {
            Symbol               = symbol,
            AvgSpreadCostInPrice = spreadCost,
            SampleSize           = sampleCount,
            IsDefault            = true,
        };
    }
}
