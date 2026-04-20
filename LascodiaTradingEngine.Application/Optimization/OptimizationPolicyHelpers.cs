using System.Text.Json;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationPolicyHelpers
{
    internal static Timeframe? GetHigherTimeframe(Timeframe tf) => tf switch
    {
        Timeframe.M1 => Timeframe.M5,
        Timeframe.M5 => Timeframe.M15,
        Timeframe.M15 => Timeframe.H1,
        Timeframe.H1 => Timeframe.H4,
        Timeframe.H4 => Timeframe.D1,
        _ => null,
    };

    internal static bool IsRegimeCompatibleWithStrategy(StrategyType strategyType, MarketRegimeEnum higherTfRegime)
    {
        if (strategyType is StrategyType.MovingAverageCrossover or StrategyType.MACDDivergence or StrategyType.MomentumTrend)
            return higherTfRegime is MarketRegimeEnum.Trending or MarketRegimeEnum.Breakout;

        if (strategyType is StrategyType.RSIReversion or StrategyType.BollingerBandReversion)
            return higherTfRegime is MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility;

        if (strategyType is StrategyType.BreakoutScalper or StrategyType.SessionBreakout)
            return higherTfRegime is MarketRegimeEnum.Trending or MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Breakout;

        if (strategyType is StrategyType.CalendarEffect)
            return higherTfRegime is MarketRegimeEnum.Ranging or MarketRegimeEnum.LowVolatility;

        if (strategyType is StrategyType.NewsFade)
            return higherTfRegime is MarketRegimeEnum.HighVolatility or MarketRegimeEnum.Breakout or MarketRegimeEnum.Trending;

        if (strategyType is StrategyType.CarryTrade)
            return higherTfRegime is MarketRegimeEnum.Trending or MarketRegimeEnum.LowVolatility;

        return true;
    }

    internal static bool IsMeaningfullyDeteriorating(
        IReadOnlyList<decimal> recentSnapshots,
        out decimal predictedDecline)
    {
        predictedDecline = 0m;
        if (recentSnapshots.Count < 3)
            return false;

        int n = recentSnapshots.Count;
        double sumW = 0;
        double sumWx = 0;
        double sumWy = 0;
        double sumWxx = 0;
        double sumWxy = 0;

        for (int i = 0; i < n; i++)
        {
            int x = n - 1 - i;
            double y = (double)recentSnapshots[i];
            double w = 1.0 + x;
            sumW += w;
            sumWx += w * x;
            sumWy += w * y;
            sumWxx += w * x * x;
            sumWxy += w * x * y;
        }

        double denom = sumW * sumWxx - sumWx * sumWx;
        if (denom == 0)
            return false;

        double slope = (sumW * sumWxy - sumWx * sumWy) / denom;
        double avgScore = recentSnapshots.Average(s => (double)s);
        double decline = Math.Abs(slope) * n;
        predictedDecline = (decimal)decline;

        return slope < 0 && decline > avgScore * 0.10;
    }

    internal static TimeSpan GetRetryEligibilityWindow(int maxRetryAttempts)
    {
        int normalizedAttempts = Math.Max(1, maxRetryAttempts);
        int maxEligibleBackoffMinutes = 15 << (normalizedAttempts - 1);
        return TimeSpan.FromMinutes(maxEligibleBackoffMinutes + 15);
    }

    internal static bool AreParametersSimilarToAny(
        string candidateJson,
        List<Dictionary<string, JsonElement>> otherParsed,
        double threshold)
    {
        if (string.IsNullOrWhiteSpace(candidateJson) || otherParsed.Count == 0)
            return false;

        Dictionary<string, JsonElement>? a;
        try
        {
            a = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(candidateJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (a is null || a.Count == 0)
            return false;

        foreach (var b in otherParsed)
        {
            if (a.Count != b.Count || !a.Keys.All(b.ContainsKey))
                continue;

            int matched = 0;
            int compared = 0;
            bool mismatch = false;
            bool sawNonNumeric = false;

            foreach (var (key, valA) in a)
            {
                var valB = b[key];
                if (!TryGetNumericValue(valA, out double dA) || !TryGetNumericValue(valB, out double dB))
                {
                    sawNonNumeric = true;
                    if (valA.ToString() != valB.ToString())
                    {
                        mismatch = true;
                        break;
                    }

                    continue;
                }

                compared++;
                double denom = Math.Max(Math.Abs(dA), Math.Abs(dB));
                if (denom == 0.0)
                {
                    matched++;
                    continue;
                }

                if (Math.Abs(dA - dB) / denom <= threshold)
                    matched++;
            }

            if (!mismatch && (compared > 0 ? matched == compared : sawNonNumeric))
                return true;
        }

        return false;
    }

    internal static double[] ParseFidelityRungs(
        string rungs,
        ILogger logger,
        string componentName)
    {
        double[] defaultRungs = [0.25, 0.50];
        if (string.IsNullOrWhiteSpace(rungs))
            return defaultRungs;

        var parts = rungs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<double>();
        var malformed = new List<string>();

        foreach (var part in parts)
        {
            if (double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out double val) && val > 0 && val < 1.0)
                parsed.Add(val);
            else
                malformed.Add(part);
        }

        if (malformed.Count > 0)
        {
            logger.LogWarning(
                "{Component}: SuccessiveHalvingRungs contains malformed values ({Values}) — these were ignored. Valid values must be between 0 and 1 exclusive",
                componentName,
                string.Join(", ", malformed.Select(v => $"'{v}'")));
        }

        if (parsed.Count == 0)
        {
            logger.LogWarning(
                "{Component}: SuccessiveHalvingRungs '{Raw}' produced no valid fidelity levels — using default (0.25, 0.50)",
                componentName,
                rungs);
            return defaultRungs;
        }

        parsed.Sort();
        return parsed.ToArray();
    }

    internal static bool IsInBlackoutPeriod(string blackoutPeriods, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(blackoutPeriods))
            return false;

        int todayOrdinal = utcNow.Month * 100 + utcNow.Day;

        foreach (var period in blackoutPeriods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = period.Split('-');
            if (parts.Length != 2)
                continue;
            if (!TryParseMonthDay(parts[0], out int startOrdinal))
                continue;
            if (!TryParseMonthDay(parts[1], out int endOrdinal))
                continue;

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

    private static bool TryGetNumericValue(JsonElement value, out double numericValue)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDouble(out numericValue);

        numericValue = default;
        return false;
    }

    private static bool TryParseMonthDay(string s, out int ordinal)
    {
        ordinal = 0;
        var md = s.Split('/');
        if (md.Length != 2)
            return false;
        if (!int.TryParse(md[0], out int month) || !int.TryParse(md[1], out int day))
            return false;
        if (month < 1 || month > 12 || day < 1 || day > 31)
            return false;

        ordinal = month * 100 + day;
        return true;
    }
}
