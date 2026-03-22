using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// Configuration for an ECN/prime brokerage venue.
/// </summary>
public sealed record EcnVenueConfig
{
    /// <summary>Venue identifier (e.g., "lmax", "currenex", "hotspot", "cboe-fx").</summary>
    public string VenueId            { get; init; } = "";
    /// <summary>FIX host for order routing (or REST endpoint for venues without FIX).</summary>
    public string Host               { get; init; } = "";
    public int    Port               { get; init; }
    public string SenderCompId       { get; init; } = "";
    public string TargetCompId       { get; init; } = "";
    /// <summary>API key or FIX password.</summary>
    public string ApiKey             { get; init; } = "";
    /// <summary>Whether this venue is enabled for trading.</summary>
    public bool   Enabled            { get; init; }
    /// <summary>Maximum lot size per order for this venue.</summary>
    public decimal MaxLotSize        { get; init; } = 50m;
    /// <summary>Minimum lot size for this venue.</summary>
    public decimal MinLotSize        { get; init; } = 0.01m;
    /// <summary>Commission per lot in the account currency.</summary>
    public decimal CommissionPerLot  { get; init; }
}

/// <summary>
/// Represents the best bid/ask from a single ECN venue at a point in time.
/// </summary>
public sealed record EcnQuote(
    string  VenueId,
    string  Symbol,
    decimal Bid,
    decimal Ask,
    decimal BidSize,
    decimal AskSize,
    DateTime Timestamp);

/// <summary>
/// Aggregates quotes from multiple ECN venues and routes orders to the venue
/// offering the best available price (best bid for sells, best ask for buys).
/// </summary>
/// <remarks>
/// This adapter sits between the engine's <see cref="IBrokerOrderExecutor"/> interface
/// and the actual venue connections. It implements smart order routing (SOR):
/// <list type="bullet">
///   <item><b>Quote aggregation:</b> collects top-of-book from all enabled venues.</item>
///   <item><b>Best execution:</b> routes orders to the venue with the tightest spread
///         and sufficient liquidity for the requested size.</item>
///   <item><b>Order splitting:</b> if no single venue can fill the full size, splits
///         across venues ordered by price priority.</item>
///   <item><b>Venue health:</b> tracks fill rates and latency per venue, excludes
///         venues with fill rates below <see cref="MinFillRate"/>.</item>
/// </list>
/// </remarks>
public interface IEcnBrokerAdapter
{
    /// <summary>Registers a venue for aggregation and routing.</summary>
    void RegisterVenue(EcnVenueConfig config);

    /// <summary>Updates the top-of-book quote for a venue/symbol pair.</summary>
    void UpdateQuote(EcnQuote quote);

    /// <summary>Returns the current best aggregated bid/ask across all venues.</summary>
    (decimal Bid, decimal Ask, string BestBidVenue, string BestAskVenue)? GetBestQuote(string symbol);

    /// <summary>
    /// Selects the optimal venue for executing the given order based on current quotes,
    /// venue health, and order size.
    /// </summary>
    EcnRoutingDecision RouteOrder(string symbol, decimal lots, bool isBuy);

    /// <summary>Records a fill outcome for venue health tracking.</summary>
    void RecordFillOutcome(string venueId, bool filled, decimal slippagePips, int latencyMs);
}

/// <summary>The routing decision produced by <see cref="IEcnBrokerAdapter.RouteOrder"/>.</summary>
public sealed record EcnRoutingDecision
{
    /// <summary>Ordered list of (venue, lots) pairs to execute. May split across venues.</summary>
    public required IReadOnlyList<(string VenueId, decimal Lots)> Fills { get; init; }
    /// <summary>Expected fill price based on current quotes.</summary>
    public decimal ExpectedPrice { get; init; }
    /// <summary>Expected total spread cost in pips.</summary>
    public double SpreadCostPips { get; init; }
    /// <summary><c>true</c> if the order can be fully filled across all venues.</summary>
    public bool CanFill { get; init; }
}

[RegisterService(ServiceLifetime.Singleton)]
public sealed class EcnBrokerAdapter : IEcnBrokerAdapter
{
    private const double MinFillRate = 0.80; // exclude venues below 80% fill rate

    private readonly Dictionary<string, EcnVenueConfig> _venues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EcnQuote>       _quotes = new(); // key: "venue|SYMBOL"
    private readonly Dictionary<string, VenueStats>     _stats  = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger<EcnBrokerAdapter> _logger;

    public EcnBrokerAdapter(ILogger<EcnBrokerAdapter> logger)
    {
        _logger = logger;
    }

    public void RegisterVenue(EcnVenueConfig config)
    {
        lock (_lock)
        {
            _venues[config.VenueId] = config;
            _stats[config.VenueId]  = new VenueStats();
        }
        _logger.LogInformation("ECN: registered venue '{Venue}' (host={Host}:{Port})",
            config.VenueId, config.Host, config.Port);
    }

    public void UpdateQuote(EcnQuote quote)
    {
        string key = $"{quote.VenueId}|{quote.Symbol}";
        lock (_lock)
            _quotes[key] = quote;
    }

