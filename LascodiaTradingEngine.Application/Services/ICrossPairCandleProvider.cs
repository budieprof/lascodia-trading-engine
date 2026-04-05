using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Provides recent candle data for correlated pairs of a given primary symbol.
/// Used by the extended ML feature pipeline to build cross-pair features.
/// </summary>
public interface ICrossPairCandleProvider
{
    /// <summary>
    /// Returns up to 3 correlated pairs' candles for the given primary symbol/timeframe,
    /// limited to the most recent <paramref name="barCount"/> bars before <paramref name="asOf"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<Candle>>> GetCrossPairCandlesAsync(
        string primarySymbol, Timeframe timeframe, DateTime asOf, int barCount, CancellationToken ct);
}
