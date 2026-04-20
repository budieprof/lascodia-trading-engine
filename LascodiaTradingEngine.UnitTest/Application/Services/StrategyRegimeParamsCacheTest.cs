using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class StrategyRegimeParamsCacheTest : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly StrategyRegimeParamsCache _cache;
    private readonly Mock<DbContext> _mockDb = new();

    public StrategyRegimeParamsCacheTest()
    {
        _metrics = new TradingMetrics(_meterFactory);
        _cache = new StrategyRegimeParamsCache(_metrics, _timeProvider);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task GetAsync_FirstCall_HitsDb()
    {
        SetRows(new StrategyRegimeParams
        {
            StrategyId = 1, Regime = MarketRegime.Trending, ParametersJson = "{\"fast\":10}", IsDeleted = false,
        });

        var result = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 60, CancellationToken.None);

        Assert.Equal("{\"fast\":10}", result);
        _mockDb.Verify(d => d.Set<StrategyRegimeParams>(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetAsync_WithinTtl_ServedFromCache_NoDbRefresh()
    {
        SetRows(new StrategyRegimeParams
        {
            StrategyId = 1, Regime = MarketRegime.Trending, ParametersJson = "{\"fast\":10}",
        });

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 60, CancellationToken.None);
        _mockDb.Invocations.Clear();

        _timeProvider.Advance(TimeSpan.FromSeconds(10));
        var cached = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 60, CancellationToken.None);

        Assert.Equal("{\"fast\":10}", cached);
        _mockDb.Verify(d => d.Set<StrategyRegimeParams>(), Times.Never);
    }

    [Fact]
    public async Task GetAsync_AfterTtlExpiry_RefreshesFromDb()
    {
        SetRows(new StrategyRegimeParams
        {
            StrategyId = 1, Regime = MarketRegime.Trending, ParametersJson = "{\"fast\":10}",
        });

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 30, CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromSeconds(31));

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 30, CancellationToken.None);

        _mockDb.Verify(d => d.Set<StrategyRegimeParams>(), Times.Exactly(2));
    }

    [Fact]
    public async Task Invalidate_ForcesDbRefreshOnNextGet()
    {
        SetRows(new StrategyRegimeParams
        {
            StrategyId = 1, Regime = MarketRegime.Trending, ParametersJson = "{\"fast\":10}",
        });

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 120, CancellationToken.None);
        _cache.Invalidate(1, MarketRegime.Trending);

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 120, CancellationToken.None);

        _mockDb.Verify(d => d.Set<StrategyRegimeParams>(), Times.Exactly(2));
    }

    [Fact]
    public async Task InvalidateAll_DropsEveryRegimeForStrategy()
    {
        // Populate two regime entries for strategy 1
        SetRows(
            new StrategyRegimeParams { StrategyId = 1, Regime = MarketRegime.Trending, ParametersJson = "{\"a\":1}" },
            new StrategyRegimeParams { StrategyId = 1, Regime = MarketRegime.Ranging, ParametersJson = "{\"a\":2}" });

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 120, CancellationToken.None);
        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Ranging,  120, CancellationToken.None);

        _cache.InvalidateAll(1);
        _mockDb.Invocations.Clear();

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 120, CancellationToken.None);
        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Ranging,  120, CancellationToken.None);

        _mockDb.Verify(d => d.Set<StrategyRegimeParams>(), Times.Exactly(2));
    }

    [Fact]
    public async Task GetAsync_TtlZero_SkipsCache_EveryCallHitsDb()
    {
        SetRows(new StrategyRegimeParams
        {
            StrategyId = 1, Regime = MarketRegime.Trending, ParametersJson = "{\"fast\":10}",
        });

        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 0, CancellationToken.None);
        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 0, CancellationToken.None);
        _ = await _cache.GetAsync(_mockDb.Object, 1, MarketRegime.Trending, 0, CancellationToken.None);

        _mockDb.Verify(d => d.Set<StrategyRegimeParams>(), Times.Exactly(3));
    }

    [Fact]
    public async Task GetAsync_NoRow_ReturnsNull_AndCachesTheMiss()
    {
        SetRows(); // No rows at all

        var first  = await _cache.GetAsync(_mockDb.Object, 99, MarketRegime.Trending, 120, CancellationToken.None);
        _mockDb.Invocations.Clear();
        var second = await _cache.GetAsync(_mockDb.Object, 99, MarketRegime.Trending, 120, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        // Null is cached too — second call must not hit DB.
        _mockDb.Verify(d => d.Set<StrategyRegimeParams>(), Times.Never);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void SetRows(params StrategyRegimeParams[] rows)
    {
        _mockDb.Setup(d => d.Set<StrategyRegimeParams>())
               .Returns(rows.AsQueryable().BuildMockDbSet().Object);
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
