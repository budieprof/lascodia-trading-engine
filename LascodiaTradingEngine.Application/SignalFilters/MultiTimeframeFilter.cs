using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Confirms a signal direction by checking SMA20 trend on higher timeframes.
/// Returns true when the majority of applicable higher timeframes agree with the signal direction.
/// </summary>
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
        if (!Enum.TryParse<Timeframe>(primaryTimeframe, ignoreCase: true, out var primaryTf)
            || !HigherTimeframes.TryGetValue(primaryTf, out var higherTfs) || higherTfs.Length == 0)
            return true;  // D1 or unknown — no higher TFs to check

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
                continue;  // Not enough data to confirm

            decimal sma20      = candles.Average(x => x.Close);
            decimal latestClose = candles[0].Close;  // Most recent close (descending order)

            bool confirms = signalDirection == "Buy"
                ? latestClose > sma20    // Uptrend confirms Buy
                : latestClose < sma20;   // Downtrend confirms Sell

            if (confirms)
                confirmations++;
        }

        // Majority of higher TFs must confirm
        return confirmations > higherTfs.Length / 2;
    }
}
