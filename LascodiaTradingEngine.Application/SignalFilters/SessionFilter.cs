using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Determines the active trading session based on UTC time and checks
/// whether that session is in the caller's allowed list.
///
/// Session ranges (UTC):
///   Asian            00:00 – 09:00
///   London           08:00 – 17:00
///   LondonNYOverlap  13:00 – 17:00  (highest priority)
///   NewYork          13:00 – 22:00
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class SessionFilter : ISessionFilter
{
    public TradingSession GetCurrentSession(DateTime utcTime)
    {
        var time = utcTime.TimeOfDay;

        var londonNYStart = new TimeSpan(13, 0, 0);
        var londonNYEnd   = new TimeSpan(17, 0, 0);
        var londonStart   = new TimeSpan(8,  0, 0);
        var londonEnd     = new TimeSpan(17, 0, 0);
        var newYorkStart  = new TimeSpan(13, 0, 0);
        var newYorkEnd    = new TimeSpan(22, 0, 0);
        var asianStart    = new TimeSpan(0,  0, 0);
        var asianEnd      = new TimeSpan(9,  0, 0);

        // LondonNYOverlap has highest priority
        if (time >= londonNYStart && time < londonNYEnd)
            return TradingSession.LondonNYOverlap;

        if (time >= londonStart && time < londonEnd)
            return TradingSession.London;

        if (time >= newYorkStart && time < newYorkEnd)
            return TradingSession.NewYork;

        if (time >= asianStart && time < asianEnd)
            return TradingSession.Asian;

        // Late evening (22:00–24:00) — treat as Asian pre-open
        return TradingSession.Asian;
    }

    public bool IsSessionAllowed(TradingSession session, IReadOnlyList<TradingSession> allowedSessions)
    {
        return allowedSessions.Contains(session);
    }

    public decimal GetSessionQuality(TradingSession session) => session switch
    {
        TradingSession.LondonNYOverlap => 1.0m,
        TradingSession.London          => 0.85m,
        TradingSession.NewYork         => 0.80m,
        TradingSession.Asian           => 0.50m,
        _                              => 0.60m,
    };
}
