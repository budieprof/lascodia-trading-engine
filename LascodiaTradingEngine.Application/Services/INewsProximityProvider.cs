namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Provides the number of minutes until the next High-impact economic event
/// affecting a symbol's currencies. Used as an ML feature.
/// </summary>
public interface INewsProximityProvider
{
    /// <summary>
    /// Returns minutes until the next High-impact economic event for the given symbol.
    /// Returns double.MaxValue if no event within 7 days.
    /// </summary>
    Task<double> GetMinutesUntilNextEventAsync(string symbol, CancellationToken ct);
}