    public (decimal Bid, decimal Ask, string BestBidVenue, string BestAskVenue)? GetBestQuote(string symbol)
    {
        lock (_lock)
        {
            decimal bestBid = 0, bestAsk = decimal.MaxValue;
            string  bidVenue = "", askVenue = "";

            foreach (var (venueId, config) in _venues)
            {
                if (!config.Enabled) continue;
                string key = $"{venueId}|{symbol}";
                if (!_quotes.TryGetValue(key, out var q)) continue;
                if (DateTime.UtcNow - q.Timestamp > TimeSpan.FromSeconds(5)) continue; // stale

                if (q.Bid > bestBid) { bestBid = q.Bid; bidVenue = venueId; }
                if (q.Ask < bestAsk) { bestAsk = q.Ask; askVenue = venueId; }
            }

            if (bidVenue == "" || askVenue == "")
                return null;

            return (bestBid, bestAsk, bidVenue, askVenue);
        }
    }

    public EcnRoutingDecision RouteOrder(string symbol, decimal lots, bool isBuy)
    {
        lock (_lock)
        {
            // Collect eligible venues with fresh quotes, sorted by price priority
            var candidates = new List<(string VenueId, decimal Price, decimal AvailableSize, EcnQuote Quote)>();

            foreach (var (venueId, config) in _venues)
            {
                if (!config.Enabled) continue;
                string key = $"{venueId}|{symbol}";
                if (!_quotes.TryGetValue(key, out var q)) continue;
                if (DateTime.UtcNow - q.Timestamp > TimeSpan.FromSeconds(5)) continue;

                // Check venue health
                if (_stats.TryGetValue(venueId, out var stats) && stats.FillRate < MinFillRate)
                    continue;

                decimal price = isBuy ? q.Ask : q.Bid;
                decimal size  = isBuy ? q.AskSize : q.BidSize;
                size = Math.Min(size, config.MaxLotSize);

                candidates.Add((venueId, price, size, q));
            }

            if (candidates.Count == 0)
            {
                _logger.LogWarning("ECN routing: no eligible venues for {Symbol}", symbol);
                return new EcnRoutingDecision
                {
                    Fills = [], ExpectedPrice = 0, SpreadCostPips = 0, CanFill = false
                };
            }

            // Sort by best price (lowest ask for buys, highest bid for sells)
            candidates.Sort((a, b) => isBuy
                ? a.Price.CompareTo(b.Price)
                : b.Price.CompareTo(a.Price));

            // Fill greedily across venues
            var fills = new List<(string VenueId, decimal Lots)>();
            decimal remaining = lots;
            decimal weightedPrice = 0;

            foreach (var c in candidates)
            {
                if (remaining <= 0) break;
                decimal fillSize = Math.Min(remaining, c.AvailableSize);
                if (fillSize < _venues[c.VenueId].MinLotSize) continue;

                fills.Add((c.VenueId, fillSize));
                weightedPrice += fillSize * c.Price;
                remaining -= fillSize;
            }

            decimal totalFilled = lots - remaining;
            decimal avgPrice = totalFilled > 0 ? weightedPrice / totalFilled : 0;

            // Compute spread cost
            var bestQuote = GetBestQuote(symbol);
            double spreadPips = 0;
            if (bestQuote.HasValue)
            {
                bool isJpy = symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase);
                double pipMult = isJpy ? 100.0 : 10000.0;
                spreadPips = (double)(bestQuote.Value.Ask - bestQuote.Value.Bid) * pipMult;
            }

            _logger.LogInformation(
                "ECN routing: {Symbol} {Side} {Lots} → {VenueCount} venues, avgPrice={Price:F5}, spread={Spread:F1}pips, canFill={CanFill}",
                symbol, isBuy ? "BUY" : "SELL", lots, fills.Count, avgPrice, spreadPips, remaining <= 0);

            return new EcnRoutingDecision
            {
                Fills         = fills,
                ExpectedPrice = avgPrice,
                SpreadCostPips = spreadPips,
                CanFill       = remaining <= 0,
            };
        }
    }

    public void RecordFillOutcome(string venueId, bool filled, decimal slippagePips, int latencyMs)
    {
        lock (_lock)
        {
            if (!_stats.TryGetValue(venueId, out var stats))
            {
                stats = new VenueStats();
                _stats[venueId] = stats;
            }
            stats.TotalOrders++;
            if (filled) stats.FilledOrders++;
            stats.TotalSlippage += (double)slippagePips;
            stats.TotalLatency  += latencyMs;
        }
    }

    private sealed class VenueStats
    {
        public int    TotalOrders;
        public int    FilledOrders;
        public double TotalSlippage;
        public long   TotalLatency;

        public double FillRate       => TotalOrders > 0 ? (double)FilledOrders / TotalOrders : 1.0;
        public double AvgSlippage    => FilledOrders > 0 ? TotalSlippage / FilledOrders : 0;
        public double AvgLatencyMs   => TotalOrders > 0 ? (double)TotalLatency / TotalOrders : 0;
    }
}
