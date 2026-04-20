using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class StrategyMetricsCacheTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly StrategyMetricsCache _cache;
    private readonly Mock<DbContext> _mockDb = new();

    public StrategyMetricsCacheTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _cache = new StrategyMetricsCache(_metrics, _timeProvider);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task GetManyAsync_EmptyInput_ReturnsEmptyAndHitsDbZeroTimes()
    {
        SetupSnapshots([]);

        var result = await _cache.GetManyAsync(_mockDb.Object, Array.Empty<long>(), 60, CancellationToken.None);

        Assert.Empty(result);
        _mockDb.Verify(d => d.Set<StrategyPerformanceSnapshot>(), Times.Never);
    }

    [Fact]
    public async Task GetManyAsync_FirstCall_IsMiss_ReturnsSnapshotFromDb()
    {
        var snapshots = new List<StrategyPerformanceSnapshot>
        {
            MakeSnapshot(id: 1, sharpe: 1.4m, health: StrategyHealthStatus.Healthy, at: DateTime.UtcNow.AddMinutes(-1)),
            MakeSnapshot(id: 2, sharpe: 0.9m, health: StrategyHealthStatus.Degrading, at: DateTime.UtcNow.AddMinutes(-2)),
        };
        SetupSnapshots(snapshots);

        var result = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1, 2 }, 60, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(1.4m, result[1].Sharpe);
        Assert.Equal(StrategyHealthStatus.Healthy, result[1].HealthStatus);
        Assert.Equal(StrategyHealthStatus.Degrading, result[2].HealthStatus);
    }

    [Fact]
    public async Task GetManyAsync_SecondCallWithinTtl_IsHit_NoDbRefresh()
    {
        SetupSnapshots(new List<StrategyPerformanceSnapshot>
        {
            MakeSnapshot(id: 1, sharpe: 1.2m, health: StrategyHealthStatus.Healthy),
        });

        _ = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);
        _mockDb.Invocations.Clear();

        // Advance only 10s — inside the 60s TTL.
        _timeProvider.Advance(TimeSpan.FromSeconds(10));
        var second = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);

        Assert.Single(second);
        Assert.Equal(1.2m, second[1].Sharpe);
        // No second DB hit — cache should have served it.
        _mockDb.Verify(d => d.Set<StrategyPerformanceSnapshot>(), Times.Never);
    }

    [Fact]
    public async Task GetManyAsync_AfterTtlExpiry_RefreshesFromDb()
    {
        var snapshots = new List<StrategyPerformanceSnapshot>
        {
            MakeSnapshot(id: 1, sharpe: 0.7m, health: StrategyHealthStatus.Healthy),
        };
        SetupSnapshots(snapshots);

        _ = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromSeconds(61)); // TTL expired

        _ = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);

        _mockDb.Verify(d => d.Set<StrategyPerformanceSnapshot>(), Times.Exactly(2));
    }

    [Fact]
    public async Task Invalidate_ForcesDbRefreshOnNextGet()
    {
        SetupSnapshots(new List<StrategyPerformanceSnapshot>
        {
            MakeSnapshot(id: 1, sharpe: 1.1m, health: StrategyHealthStatus.Healthy),
        });

        _ = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);
        _cache.Invalidate(1, trigger: "backtest_completed");

        _ = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);

        _mockDb.Verify(d => d.Set<StrategyPerformanceSnapshot>(), Times.Exactly(2));
    }

    [Fact]
    public async Task GetManyAsync_StrategyWithoutSnapshot_CachesSentinel_NoRepeatedDbQueries()
    {
        SetupSnapshots([]); // No snapshots in DB

        var first = await _cache.GetManyAsync(_mockDb.Object, new long[] { 99 }, 60, CancellationToken.None);
        Assert.Equal(0m, first[99].Sharpe);
        Assert.Null(first[99].HealthStatus);

        _mockDb.Invocations.Clear();
        var second = await _cache.GetManyAsync(_mockDb.Object, new long[] { 99 }, 60, CancellationToken.None);

        Assert.Equal(0m, second[99].Sharpe);
        Assert.Null(second[99].HealthStatus);
        // Sentinel was cached — no DB re-query.
        _mockDb.Verify(d => d.Set<StrategyPerformanceSnapshot>(), Times.Never);
    }

    [Fact]
    public async Task GetManyAsync_UsesMostRecentSnapshot_PerStrategy()
    {
        var older = MakeSnapshot(id: 1, sharpe: 0.1m, health: StrategyHealthStatus.Degrading, at: DateTime.UtcNow.AddHours(-5));
        var newer = MakeSnapshot(id: 1, sharpe: 2.3m, health: StrategyHealthStatus.Critical, at: DateTime.UtcNow.AddMinutes(-1));
        SetupSnapshots(new List<StrategyPerformanceSnapshot> { older, newer });

        var result = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);

        Assert.Equal(2.3m, result[1].Sharpe);
        Assert.Equal(StrategyHealthStatus.Critical, result[1].HealthStatus);
    }

    [Fact]
    public async Task GetManyAsync_PartialCacheHit_RefreshesOnlyMissingIds()
    {
        SetupSnapshots(new List<StrategyPerformanceSnapshot>
        {
            MakeSnapshot(id: 1, sharpe: 0.5m, health: StrategyHealthStatus.Healthy),
            MakeSnapshot(id: 2, sharpe: 1.5m, health: StrategyHealthStatus.Healthy),
        });

        _ = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1 }, 60, CancellationToken.None);
        // At this point strategy 1 is cached; strategy 2 is not.

        var combined = await _cache.GetManyAsync(_mockDb.Object, new long[] { 1, 2 }, 60, CancellationToken.None);

        Assert.Equal(0.5m, combined[1].Sharpe);
        Assert.Equal(1.5m, combined[2].Sharpe);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void SetupSnapshots(IEnumerable<StrategyPerformanceSnapshot> snapshots)
    {
        _mockDb.Setup(d => d.Set<StrategyPerformanceSnapshot>())
               .Returns(snapshots.AsQueryable().BuildMockDbSet().Object);
    }

    private static StrategyPerformanceSnapshot MakeSnapshot(
        long id,
        decimal sharpe,
        StrategyHealthStatus health,
        DateTime? at = null) => new()
        {
            StrategyId   = id,
            SharpeRatio  = sharpe,
            HealthStatus = health,
            EvaluatedAt  = at ?? DateTime.UtcNow,
            IsDeleted    = false,
        };

    // Minimal TimeProvider stub for TTL tests — base time moves only via Advance.
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 20, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options)
        {
            var m = new Meter(options);
            _meters.Add(m);
            return m;
        }
        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
        }
    }
}
