using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Backtesting;

internal static class ValidationCandleSeriesInspector
{
    private static readonly TimeSpan TradingHoursStep = TimeSpan.FromMinutes(30);

    internal static bool TryValidate(
        IReadOnlyList<Candle> candles,
        Timeframe timeframe,
        int maxGapMultiplier,
        ISet<DateTime>? holidayDates,
        string? tradingHoursJson,
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
            var effectiveTradableGap = ComputeTradableGap(previous.Timestamp, candle.Timestamp, tradingHoursJson, holidayDates);
            if (effectiveTradableGap > maxAllowedGap
                && gap > naturalClosureFloor
                && !IsNaturalMarketClosure(previous.Timestamp, candle.Timestamp, holidayDates))
            {
                issue = $"candle gap of {gap.TotalMinutes:F0} minutes ({effectiveTradableGap.TotalMinutes:F0} tradable minutes) exceeds validation tolerance for {timeframe}";
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

    private static TimeSpan ComputeTradableGap(
        DateTime fromUtc,
        DateTime toUtc,
        string? tradingHoursJson,
        ISet<DateTime>? holidayDates)
    {
        if (fromUtc >= toUtc)
            return TimeSpan.Zero;

        var sessions = ParseTradingHours(tradingHoursJson);
        var cursor = fromUtc;
        TimeSpan tradable = TimeSpan.Zero;
        while (cursor < toUtc)
        {
            var next = cursor + TradingHoursStep;
            if (next > toUtc)
                next = toUtc;

            var midpoint = cursor + TimeSpan.FromTicks((next - cursor).Ticks / 2);
            if (IsMarketTradable(midpoint, sessions, holidayDates))
                tradable += next - cursor;

            cursor = next;
        }

        return tradable;
    }

    private static bool IsMarketTradable(
        DateTime timestampUtc,
        IReadOnlyDictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)> sessions,
        ISet<DateTime>? holidayDates)
    {
        if (holidayDates?.Contains(timestampUtc.Date) == true)
            return false;

        if (sessions.Count == 0)
            return timestampUtc.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;

        if (!sessions.TryGetValue(timestampUtc.DayOfWeek, out var current))
            return true;

        if (current.Closed)
            return false;

        var time = TimeOnly.FromDateTime(timestampUtc);
        if (current.Start == current.End)
            return true;

        if (current.Start < current.End)
            return time >= current.Start && time < current.End;

        return time >= current.Start || time < current.End;
    }

    private static IReadOnlyDictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)> ParseTradingHours(string? tradingHoursJson)
    {
        if (string.IsNullOrWhiteSpace(tradingHoursJson))
            return new Dictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)>();

        try
        {
            using var doc = JsonDocument.Parse(tradingHoursJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)>();

            var sessions = new Dictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                DayOfWeek? day = prop.Name.ToLowerInvariant() switch
                {
                    "mon" or "monday" => DayOfWeek.Monday,
                    "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
                    "wed" or "wednesday" => DayOfWeek.Wednesday,
                    "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
                    "fri" or "friday" => DayOfWeek.Friday,
                    "sat" or "saturday" => DayOfWeek.Saturday,
                    "sun" or "sunday" => DayOfWeek.Sunday,
                    _ => null,
                };

                if (!day.HasValue || prop.Value.ValueKind != JsonValueKind.String)
                    continue;

                var raw = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                if (raw.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    sessions[day.Value] = (default, default, true);
                    continue;
                }

                var parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !TimeOnly.TryParse(parts[0], out var start)
                    || !TimeOnly.TryParse(parts[1], out var end))
                {
                    continue;
                }

                sessions[day.Value] = (start, end, false);
            }

            return sessions;
        }
        catch (JsonException)
        {
            return new Dictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)>();
        }
    }
}
