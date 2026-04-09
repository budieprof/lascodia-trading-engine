using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

internal static class ValidationCandleSeriesInspector
{
    internal static bool TryValidate(
        IReadOnlyList<Candle> candles,
        Timeframe timeframe,
        int maxGapMultiplier,
        ISet<DateTime>? holidayDates,
        out string issue)
    {
        if (candles.Count == 0)
        {
            issue = "no closed candles found";
            return false;
        }

        if (candles.Count == 1)
        {
            issue = "fewer than 2 candles available for validation";
            return false;
        }

        var expectedGap = GetExpectedBarDuration(timeframe);
        var maxAllowedGap = TimeSpan.FromTicks(expectedGap.Ticks * Math.Max(1, maxGapMultiplier));
        var naturalClosureFloor = TimeSpan.FromDays(3);

        for (int i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            if (candle.High < candle.Low)
            {
                issue = $"candle at {candle.Timestamp:O} has High < Low";
                return false;
            }

            if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0)
            {
                issue = $"candle at {candle.Timestamp:O} has non-positive OHLC values";
                return false;
            }

            if (i == 0)
                continue;

            var previous = candles[i - 1];
            if (candle.Timestamp <= previous.Timestamp)
            {
                issue = candle.Timestamp == previous.Timestamp
                    ? $"duplicate candle timestamp detected at {candle.Timestamp:O}"
                    : $"non-monotonic candle ordering detected between {previous.Timestamp:O} and {candle.Timestamp:O}";
                return false;
            }

            var gap = candle.Timestamp - previous.Timestamp;
            if (gap > maxAllowedGap && gap > naturalClosureFloor && !IsNaturalMarketClosure(previous.Timestamp, candle.Timestamp, holidayDates))
            {
                issue = $"candle gap of {gap.TotalMinutes:F0} minutes exceeds validation tolerance for {timeframe}";
                return false;
            }
        }

        issue = string.Empty;
        return true;
    }

    private static TimeSpan GetExpectedBarDuration(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1)
    };

    private static bool IsNaturalMarketClosure(
        DateTime fromUtc,
        DateTime toUtc,
        ISet<DateTime>? holidayDates)
    {
        for (var day = fromUtc.Date.AddDays(1); day <= toUtc.Date; day = day.AddDays(1))
        {
            bool isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            bool isHoliday = holidayDates?.Contains(day) == true;
            if (!isWeekend && !isHoliday)
                return false;
        }

        return true;
    }
}
