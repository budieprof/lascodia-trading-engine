using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface ISessionFilter
{
    TradingSession GetCurrentSession(DateTime utcTime);
    bool IsSessionAllowed(TradingSession session, IReadOnlyList<TradingSession> allowedSessions);

    /// <summary>
    /// Returns a quality score (0.0–1.0) for the given session, reflecting its
    /// typical liquidity and signal reliability. Used by post-evaluator confidence
    /// modifiers to scale signal confidence based on session quality.
    /// </summary>
    decimal GetSessionQuality(TradingSession session);
}
