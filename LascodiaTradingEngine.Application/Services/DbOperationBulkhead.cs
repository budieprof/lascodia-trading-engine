using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Default <see cref="IDbOperationBulkhead"/>. One <see cref="SemaphoreSlim"/>
/// per named group; capacities baked in as well-known defaults (operators can
/// grow the fleet later — mutating capacity at runtime is deliberately not
/// supported because it's a race hazard vs. in-flight slot holders).
///
/// <para>
/// Metrics: <c>trading.db_bulkhead.waits</c> counts every acquisition that had
/// to wait; <c>trading.db_bulkhead.wait_ms</c> histograms the wait duration.
/// Both are tagged with the group name so dashboards can show which group is
/// saturated without manual bucketing.
/// </para>
/// </summary>
[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton, typeof(IDbOperationBulkhead))]
public sealed class DbOperationBulkhead : IDbOperationBulkhead
{
    public const string GroupSignalPath  = "signal-path";
    public const string GroupMLTraining  = "ml-training";
    public const string GroupBacktesting = "backtesting";
    public const string GroupOther       = "other";

    // Well-known default capacities. Deliberately sized so the sum exceeds
    // the typical Npgsql MaxPoolSize (100) with some headroom — the bulkhead
    // controls concurrency per logical group, not total connections. If an
    // operator raises MaxPoolSize, these can be raised too.
    private static readonly Dictionary<string, int> DefaultCapacities = new(StringComparer.OrdinalIgnoreCase)
    {
        [GroupSignalPath]  = 60,
        [GroupMLTraining]  = 60,
        [GroupBacktesting] = 40,
        [GroupOther]       = 40,
    };

    private readonly TradingMetrics _metrics;
    private readonly ILogger<DbOperationBulkhead> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.OrdinalIgnoreCase);

    public DbOperationBulkhead(TradingMetrics metrics, ILogger<DbOperationBulkhead> logger)
    {
        _metrics = metrics;
        _logger  = logger;
        foreach (var (group, cap) in DefaultCapacities)
            _semaphores[group] = new SemaphoreSlim(cap, cap);
    }

    public async ValueTask<IDisposable> AcquireAsync(string group, CancellationToken ct = default)
    {
        var sem = _semaphores.GetOrAdd(group,
            _ => new SemaphoreSlim(DefaultCapacities[GroupOther], DefaultCapacities[GroupOther]));

        bool hadToWait = sem.CurrentCount == 0;
        var sw = hadToWait ? Stopwatch.StartNew() : null;

        await sem.WaitAsync(ct).ConfigureAwait(false);

        if (sw is not null)
        {
            sw.Stop();
            _metrics.DbBulkheadWaits.Add(1, new KeyValuePair<string, object?>("group", group));
            _metrics.DbBulkheadWaitMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("group", group));
        }

        return new Slot(sem, group, _logger);
    }

    public int AvailableSlots(string group)
        => _semaphores.TryGetValue(group, out var sem) ? sem.CurrentCount : 0;

    private sealed class Slot : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private readonly string _group;
        private readonly ILogger _logger;
        private int _released;

        public Slot(SemaphoreSlim sem, string group, ILogger logger)
        {
            _sem = sem;
            _group = group;
            _logger = logger;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try { _sem.Release(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DbOperationBulkhead: release failed for group '{Group}'", _group);
            }
        }
    }
}
