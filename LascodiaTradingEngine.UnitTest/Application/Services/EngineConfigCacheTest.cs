using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class EngineConfigCacheTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly EngineConfigCache _cache;
    private readonly Mock<DbContext> _db = new();

    public EngineConfigCacheTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _cache = new EngineConfigCache(_metrics, _timeProvider);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task GetRawAsync_FirstCall_LoadsFromDbAndCaches()
    {
        SetRows(new EngineConfig { Key = "Foo", Value = "42" });

        var first  = await _cache.GetRawAsync(_db.Object, "Foo", 60, CancellationToken.None);
        _db.Invocations.Clear();
        var second = await _cache.GetRawAsync(_db.Object, "Foo", 60, CancellationToken.None);

        Assert.Equal("42", first);
        Assert.Equal("42", second);
        _db.Verify(d => d.Set<EngineConfig>(), Times.Never); // second served from cache
    }

    [Fact]
    public async Task GetRawAsync_MissingKey_CachesNull_NoRepeatedDbHits()
    {
        SetRows();

        var first  = await _cache.GetRawAsync(_db.Object, "DoesNotExist", 60, CancellationToken.None);
        _db.Invocations.Clear();
        var second = await _cache.GetRawAsync(_db.Object, "DoesNotExist", 60, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        _db.Verify(d => d.Set<EngineConfig>(), Times.Never);
    }

    [Fact]
    public async Task GetRawAsync_AfterTtlExpiry_RefreshesFromDb()
    {
        SetRows(new EngineConfig { Key = "Foo", Value = "42" });
        _ = await _cache.GetRawAsync(_db.Object, "Foo", 30, CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromSeconds(31));

        _ = await _cache.GetRawAsync(_db.Object, "Foo", 30, CancellationToken.None);

        _db.Verify(d => d.Set<EngineConfig>(), Times.Exactly(2));
    }

    [Fact]
    public async Task Invalidate_DropsCachedEntry()
    {
        SetRows(new EngineConfig { Key = "Foo", Value = "42" });
        _ = await _cache.GetRawAsync(_db.Object, "Foo", 60, CancellationToken.None);

        _cache.Invalidate("Foo");

        _ = await _cache.GetRawAsync(_db.Object, "Foo", 60, CancellationToken.None);
        _db.Verify(d => d.Set<EngineConfig>(), Times.Exactly(2));
    }

    [Fact]
    public async Task GetIntAsync_ParsesValue()
    {
        SetRows(new EngineConfig { Key = "Some:Key", Value = "42" });
        Assert.Equal(42, await _cache.GetIntAsync(_db.Object, "Some:Key", 0, 60, CancellationToken.None));
    }

    [Fact]
    public async Task GetBoolAsync_ParsesValue()
    {
        SetRows(new EngineConfig { Key = "Some:Key", Value = "true" });
        Assert.True(await _cache.GetBoolAsync(_db.Object, "Some:Key", false, 60, CancellationToken.None));
    }

    [Fact]
    public async Task GetRawAsync_ZeroTtl_BypassesCache()
    {
        SetRows(new EngineConfig { Key = "Foo", Value = "42" });

        _ = await _cache.GetRawAsync(_db.Object, "Foo", 0, CancellationToken.None);
        _ = await _cache.GetRawAsync(_db.Object, "Foo", 0, CancellationToken.None);

        _db.Verify(d => d.Set<EngineConfig>(), Times.Exactly(2));
    }

    private void SetRows(params EngineConfig[] rows)
    {
        _db.Setup(d => d.Set<EngineConfig>()).Returns(rows.AsQueryable().BuildMockDbSet().Object);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 20, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
