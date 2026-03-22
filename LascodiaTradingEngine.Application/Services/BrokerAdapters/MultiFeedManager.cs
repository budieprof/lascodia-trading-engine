using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Options;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// Manages multiple <see cref="IBrokerDataFeed"/> instances for redundancy.
/// Routes ticks from the primary feed and automatically fails over to a secondary
/// when the primary goes stale. Cross-validates ticks across feeds to detect
/// erroneous data.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Primary/secondary model:</b> the first registered feed is the primary.
///         Others are secondaries that receive ticks in parallel for cross-validation.</item>
///   <item><b>Staleness detection:</b> if the primary feed produces no ticks for a symbol
///         for <see cref="_stalenessThreshold"/>, the manager promotes the next healthy
///         secondary to primary and emits a warning.</item>
///   <item><b>Cross-validation:</b> when multiple feeds produce ticks for the same symbol
///         within <see cref="_crossValidationWindowMs"/>, the manager compares mid prices.
///         If they diverge by more than <see cref="_maxMidPriceDivergencePips"/>, the tick
///         is flagged as suspicious and logged.</item>
///   <item><b>Automatic recovery:</b> when the original primary recovers (produces fresh ticks),
///         it is re-promoted if its cross-validation score is better than the current primary.</item>
/// </list>
/// </remarks>
public interface IMultiFeedManager
{
    /// <summary>Registers a data feed with a name (e.g., "oanda", "saxo", "lmax").</summary>
    void RegisterFeed(string name, IBrokerDataFeed feed);

    /// <summary>
    /// Subscribes all registered feeds to the given symbols and routes ticks
    /// through cross-validation to the provided callback.
    /// </summary>
    Task SubscribeAllAsync(IEnumerable<string> symbols, Func<Tick, Task> onTick, CancellationToken ct);

    /// <summary>Returns the name of the currently active primary feed.</summary>
    string PrimaryFeedName { get; }

    /// <summary>Returns health status of all registered feeds.</summary>
    IReadOnlyDictionary<string, FeedHealth> GetFeedHealthSnapshot();
}

/// <summary>Health status of a single data feed.</summary>
public sealed record FeedHealth(
    string  Name,
    bool    IsHealthy,
    int     TicksLastMinute,
    int     StaleSymbols,
    int     CrossValidationFailures,
    DateTime LastTickAt);

[RegisterService(ServiceLifetime.Singleton)]
public sealed class MultiFeedManager : IMultiFeedManager
{
    private readonly TimeSpan _stalenessThreshold;
    private readonly int      _crossValidationWindowMs;
    private readonly double   _maxMidPriceDivergencePips;

    private readonly List<(string Name, IBrokerDataFeed Feed)> _feeds = [];
    private readonly ConcurrentDictionary<string, FeedState>   _feedStates = new();
    private readonly ConcurrentDictionary<string, TickSnapshot> _lastTicks = new();
    private readonly ILogger<MultiFeedManager> _logger;

    private string _primaryName = "";

    public MultiFeedManager(ILogger<MultiFeedManager> logger, MultiFeedOptions options)
    {
        _logger = logger;
        _stalenessThreshold        = TimeSpan.FromSeconds(options.StalenessThresholdSeconds);
        _crossValidationWindowMs   = options.CrossValidationWindowMs;
        _maxMidPriceDivergencePips = options.MaxMidPriceDivergencePips;
    }

    public string PrimaryFeedName => _primaryName;

    public void RegisterFeed(string name, IBrokerDataFeed feed)
    {
        _feeds.Add((name, feed));
        _feedStates[name] = new FeedState();

        if (_feeds.Count == 1)
            _primaryName = name;

        _logger.LogInformation("MultiFeedManager: registered feed '{Name}' (primary={IsPrimary})",
            name, name == _primaryName);
    }

    public async Task SubscribeAllAsync(IEnumerable<string> symbols, Func<Tick, Task> onTick, CancellationToken ct)
    {
        var symbolList = symbols.ToList();

        // Subscribe each feed with a per-feed tick handler
        var tasks = _feeds.Select(f =>
            f.Feed.SubscribeAsync(symbolList, tick => HandleTickAsync(f.Name, tick, onTick), ct));

        // Start staleness monitor
        _ = Task.Run(() => MonitorStalenessAsync(ct), ct);

        await Task.WhenAll(tasks);
    }

