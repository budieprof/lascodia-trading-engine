using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace LascodiaTradingEngine.Application.Workers;

public interface IMLConformalRegimeResolver
{
    Task<IRegimeTimeline> LoadAsync(
        DbContext db,
        IReadOnlyCollection<(string Symbol, Timeframe Timeframe)> contexts,
        DateTime windowStartUtc,
        CancellationToken ct);
}

public interface IRegimeTimeline
{
    /// <summary>
    /// Resolve the regime active at <paramref name="whenUtc"/> for the given context.
    /// Returns <c>null</c> when no snapshot exists at or before that time (e.g. the
    /// log predates regime detection for that pair).
    /// </summary>
    global::LascodiaTradingEngine.Domain.Enums.MarketRegime? RegimeAt(string symbol, Timeframe timeframe, DateTime whenUtc);
}

public sealed class MLConformalRegimeResolver : IMLConformalRegimeResolver
{
    public async Task<IRegimeTimeline> LoadAsync(
        DbContext db,
        IReadOnlyCollection<(string Symbol, Timeframe Timeframe)> contexts,
        DateTime windowStartUtc,
        CancellationToken ct)
    {
        if (contexts.Count == 0)
            return EmptyRegimeTimeline.Instance;

        // Bucket symbol/timeframe pairs to keep the EF query simple. Snapshots near the
        // window boundary still need to be loaded so the resolver can answer for log
        // timestamps shortly after windowStart — fetch a small buffer back.
        var symbols = contexts.Select(c => c.Symbol).Distinct(StringComparer.Ordinal).ToList();
        var timeframes = contexts.Select(c => c.Timeframe).Distinct().ToList();
        var inScope = new HashSet<(string, Timeframe)>(contexts);
        var bufferStart = windowStartUtc - TimeSpan.FromDays(7);

        var rows = await db.Set<MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted
                        && symbols.Contains(s.Symbol)
                        && timeframes.Contains(s.Timeframe)
                        && s.DetectedAt >= bufferStart)
            .Select(s => new RegimeSnapshotRow(s.Symbol, s.Timeframe, s.DetectedAt, s.Regime))
            .ToListAsync(ct);

        var timeline = new Dictionary<(string, Timeframe), List<RegimeSnapshotRow>>(inScope.Count);
        foreach (var row in rows)
        {
            if (!inScope.Contains((row.Symbol, row.Timeframe))) continue;
            if (!timeline.TryGetValue((row.Symbol, row.Timeframe), out var list))
                timeline[(row.Symbol, row.Timeframe)] = list = new List<RegimeSnapshotRow>();
            list.Add(row);
        }
        foreach (var list in timeline.Values)
            list.Sort(static (a, b) => a.DetectedAt.CompareTo(b.DetectedAt));

        return new RegimeTimeline(timeline);
    }

    private readonly record struct RegimeSnapshotRow(
        string Symbol,
        Timeframe Timeframe,
        DateTime DetectedAt,
        global::LascodiaTradingEngine.Domain.Enums.MarketRegime Regime);

    private sealed class RegimeTimeline : IRegimeTimeline
    {
        private readonly Dictionary<(string, Timeframe), List<RegimeSnapshotRow>> _byContext;

        public RegimeTimeline(Dictionary<(string, Timeframe), List<RegimeSnapshotRow>> byContext)
        {
            _byContext = byContext;
        }

        public global::LascodiaTradingEngine.Domain.Enums.MarketRegime? RegimeAt(string symbol, Timeframe timeframe, DateTime whenUtc)
        {
            if (!_byContext.TryGetValue((symbol, timeframe), out var snapshots) || snapshots.Count == 0)
                return null;

            // Binary search for the largest DetectedAt <= whenUtc. The list is sorted
            // ascending, so we want the rightmost element whose DetectedAt is in range.
            int lo = 0, hi = snapshots.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (snapshots[mid].DetectedAt <= whenUtc)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best < 0 ? null : snapshots[best].Regime;
        }
    }

    private sealed class EmptyRegimeTimeline : IRegimeTimeline
    {
        public static readonly EmptyRegimeTimeline Instance = new();
        public global::LascodiaTradingEngine.Domain.Enums.MarketRegime? RegimeAt(string symbol, Timeframe timeframe, DateTime whenUtc) => null;
    }
}
