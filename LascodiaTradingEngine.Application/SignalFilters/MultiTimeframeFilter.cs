using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Confirms a signal direction by checking trend on higher timeframes.
/// Returns true when the majority of applicable higher timeframes agree with the signal direction.
/// Uses a configurable SMA period (default 50) for trend determination.
/// D1 signals return false (unconfirmed) since no higher timeframe exists to validate against.
/// </summary>
[RegisterService]
public class MultiTimeframeFilter : IMultiTimeframeFilter
{
    private static readonly Timeframe[] TimeframeHierarchy =
        [Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.H1, Timeframe.H4, Timeframe.D1];

    private static readonly Dictionary<Timeframe, Timeframe[]> HigherTimeframes = new()
    {
        [Timeframe.M1]  = [Timeframe.M15, Timeframe.H1],
        [Timeframe.M5]  = [Timeframe.H1, Timeframe.H4],
        [Timeframe.M15] = [Timeframe.H1, Timeframe.H4],
        [Timeframe.H1]  = [Timeframe.H4, Timeframe.D1],
        [Timeframe.H4]  = [Timeframe.D1],
        [Timeframe.D1]  = [],
    };

    /// <summary>Default SMA period for trend confirmation on higher timeframes.</summary>
    private const int DefaultSmaPeriod = 50;

    private readonly IReadApplicationDbContext _context;

    public MultiTimeframeFilter(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> IsConfirmedAsync(
        string symbol,
        string signalDirection,
        string primaryTimeframe,
        CancellationToken ct)
    {
        var (confirmations, total) = await CountConfirmationsAsync(symbol, signalDirection, primaryTimeframe, ct);
        // No higher timeframes available (e.g. D1 signals) — cannot confirm, return false
        // to force downstream callers to handle the unconfirmed case explicitly.
        if (total == 0) return false;
        return confirmations > total / 2;
    }

    public async Task<decimal> GetConfirmationStrengthAsync(
        string symbol,
        string signalDirection,
        string primaryTimeframe,
        CancellationToken ct)
    {
        var (confirmations, total) = await CountConfirmationsAsync(symbol, signalDirection, primaryTimeframe, ct);
        // No higher timeframes available — return 0 (no confirmation) instead of 1.0
        if (total == 0) return 0m;
        return (decimal)confirmations / total;
    }

    private async Task<(int Confirmations, int Total)> CountConfirmationsAsync(
        string symbol,
        string signalDirection,
        string primaryTimeframe,
        CancellationToken ct)
    {
        if (!Enum.TryParse<Timeframe>(primaryTimeframe, ignoreCase: true, out var primaryTf)
            || !HigherTimeframes.TryGetValue(primaryTf, out var higherTfs) || higherTfs.Length == 0)
            return (0, 0);

        int confirmations = 0;
        int smaPeriod = DefaultSmaPeriod;

        foreach (var tf in higherTfs)
        {
            var candles = await _context.GetDbContext()
                .Set<Domain.Entities.Candle>()
                .Where(x => x.Symbol == symbol && x.Timeframe == tf && !x.IsDeleted)
                .OrderByDescending(x => x.Timestamp)
                .Take(smaPeriod)
                .ToListAsync(ct);

            if (candles.Count < smaPeriod)
                continue;

            decimal sma         = candles.Average(x => x.Close);
            decimal latestClose = candles[0].Close;

            bool confirms = signalDirection == "Buy"
                ? latestClose > sma
                : latestClose < sma;

            if (confirms)
                confirmations++;
        }

        return (confirmations, higherTfs.Length);
    }
}
