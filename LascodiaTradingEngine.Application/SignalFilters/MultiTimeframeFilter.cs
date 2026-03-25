using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Confirms a signal direction by checking SMA20 trend on higher timeframes.
/// Returns true when the majority of applicable higher timeframes agree with the signal direction.
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
        if (total == 0) return true;
        return confirmations > total / 2;
    }

    public async Task<decimal> GetConfirmationStrengthAsync(
        string symbol,
        string signalDirection,
        string primaryTimeframe,
        CancellationToken ct)
    {
        var (confirmations, total) = await CountConfirmationsAsync(symbol, signalDirection, primaryTimeframe, ct);
        if (total == 0) return 1.0m;
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

        foreach (var tf in higherTfs)
        {
            var candles = await _context.GetDbContext()
                .Set<Domain.Entities.Candle>()
                .Where(x => x.Symbol == symbol && x.Timeframe == tf && !x.IsDeleted)
                .OrderByDescending(x => x.Timestamp)
                .Take(20)
                .ToListAsync(ct);

            if (candles.Count < 20)
                continue;

            decimal sma20      = candles.Average(x => x.Close);
            decimal latestClose = candles[0].Close;

            bool confirms = signalDirection == "Buy"
                ? latestClose > sma20
                : latestClose < sma20;

            if (confirms)
                confirmations++;
        }

        return (confirmations, higherTfs.Length);
    }
}
