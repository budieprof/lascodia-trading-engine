using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Utilities;

internal sealed record TradingDayBaseline(
    DateTime TradingDayStartUtc,
    decimal StartOfDayEquity,
    string Source);

internal static class TradingDayBoundaryHelper
{
    internal static DateTime GetTradingDayStartUtc(DateTime nowUtc, int rolloverMinuteOfDayUtc)
    {
        var utc = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc
            : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        var rolloverToday = utc.Date.AddMinutes(rolloverMinuteOfDayUtc);
        return utc >= rolloverToday
            ? rolloverToday
            : rolloverToday.AddDays(-1);
    }

    internal static string FormatTradingDayKey(DateTime tradingDayStartUtc)
        => tradingDayStartUtc.ToString("O", CultureInfo.InvariantCulture);

    internal static async Task<TradingDayBaseline?> ResolveStartOfDayEquityAsync(
        DbContext db,
        long accountId,
        DateTime nowUtc,
        TradingDayOptions options,
        CancellationToken ct)
    {
        var tradingDayStartUtc = GetTradingDayStartUtc(nowUtc, options.RolloverMinuteOfDayUtc);
        var tradingDayEndUtc = tradingDayStartUtc.AddDays(1);

        var firstAttribution = await db.Set<AccountPerformanceAttribution>()
            .Where(a => a.TradingAccountId == accountId
                     && a.AttributionDate >= tradingDayStartUtc
                     && a.AttributionDate < tradingDayEndUtc
                     && !a.IsDeleted)
            .OrderBy(a => a.AttributionDate)
            .FirstOrDefaultAsync(ct);
        if (firstAttribution is not null && firstAttribution.StartOfDayEquity > 0)
        {
            return new TradingDayBaseline(
                tradingDayStartUtc,
                firstAttribution.StartOfDayEquity,
                $"Attribution:{firstAttribution.AttributionDate:O}");
        }

        var previousAttribution = await db.Set<AccountPerformanceAttribution>()
            .Where(a => a.TradingAccountId == accountId
                     && a.AttributionDate < tradingDayStartUtc
                     && !a.IsDeleted)
            .OrderByDescending(a => a.AttributionDate)
            .FirstOrDefaultAsync(ct);
        if (previousAttribution is not null && previousAttribution.EndOfDayEquity > 0)
        {
            return new TradingDayBaseline(
                tradingDayStartUtc,
                previousAttribution.EndOfDayEquity,
                $"PreviousAttribution:{previousAttribution.AttributionDate:O}");
        }

        var nearestSnapshot = await ResolveNearestBoundarySnapshotAsync(
            db,
            accountId,
            tradingDayStartUtc,
            TimeSpan.FromMinutes(options.BrokerSnapshotBoundaryToleranceMinutes),
            ct);

        if (nearestSnapshot is not null && nearestSnapshot.Equity > 0)
        {
            return new TradingDayBaseline(
                tradingDayStartUtc,
                nearestSnapshot.Equity,
                $"BrokerSnapshot:{nearestSnapshot.ReportedAt:O}");
        }

        return null;
    }

    private static async Task<BrokerAccountSnapshot?> ResolveNearestBoundarySnapshotAsync(
        DbContext db,
        long accountId,
        DateTime tradingDayStartUtc,
        TimeSpan tolerance,
        CancellationToken ct)
    {
        if (tolerance < TimeSpan.Zero)
            return null;

        var snapshotBefore = await db.Set<BrokerAccountSnapshot>()
            .Where(s => s.TradingAccountId == accountId
                     && s.ReportedAt <= tradingDayStartUtc
                     && !s.IsDeleted)
            .OrderByDescending(s => s.ReportedAt)
            .FirstOrDefaultAsync(ct);

        var snapshotAfter = await db.Set<BrokerAccountSnapshot>()
            .Where(s => s.TradingAccountId == accountId
                     && s.ReportedAt >= tradingDayStartUtc
                     && !s.IsDeleted)
            .OrderBy(s => s.ReportedAt)
            .FirstOrDefaultAsync(ct);

        BrokerAccountSnapshot? best = null;
        TimeSpan bestDistance = TimeSpan.MaxValue;

        if (snapshotBefore is not null)
        {
            var distance = tradingDayStartUtc - snapshotBefore.ReportedAt;
            if (distance <= tolerance)
            {
                best = snapshotBefore;
                bestDistance = distance;
            }
        }

        if (snapshotAfter is not null)
        {
            var distance = snapshotAfter.ReportedAt - tradingDayStartUtc;
            if (distance <= tolerance && distance < bestDistance)
                best = snapshotAfter;
        }

        return best;
    }
}
