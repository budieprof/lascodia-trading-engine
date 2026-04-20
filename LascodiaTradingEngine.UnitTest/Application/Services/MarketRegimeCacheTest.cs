using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class MarketRegimeCacheTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly MarketRegimeCache _cache;
    private readonly Mock<DbContext> _db = new();

    public MarketRegimeCacheTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _cache = new MarketRegimeCache(_metrics, _timeProvider);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task GetAsync_FirstCall_LoadsLatestSnapshotFromDb()
    {
        var now = DateTime.UtcNow;
        SetSnapshots(
            MakeSnapshot("EURUSD", Timeframe.H1, MarketRegime.Ranging, now.AddMinutes(-30)),
            MakeSnapshot("EURUSD", Timeframe.H1, MarketRegime.Trending, now)); // newer

        var regime = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);
        Assert.Equal(MarketRegime.Trending, regime);
    }

    [Fact]
    public async Task GetAsync_RepeatedCall_ServedFromCache()
    {
        SetSnapshots(MakeSnapshot("EURUSD", Timeframe.H1, MarketRegime.Trending, DateTime.UtcNow));

        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);
        _db.Invocations.Clear();
        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);

        _db.Verify(d => d.Set<MarketRegimeSnapshot>(), Times.Never);
    }

    [Fact]
    public async Task Invalidate_ForcesDbRefresh_OnNextGet()
    {
        SetSnapshots(MakeSnapshot("EURUSD", Timeframe.H1, MarketRegime.Trending, DateTime.UtcNow));
        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);

        _cache.Invalidate("EURUSD", Timeframe.H1);

        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);
        _db.Verify(d => d.Set<MarketRegimeSnapshot>(), Times.Exactly(2));
    }

    [Fact]
    public async Task GetAsync_AbsentSnapshot_CachesNullSentinel()
    {
        SetSnapshots();

        var first  = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);
        _db.Invocations.Clear();
        var second = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        _db.Verify(d => d.Set<MarketRegimeSnapshot>(), Times.Never);
    }

    [Fact]
    public async Task InvalidateSymbol_DropsEveryTimeframeForThatSymbol()
    {
        var now = DateTime.UtcNow;
        SetSnapshots(
            MakeSnapshot("EURUSD", Timeframe.H1, MarketRegime.Trending, now),
            MakeSnapshot("EURUSD", Timeframe.H4, MarketRegime.Ranging, now));

        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);
        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H4, 60, CancellationToken.None);
        _cache.InvalidateSymbol("EURUSD");
        _db.Invocations.Clear();

        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H1, 60, CancellationToken.None);
        _ = await _cache.GetAsync(_db.Object, "EURUSD", Timeframe.H4, 60, CancellationToken.None);
        _db.Verify(d => d.Set<MarketRegimeSnapshot>(), Times.Exactly(2));
    }

    private void SetSnapshots(params MarketRegimeSnapshot[] rows)
    {
        _db.Setup(d => d.Set<MarketRegimeSnapshot>()).Returns(rows.AsQueryable().BuildMockDbSet().Object);
    }

    private static MarketRegimeSnapshot MakeSnapshot(string symbol, Timeframe tf, MarketRegime regime, DateTime at) => new()
    {
        Symbol = symbol,
        Timeframe = tf,
        Regime = regime,
        DetectedAt = at,
    };

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 20, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
