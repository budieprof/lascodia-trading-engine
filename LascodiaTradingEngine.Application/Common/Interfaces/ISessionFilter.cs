using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Determines the active trading session (Sydney, Tokyo, London, NewYork, Overlap) for a given
/// UTC time and checks whether trading is permitted in the current session.
/// </summary>
public interface ISessionFilter
{
    /// <summary>Returns the trading session active at the given UTC time.</summary>
    TradingSession GetCurrentSession(DateTime utcTime);

    /// <summary>
    /// Returns the trading session active at the given UTC time for a specific symbol.
    /// Checks symbol-specific session schedules first, then falls back to global resolution.
    /// Returns <c>null</c> if the symbol has specific schedules but none match (outside session).
    /// </summary>
    TradingSession? GetCurrentSession(DateTime utcTime, string symbol);

    /// <summary>Returns <c>true</c> if the given session is in the allowed sessions list.</summary>
    bool IsSessionAllowed(TradingSession session, IReadOnlyList<TradingSession> allowedSessions);

    /// <summary>
    /// Returns a quality score (0.0–1.0) for the given session, reflecting its
    /// typical liquidity and signal reliability. Used by post-evaluator confidence
    /// modifiers to scale signal confidence based on session quality.
    /// </summary>
    decimal GetSessionQuality(TradingSession session);
}
