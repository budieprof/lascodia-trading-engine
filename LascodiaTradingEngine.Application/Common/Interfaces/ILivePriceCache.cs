namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Thread-safe cache of the latest bid/ask prices per symbol.
/// Updated by tick ingestion and read by strategy evaluators, risk checkers, and ML scoring.
/// </summary>
public interface ILivePriceCache
{
    /// <summary>Updates or inserts the latest bid/ask for the given symbol.</summary>
    void Update(string symbol, decimal bid, decimal ask, DateTime timestamp);

    /// <summary>Returns the latest bid/ask for the given symbol, or <c>null</c> if not cached.</summary>
    (decimal Bid, decimal Ask, DateTime Timestamp)? Get(string symbol);

    /// <summary>Returns a snapshot of all cached symbols and their latest prices.</summary>
    IReadOnlyDictionary<string, (decimal Bid, decimal Ask, DateTime Timestamp)> GetAll();
}
