using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.BrokerAdapters;

/// <summary>
/// Tracks historical fill quality (slippage, latency) per broker×symbol×hour and
/// recommends the best broker for a given order context.
/// </summary>
/// <remarks>
/// Loaded from <see cref="ExecutionQualityLog"/> on startup and updated after every fill.
/// The tracker maintains rolling P50 slippage per (broker, symbol, hour-of-day) bucket.
/// <c>BrokerFailoverService</c> consults this tracker before routing orders.
/// </remarks>
public interface IBrokerQualityTracker
{
    /// <summary>
    /// Returns the broker name with the lowest historical slippage for the given
    /// symbol at the current hour-of-day, or <c>null</c> if no data is available.
    /// </summary>
    string? GetBestBroker(string symbol);

    /// <summary>Records a fill for future quality tracking.</summary>
    void RecordFill(string brokerName, string symbol, decimal slippagePips, int latencyMs);

    /// <summary>Loads historical fill quality data from the database.</summary>
    Task LoadHistoryAsync(CancellationToken ct);
}

public sealed class BrokerQualityTracker : IBrokerQualityTracker
{
    // Key: "brokerName|SYMBOL|hour" → rolling slippage values (last 100)
    private readonly ConcurrentDictionary<string, RollingWindow> _fillData = new();
    private const int WindowSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BrokerQualityTracker> _logger;

    public BrokerQualityTracker(
        IServiceScopeFactory scopeFactory,
        ILogger<BrokerQualityTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public string? GetBestBroker(string symbol)
    {
        int hour = DateTime.UtcNow.Hour;
        string suffix = $"|{symbol.ToUpperInvariant()}|{hour}";

        string? bestBroker = null;
        double  bestMedian = double.MaxValue;

        foreach (var kvp in _fillData)
        {
            if (!kvp.Key.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            double median = kvp.Value.MedianSlippage;
            if (median < bestMedian)
            {
                bestMedian = median;
                bestBroker = kvp.Key[..kvp.Key.IndexOf('|')];
            }
        }

        if (bestBroker is not null)
            _logger.LogDebug(
                "BrokerQualityTracker: best broker for {Symbol} at hour {Hour} = {Broker} (median slippage={Slip:F2})",
                symbol, hour, bestBroker, bestMedian);

        return bestBroker;
    }

    public void RecordFill(string brokerName, string symbol, decimal slippagePips, int latencyMs)
    {
        int hour = DateTime.UtcNow.Hour;
        string key = $"{brokerName}|{symbol.ToUpperInvariant()}|{hour}";

        var window = _fillData.GetOrAdd(key, _ => new RollingWindow(WindowSize));
        window.Add((double)slippagePips);

        _logger.LogDebug(
            "BrokerQualityTracker: recorded fill {Broker}/{Symbol}/h{Hour} slip={Slip:F2} latency={Lat}ms",
            brokerName, symbol, hour, slippagePips, latencyMs);
    }

    public async Task LoadHistoryAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readDb      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var ctx         = readDb.GetDbContext();

        var cutoff = DateTime.UtcNow.AddDays(-30);

        var logs = await ctx.Set<ExecutionQualityLog>()
            .Where(l => !l.IsDeleted && l.RecordedAt >= cutoff)
            .AsNoTracking()
            .Select(l => new { l.Symbol, l.SlippagePips, l.RecordedAt })
            .ToListAsync(ct);

        // We don't have BrokerName on ExecutionQualityLog, so use "default" as the broker key
        foreach (var log in logs)
        {
            int hour   = log.RecordedAt.Hour;
            string key = $"default|{log.Symbol.ToUpperInvariant()}|{hour}";
            var window = _fillData.GetOrAdd(key, _ => new RollingWindow(WindowSize));
            window.Add((double)log.SlippagePips);
        }

        _logger.LogInformation(
            "BrokerQualityTracker: loaded {Count} historical fills into {Buckets} buckets",
            logs.Count, _fillData.Count);
    }

    private sealed class RollingWindow
    {
        private readonly double[] _values;
        private int _count;
        private int _head;

        public RollingWindow(int capacity) => _values = new double[capacity];

        public void Add(double value)
        {
            _values[_head] = value;
            _head = (_head + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }

        public double MedianSlippage
        {
            get
            {
                if (_count == 0) return double.MaxValue;
                var sorted = new double[_count];
                Array.Copy(_values, sorted, _count);
                Array.Sort(sorted);
                return sorted[_count / 2];
            }
        }
    }
}
