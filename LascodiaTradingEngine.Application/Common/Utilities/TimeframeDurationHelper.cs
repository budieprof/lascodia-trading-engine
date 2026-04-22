using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Centralized timeframe-to-duration mapping used by workers that reason in bar units.
/// </summary>
public static class TimeframeDurationHelper
{
    /// <summary>Returns the nominal wall-clock duration of one closed bar.</summary>
    public static TimeSpan BarDuration(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1  => TimeSpan.FromMinutes(1),
        Timeframe.M5  => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.H1  => TimeSpan.FromHours(1),
        Timeframe.H4  => TimeSpan.FromHours(4),
        Timeframe.D1  => TimeSpan.FromDays(1),
        _             => TimeSpan.FromHours(1),
    };

    /// <summary>Returns the nominal wall-clock duration of one closed bar in minutes.</summary>
    public static double BarMinutes(Timeframe timeframe)
        => BarDuration(timeframe).TotalMinutes;

    /// <summary>
    /// Conservative minimum elapsed time before resolving a next-bar prediction outcome.
    /// This allows the next candle to close and be persisted.
    /// </summary>
    public static TimeSpan NextBarResolutionDelay(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1  => TimeSpan.FromMinutes(3),
        Timeframe.M5  => TimeSpan.FromMinutes(12),
        Timeframe.M15 => TimeSpan.FromMinutes(35),
        Timeframe.H1  => TimeSpan.FromMinutes(125),
        Timeframe.H4  => TimeSpan.FromHours(5),
        Timeframe.D1  => TimeSpan.FromHours(26),
        _             => TimeSpan.FromMinutes(125),
    };
}
