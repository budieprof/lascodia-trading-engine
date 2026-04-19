namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Engine-wide symbol normalization. Call at every EA ingestion boundary (candles,
/// orders, positions, snapshots) so downstream lookups against CurrencyPair /
/// SymbolSpec / TradeSignal / Order use a consistent key.
///
/// Brokers surface the same instrument under many names: "EURUSD", "EURUSD.a"
/// (IC Markets raw), "EURUSD.i" (FBS), "EURUSD.m" (micro), "EURUSD-pro" (XM),
/// "EUR/USD" (FIX), "EUR_USD" (OANDA). Downstream code assumes the canonical
/// 6-character form, so a broker-specific suffix silently breaks risk-checker
/// symbol-spec lookups — the signal is rejected with a generic error and a
/// trader sees "no signals" instead of "signal dropped due to symbol mismatch".
///
/// This utility strips the known broker-suffix patterns and upper-cases, yielding
/// the canonical form the rest of the engine uses.
/// </summary>
public static class SymbolNormalizer
{
    /// <summary>
    /// Normalize a broker-quoted symbol to the engine's canonical form.
    /// Safe to call on already-canonical symbols (idempotent).
    /// Returns the input unchanged if it's null/whitespace (let the caller decide
    /// how to reject); strips broker suffixes and path separators otherwise.
    /// </summary>
    public static string Normalize(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return symbol ?? string.Empty;

        // Strip anything from the first '.' onward ("EURUSD.a" → "EURUSD"),
        // the first '-' ("EURUSD-pro" → "EURUSD"), or replace FIX/OANDA separators.
        var cleaned = symbol.Trim();
        cleaned = cleaned.Replace("/", string.Empty).Replace("_", string.Empty);

        int dot = cleaned.IndexOf('.');
        if (dot > 0) cleaned = cleaned[..dot];

        int dash = cleaned.IndexOf('-');
        if (dash > 0) cleaned = cleaned[..dash];

        return cleaned.ToUpperInvariant();
    }

    /// <summary>
    /// True when two symbols resolve to the same canonical form.
    /// Use where you'd otherwise write <c>a == b</c> but either side could carry
    /// a broker suffix.
    /// </summary>
    public static bool AreSame(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), System.StringComparison.Ordinal);
}
