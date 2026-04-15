using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

public static partial class StrategyGenerationHelpers
{
    // Market-data shaping helpers shared by screening and feedback paths.

    private static readonly TimeSpan TradingHoursStep = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LivePriceStalenessLimit = TimeSpan.FromHours(2);

    /// <summary>
    /// Measures candle staleness in market-open hours rather than raw wall-clock hours when
    /// trading-session metadata is available.
    /// </summary>
    public static double ComputeEffectiveCandleAgeHours(DateTime lastCandleTimestampUtc, string? tradingHoursJson, DateTime nowUtc)
    {
        if (lastCandleTimestampUtc >= nowUtc)
            return 0;

        double wallClockHours = (nowUtc - lastCandleTimestampUtc).TotalHours;
        if (string.IsNullOrWhiteSpace(tradingHoursJson))
            return wallClockHours;

        try
        {
            var sessions = ParseTradingHours(tradingHoursJson);
            if (sessions.Count == 0)
                return wallClockHours;

            double openHours = 0;
            var cursor = lastCandleTimestampUtc;
            // Walk the interval in fixed steps so overnight and weekend gaps are counted only
            // when the symbol's trading session is actually open.
            while (cursor < nowUtc)
            {
                var next = cursor + TradingHoursStep;
                if (next > nowUtc)
                    next = nowUtc;

                var midpoint = cursor + TimeSpan.FromTicks((next - cursor).Ticks / 2);
                if (IsWithinTradingSession(midpoint, sessions))
                    openHours += (next - cursor).TotalHours;

                cursor = next;
            }

            return openHours;
        }
        catch
        {
            return wallClockHours;
        }
    }

    /// <summary>
    /// Builds screening backtest options from configured defaults, symbol metadata, and any
    /// fresh live-price spread observation.
    /// </summary>
    public static LascodiaTradingEngine.Application.Backtesting.Services.BacktestOptions BuildScreeningOptions(
        string symbol,
        CurrencyPair? pairInfo,
        AssetClass assetClass,
        double screeningSpreadPoints,
        double screeningCommissionPerLot,
        double screeningSlippagePips,
        ILivePriceCache livePriceCache,
        DateTime utcNow)
    {
        var pointSize = pairInfo != null && pairInfo.DecimalPlaces > 0
            ? 1.0m / (decimal)Math.Pow(10, pairInfo.DecimalPlaces)
            : GetDefaultPointSize(assetClass);

        decimal spreadPriceUnits = pointSize * (decimal)screeningSpreadPoints;

        var livePrice = livePriceCache.Get(symbol);
        // Live spreads can be materially wider than static defaults during stressed periods, so
        // prefer the fresher live observation whenever it is wider and still recent.
        if (livePrice.HasValue
            && livePrice.Value.Ask > livePrice.Value.Bid
            && (utcNow - livePrice.Value.Timestamp) < LivePriceStalenessLimit)
        {
            var liveSpread = livePrice.Value.Ask - livePrice.Value.Bid;
            if (liveSpread > spreadPriceUnits)
                spreadPriceUnits = liveSpread;
        }

        decimal commissionPerLot = ScaleCommissionForAssetClass((decimal)screeningCommissionPerLot, assetClass);
        if (pairInfo != null && pairInfo.PipSize > 0)
        {
            decimal pipSizeRatio = pairInfo.PipSize / 10m;
            if (pipSizeRatio > 1.5m)
                commissionPerLot *= pipSizeRatio;
        }

        return new LascodiaTradingEngine.Application.Backtesting.Services.BacktestOptions
        {
            SpreadPriceUnits = spreadPriceUnits,
            CommissionPerLot = commissionPerLot,
            SlippagePriceUnits = pointSize * (decimal)screeningSlippagePips * 10,
            ContractSize = pairInfo?.ContractSize ?? GetDefaultContractSize(assetClass),
        };
    }

    private static Dictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)> ParseTradingHours(string tradingHoursJson)
    {
        using var doc = JsonDocument.Parse(tradingHoursJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return [];

        var sessions = new Dictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var day = prop.Name.ToLowerInvariant() switch
            {
                "mon" or "monday" => DayOfWeek.Monday,
                "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
                "wed" or "wednesday" => DayOfWeek.Wednesday,
                "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
                "fri" or "friday" => DayOfWeek.Friday,
                "sat" or "saturday" => DayOfWeek.Saturday,
                "sun" or "sunday" => DayOfWeek.Sunday,
                _ => (DayOfWeek?)null,
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

    private static bool IsWithinTradingSession(
        DateTime timestampUtc,
        IReadOnlyDictionary<DayOfWeek, (TimeOnly Start, TimeOnly End, bool Closed)> sessions)
    {
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
}
