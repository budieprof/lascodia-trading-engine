using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Point-in-time economic-event proximity features for a currency pair. News
/// shocks dominate short-term FX volatility; a model that knows "NFP in 45 min"
/// or "just had ECB decision 10 min ago" can size down or abstain around event
/// windows that would otherwise blow its stop.
///
/// <para>
/// For EURUSD the provider loads High/Medium impact events where
/// <c>Currency ∈ {EUR, USD}</c> and derives three bounded scalars:
/// </para>
///
/// <list type="bullet">
///   <item><c>HoursToNextHighImpact</c> — normalized [0, 1] where 0 = imminent,
///         1 = no High event within 24h.</item>
///   <item><c>HoursSinceLastHighImpact</c> — normalized [0, 1] where 0 = just
///         had one, 1 = nothing in the last 24h.</item>
///   <item><c>HighMediumPendingNext6h</c> — count of Medium+ events in the next
///         6h, clipped to [0, 5] then normalized to [0, 1].</item>
/// </list>
///
/// <para>
/// The V3 feature vector (see <see cref="CrossAssetFeatureProvider"/>)
/// appends these three slots after the V2 37-feature vector + 3 cross-asset
/// slots, producing a 43-feature input.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class EconomicEventFeatureProvider
{
    public const int EventSlotCount = 3;

    public static readonly string[] EventFeatureNames =
    [
        "HoursToNextHighImpactNorm",
        "HoursSinceLastHighImpactNorm",
        "HighMedPendingNext6hNorm",
    ];

    private readonly IServiceScopeFactory _scopeFactory;

    public EconomicEventFeatureProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Loads relevant events for a symbol once (two currencies' High+Medium events
    /// within ±48h of the backtest window) and returns a lookup that the sample
    /// builder can hit per-bar without further DB traffic. Empty lookup when the
    /// symbol is shorter than 6 chars or the table is empty for the window.
    /// </summary>
    public async Task<EventLookup> LoadForSymbolAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        if (symbol.Length < 6) return EventLookup.Empty;

        var currencies = new[]
        {
            symbol[..3].ToUpperInvariant(),
            symbol.Substring(3, 3).ToUpperInvariant(),
        };

        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // Pad window by 48h so "since last" and "to next" are accurate at edges.
        var loadStart = fromUtc.AddHours(-48);
        var loadEnd   = toUtc.AddHours(48);

        var events = await readCtx.GetDbContext()
            .Set<EconomicEvent>()
            .AsNoTracking()
            .Where(e => !e.IsDeleted
                     && currencies.Contains(e.Currency)
                     && (e.Impact == EconomicImpact.High || e.Impact == EconomicImpact.Medium)
                     && e.ScheduledAt >= loadStart
                     && e.ScheduledAt <= loadEnd)
            .OrderBy(e => e.ScheduledAt)
            .Select(e => new EventRow(e.ScheduledAt, e.Impact == EconomicImpact.High))
            .ToListAsync(ct);

        return new EventLookup(events);
    }

    /// <summary>
    /// Preloaded event series sorted by scheduled time. Supplies per-timestamp
    /// features in O(log N) via binary search. Safe for concurrent reads.
    /// </summary>
    public sealed class EventLookup
    {
        public static readonly EventLookup Empty = new(Array.Empty<EventRow>());

        private readonly EventRow[] _events;

        public EventLookup(IEnumerable<EventRow> events)
        {
            _events = events.OrderBy(e => e.ScheduledAt).ToArray();
        }

        public EventFeatureSnapshot SnapshotAt(DateTime asOfUtc)
        {
            if (_events.Length == 0)
                return new EventFeatureSnapshot(1f, 1f, 0f);

            // Binary-search for the first event at or after asOfUtc
            int idx = Array.BinarySearch(
                _events,
                new EventRow(asOfUtc, false),
                EventRowComparer.Instance);
            if (idx < 0) idx = ~idx;

            // Next High-impact event
            float hoursToNextHigh = 24f;
            for (int j = idx; j < _events.Length; j++)
            {
                if (!_events[j].IsHigh) continue;
                double hours = (_events[j].ScheduledAt - asOfUtc).TotalHours;
                if (hours > 24) break;
                hoursToNextHigh = (float)Math.Max(0, hours);
                break;
            }

            // Last High-impact event
            float hoursSinceLastHigh = 24f;
            for (int j = idx - 1; j >= 0; j--)
            {
                if (!_events[j].IsHigh) continue;
                double hours = (asOfUtc - _events[j].ScheduledAt).TotalHours;
                if (hours > 24) break;
                hoursSinceLastHigh = (float)Math.Max(0, hours);
                break;
            }

            // Count of Medium+ events in the next 6h
            int pending6h = 0;
            for (int j = idx; j < _events.Length; j++)
            {
                double hours = (_events[j].ScheduledAt - asOfUtc).TotalHours;
                if (hours < 0) continue;
                if (hours > 6) break;
                pending6h++;
            }
            pending6h = Math.Clamp(pending6h, 0, 5);

            return new EventFeatureSnapshot(
                hoursToNextHigh / 24f,
                hoursSinceLastHigh / 24f,
                pending6h / 5f);
        }

        private sealed class EventRowComparer : IComparer<EventRow>
        {
            public static readonly EventRowComparer Instance = new();
            public int Compare(EventRow x, EventRow y) => x.ScheduledAt.CompareTo(y.ScheduledAt);
        }
    }

    public readonly record struct EventRow(DateTime ScheduledAt, bool IsHigh);
}

/// <summary>Three-scalar snapshot populated by <see cref="EconomicEventFeatureProvider.EventLookup.SnapshotAt"/>.</summary>
public readonly record struct EventFeatureSnapshot(
    float HoursToNextHighNormalized,
    float HoursSinceLastHighNormalized,
    float HighMedPending6hNormalized);