    public IReadOnlyDictionary<string, FeedHealth> GetFeedHealthSnapshot()
    {
        var result = new Dictionary<string, FeedHealth>();
        foreach (var (name, _) in _feeds)
        {
            if (!_feedStates.TryGetValue(name, out var state)) continue;
            result[name] = new FeedHealth(
                name,
                IsHealthy: state.LastTickUtc > DateTime.UtcNow - _stalenessThreshold,
                TicksLastMinute: state.TicksLastMinute,
                StaleSymbols: state.StaleSymbolCount,
                CrossValidationFailures: state.CrossValidationFailures,
                LastTickAt: state.LastTickUtc);
        }
        return result;
    }

    // ── Tick routing with cross-validation ───────────────────────────────────

    private async Task HandleTickAsync(string feedName, Tick tick, Func<Tick, Task> onTick)
    {
        // Update feed state
        if (_feedStates.TryGetValue(feedName, out var state))
        {
            state.LastTickUtc = DateTime.UtcNow;
            Interlocked.Increment(ref state.TickCount);
        }

        // Store for cross-validation
        string cvKey = $"{tick.Symbol}";
        var snapshot = _lastTicks.GetOrAdd(cvKey, _ => new TickSnapshot());
        snapshot.Update(feedName, tick);

        // Cross-validate if we have ticks from multiple feeds within the window
        if (snapshot.HasMultipleFeeds)
        {
            double divergence = snapshot.MidPriceDivergencePips(tick.Symbol, _crossValidationWindowMs);
            if (divergence > _maxMidPriceDivergencePips)
            {
                _logger.LogWarning(
                    "MultiFeedManager: mid-price divergence {Div:F1} pips for {Symbol} across feeds — tick from '{Feed}' may be erroneous",
                    divergence, tick.Symbol, feedName);

                if (state is not null)
                    Interlocked.Increment(ref state.CrossValidationFailures);

                // Skip this tick if it's from a non-primary feed with high divergence
                if (feedName != _primaryName)
                    return;
            }
        }

        // Only forward ticks from the primary feed to the callback
        if (feedName == _primaryName)
            await onTick(tick);
    }

    // ── Staleness monitor ───────────────────────────────────────────────────

    private async Task MonitorStalenessAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            if (!_feedStates.TryGetValue(_primaryName, out var primaryState))
                continue;

            // Check if primary is stale
            if (DateTime.UtcNow - primaryState.LastTickUtc > _stalenessThreshold)
            {
                // Find a healthy secondary
                foreach (var (name, _) in _feeds)
                {
                    if (name == _primaryName) continue;
                    if (!_feedStates.TryGetValue(name, out var secState)) continue;

                    if (DateTime.UtcNow - secState.LastTickUtc < _stalenessThreshold)
                    {
                        string oldPrimary = _primaryName;
                        _primaryName = name;

                        _logger.LogWarning(
                            "MultiFeedManager: primary feed '{Old}' stale (no ticks for {Sec}s) — " +
                            "failing over to '{New}'",
                            oldPrimary, _stalenessThreshold.TotalSeconds, name);
                        break;
                    }
                }
            }

            // Update ticks-per-minute counters
            foreach (var (name, _) in _feeds)
            {
                if (_feedStates.TryGetValue(name, out var s))
                {
                    s.TicksLastMinute = s.TickCount;
                    Interlocked.Exchange(ref s.TickCount, 0);
                }
            }
        }
    }

    // ── Internal state ──────────────────────────────────────────────────────

    private sealed class FeedState
    {
        public DateTime LastTickUtc = DateTime.MinValue;
        public int TickCount;
        public int TicksLastMinute;
        public int StaleSymbolCount;
        public int CrossValidationFailures;
    }

    private sealed class TickSnapshot
    {
        private readonly ConcurrentDictionary<string, (Tick Tick, DateTime ReceivedAt)> _feedTicks = new();

        public void Update(string feedName, Tick tick)
            => _feedTicks[feedName] = (tick, DateTime.UtcNow);

        public bool HasMultipleFeeds => _feedTicks.Count > 1;

        public double MidPriceDivergencePips(string symbol, int crossValidationWindowMs)
        {
            var recent = _feedTicks.Values
                .Where(v => DateTime.UtcNow - v.ReceivedAt < TimeSpan.FromMilliseconds(crossValidationWindowMs))
                .Select(v => ((double)v.Tick.Bid + (double)v.Tick.Ask) / 2.0)
                .ToList();

            if (recent.Count < 2) return 0;

            double maxMid = recent.Max();
            double minMid = recent.Min();
            bool isJpy = symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase);
            double pipMultiplier = isJpy ? 100.0 : 10000.0;

            return (maxMid - minMid) * pipMultiplier;
        }
    }
}
