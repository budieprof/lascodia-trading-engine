using LascodiaTradingEngine.Application.Common.Interfaces;

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
public class SessionFilter : ISessionFilter
{
    public string GetCurrentSession(DateTime utcTime)
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
            return "LondonNYOverlap";

        if (time >= londonStart && time < londonEnd)
            return "London";

        if (time >= newYorkStart && time < newYorkEnd)
            return "NewYork";

        if (time >= asianStart && time < asianEnd)
            return "Asian";

        // Late evening (22:00–24:00) — treat as Asian pre-open
        return "Asian";
    }

    public bool IsSessionAllowed(string session, IReadOnlyList<string> allowedSessions)
    {
        return allowedSessions.Contains(session);
    }
}
