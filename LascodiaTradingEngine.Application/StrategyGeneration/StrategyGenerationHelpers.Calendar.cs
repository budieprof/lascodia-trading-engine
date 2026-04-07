using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

public static partial class StrategyGenerationHelpers
{
    public static bool IsWeekendForAssetMix(IEnumerable<(string Symbol, CurrencyPair? Pair)> symbols, DateTime utcNow)
    {
        if (utcNow.DayOfWeek != DayOfWeek.Saturday && utcNow.DayOfWeek != DayOfWeek.Sunday)
            return false;

        return symbols.Any(s => ClassifyAsset(s.Symbol, s.Pair) != AssetClass.Crypto);
    }

    public static bool IsInBlackoutPeriod(string blackoutPeriods, string blackoutTimezone, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(blackoutPeriods))
            return false;

        DateTime now;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(blackoutTimezone);
            now = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        }
        catch
        {
            now = utcNow;
        }

        int todayOrdinal = now.Month * 100 + now.Day;

        foreach (var period in blackoutPeriods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = period.Split('-');
            if (parts.Length != 2
                || !TryParseMonthDay(parts[0], out int startOrdinal)
                || !TryParseMonthDay(parts[1], out int endOrdinal))
            {
                continue;
            }

            if (startOrdinal <= endOrdinal)
            {
                if (todayOrdinal >= startOrdinal && todayOrdinal <= endOrdinal)
                    return true;
            }
            else if (todayOrdinal >= startOrdinal || todayOrdinal <= endOrdinal)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseMonthDay(string value, out int ordinal)
    {
        ordinal = 0;
        var parts = value.Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int month)
            || !int.TryParse(parts[1], out int day))
        {
            return false;
        }

        if (month is < 1 or > 12 || day is < 1 or > 31)
            return false;

        ordinal = month * 100 + day;
        return true;
    }
}
