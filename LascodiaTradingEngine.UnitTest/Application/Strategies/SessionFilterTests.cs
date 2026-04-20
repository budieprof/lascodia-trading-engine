using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

/// <summary>
/// Tests for the two reject-only gate filters wired into StrategyWorker:
/// MultiTimeframeConfirmationFilter and RegimeArchetypeGateFilter.
/// </summary>
public class SessionFilterTests
{
    private static (IReadApplicationDbContext Ctx, ApplicationDbContextFake Inner) NewCtx()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContextFake>()
            .UseInMemoryDatabase($"filter-{Guid.NewGuid()}").Options;
        var ctx = new ApplicationDbContextFake(opts);
        return (ctx, ctx);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // MultiTimeframeConfirmationFilter
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MtfFilter_NoSnapshot_AllowsThrough()
    {
        var (ctx, _) = NewCtx();
        var filter = new MultiTimeframeConfirmationFilter(ctx, NullLogger<MultiTimeframeConfirmationFilter>.Instance);

        var result = await filter.CheckAsync(
            symbol: "EURUSD", signalTimeframe: Timeframe.M15, direction: TradeDirection.Buy,
            asOfUtc: DateTime.UtcNow, ct: CancellationToken.None);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task MtfFilter_HighTfCrisis_RejectsSignal()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        // Place a fresh Crisis snapshot on the higher TF (M15 → H1 via the filter's map).
        inner.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Crisis, DetectedAt = now.AddMinutes(-5),
        });
        await inner.SaveChangesAsync();

        var filter = new MultiTimeframeConfirmationFilter(ctx, NullLogger<MultiTimeframeConfirmationFilter>.Instance);
        var result = await filter.CheckAsync("EURUSD", Timeframe.M15, TradeDirection.Buy, now, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Contains("Crisis", result.RejectionReason);
    }

    [Fact]
    public async Task MtfFilter_HighTfRanging_AllowsSignal()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        inner.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Ranging, DetectedAt = now.AddMinutes(-5),
        });
        await inner.SaveChangesAsync();

        var filter = new MultiTimeframeConfirmationFilter(ctx, NullLogger<MultiTimeframeConfirmationFilter>.Instance);
        var result = await filter.CheckAsync("EURUSD", Timeframe.M15, TradeDirection.Sell, now, CancellationToken.None);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task MtfFilter_StaleSnapshot_TreatedAsColdStart()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        // Snapshot older than the default 6h max-stale threshold
        inner.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Crisis, DetectedAt = now.AddHours(-12),
        });
        await inner.SaveChangesAsync();

        var filter = new MultiTimeframeConfirmationFilter(ctx, NullLogger<MultiTimeframeConfirmationFilter>.Instance);
        var result = await filter.CheckAsync("EURUSD", Timeframe.M15, TradeDirection.Buy, now, CancellationToken.None);

        Assert.True(result.Allowed);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // RegimeArchetypeGateFilter
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RegimeGate_CrisisRegime_RejectsAllTypes()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        inner.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Crisis, DetectedAt = now.AddMinutes(-5),
        });
        await inner.SaveChangesAsync();

        var mapper = new RegimeStrategyMapper();
        var filter = new RegimeArchetypeGateFilter(ctx, mapper, NullLogger<RegimeArchetypeGateFilter>.Instance);
        var result = await filter.CheckAsync("EURUSD", Timeframe.H1,
            StrategyType.MovingAverageCrossover, now, CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task RegimeGate_MismatchedType_Rejects()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        // Ranging regime has RSI + Bollinger + StatArb + VWAP + CompositeML + CalendarEffect —
        // MovingAverageCrossover (a Trending type) should be rejected.
        inner.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Ranging, DetectedAt = now.AddMinutes(-5),
        });
        await inner.SaveChangesAsync();

        var mapper = new RegimeStrategyMapper();
        var filter = new RegimeArchetypeGateFilter(ctx, mapper, NullLogger<RegimeArchetypeGateFilter>.Instance);
        var result = await filter.CheckAsync("EURUSD", Timeframe.H1,
            StrategyType.MovingAverageCrossover, now, CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task RegimeGate_CompatibleType_Allows()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        inner.Set<MarketRegimeSnapshot>().Add(new MarketRegimeSnapshot
        {
            Id = 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Regime = MarketRegime.Trending, DetectedAt = now.AddMinutes(-5),
        });
        await inner.SaveChangesAsync();

        var mapper = new RegimeStrategyMapper();
        var filter = new RegimeArchetypeGateFilter(ctx, mapper, NullLogger<RegimeArchetypeGateFilter>.Instance);
        var result = await filter.CheckAsync("EURUSD", Timeframe.H1,
            StrategyType.MovingAverageCrossover, now, CancellationToken.None);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task RegimeGate_NoSnapshot_TreatedAsColdStartAllow()
    {
        var (ctx, _) = NewCtx();
        var mapper = new RegimeStrategyMapper();
        var filter = new RegimeArchetypeGateFilter(ctx, mapper, NullLogger<RegimeArchetypeGateFilter>.Instance);
        var result = await filter.CheckAsync("EURUSD", Timeframe.H1,
            StrategyType.MovingAverageCrossover, DateTime.UtcNow, CancellationToken.None);

        Assert.True(result.Allowed);
    }
}
