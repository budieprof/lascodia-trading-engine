using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Resolves quote-to-account currency exchange rates for cross-currency margin calculations.
/// </summary>
public static class CurrencyConversionHelper
{
    /// <summary>
    /// Resolves the exchange rate to convert from a symbol's quote currency to the account currency.
    /// Returns null (treated as 1.0 by RiskChecker) when the currencies already match or the rate
    /// cannot be determined from the live price cache.
    /// </summary>
    public static decimal? ResolveQuoteToAccountRate(
        CurrencyPair? spec, string accountCurrency, ILivePriceCache? cache)
    {
        if (spec is null || cache is null) return null;

        string quoteCcy = spec.QuoteCurrency.ToUpperInvariant();
        string acctCcy = accountCurrency.ToUpperInvariant();

        if (quoteCcy == acctCcy) return 1.0m;

        return LookupConversionRate(quoteCcy, acctCcy, cache);
    }

    /// <summary>
    /// Builds a dictionary of quote-to-account exchange rates for all symbols with open positions.
    /// </summary>
    public static Dictionary<string, decimal>? ResolvePortfolioQuoteToAccountRates(
        List<CurrencyPair> specs, string accountCurrency, ILivePriceCache? cache)
    {
        if (cache is null || specs.Count == 0) return null;

        string acctCcy = accountCurrency.ToUpperInvariant();
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var spec in specs)
        {
            string quoteCcy = spec.QuoteCurrency.ToUpperInvariant();
            if (quoteCcy == acctCcy)
            {
                rates[spec.Symbol] = 1.0m;
                continue;
            }

            var rate = LookupConversionRate(quoteCcy, acctCcy, cache);
            if (rate.HasValue)
                rates[spec.Symbol] = rate.Value;
        }

        return rates.Count > 0 ? rates : null;
    }

    /// <summary>
    /// Looks up a conversion rate from <paramref name="fromCcy"/> to <paramref name="toCcy"/>
    /// using the live price cache. Tries direct pair (e.g., GBPUSD) and inverse (e.g., USDGBP).
    /// </summary>
    public static decimal? LookupConversionRate(string fromCcy, string toCcy, ILivePriceCache cache)
    {
        // Try direct: fromCcy + toCcy (e.g., GBPUSD for GBP→USD)
        string directSymbol = fromCcy + toCcy;
        try
        {
            var directPrice = cache.Get(directSymbol);
            if (directPrice is not null)
            {
                decimal mid = (directPrice.Value.Bid + directPrice.Value.Ask) / 2m;
                if (mid > 0) return mid;
            }
        }
        catch { /* symbol not in cache */ }

        // Try inverse: toCcy + fromCcy (e.g., USDGBP for GBP→USD → 1/rate)
        string inverseSymbol = toCcy + fromCcy;
        try
        {
            var inversePrice = cache.Get(inverseSymbol);
            if (inversePrice is not null)
            {
                decimal mid = (inversePrice.Value.Bid + inversePrice.Value.Ask) / 2m;
                if (mid > 0) return 1.0m / mid;
            }
        }
        catch { /* symbol not in cache */ }

        return null;
    }
}
