using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.Application.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

public class RejectionReasonAggregatorTests
{
    private static (IReadApplicationDbContext Ctx, ApplicationDbContextFake Inner) NewCtx()
    {
        var opts = new DbContextOptionsBuilder<ApplicationDbContextFake>()
            .UseInMemoryDatabase($"aggregator-{Guid.NewGuid()}").Options;
        var ctx = new ApplicationDbContextFake(opts);
        return (ctx, ctx);
    }

    private static void SeedFailures(ApplicationDbContextFake ctx, StrategyType type, int count,
        ScreeningFailureReason reason, DateTime at)
    {
        for (int i = 0; i < count; i++)
        {
            ctx.Set<StrategyGenerationFailure>().Add(new StrategyGenerationFailure
            {
                Id            = ctx.Set<StrategyGenerationFailure>().Count() + i + 1,
                CandidateId   = $"c-{Guid.NewGuid()}",
                CandidateHash = "h",
                StrategyType  = type,
                Symbol        = "EURUSD",
                Timeframe     = Timeframe.H1,
                FailureStage  = "Screening",
                FailureReason = reason.ToString(),
                CreatedAtUtc  = at,
            });
        }
        ctx.SaveChanges();
    }

    [Fact]
    public async Task EmptyWindow_ReturnsEmptyDistribution()
    {
        var (ctx, _) = NewCtx();
        var agg = new RejectionReasonAggregator(ctx, NullLogger<RejectionReasonAggregator>.Instance);
        var result = await agg.LoadAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task UnderfitDominant_ClassifiedAsUnderfit()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        // 15 samples, 12 Underfit → 80% dominance → Underfit
        SeedFailures(inner, StrategyType.MovingAverageCrossover, 8, ScreeningFailureReason.ZeroTradesIS, now.AddDays(-1));
        SeedFailures(inner, StrategyType.MovingAverageCrossover, 4, ScreeningFailureReason.IsThreshold,  now.AddDays(-2));
        SeedFailures(inner, StrategyType.MovingAverageCrossover, 3, ScreeningFailureReason.Degradation,  now.AddDays(-3));

        var agg = new RejectionReasonAggregator(ctx, NullLogger<RejectionReasonAggregator>.Instance);
        var result = await agg.LoadAsync(CancellationToken.None);

        Assert.Equal(RejectionClass.Underfit, result[StrategyType.MovingAverageCrossover]);
    }

    [Fact]
    public async Task OverfitDominant_ClassifiedAsOverfit()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        SeedFailures(inner, StrategyType.RSIReversion, 10, ScreeningFailureReason.DeflatedSharpe,     now.AddDays(-1));
        SeedFailures(inner, StrategyType.RSIReversion, 5,  ScreeningFailureReason.MonteCarloShuffle, now.AddDays(-2));
        SeedFailures(inner, StrategyType.RSIReversion, 2,  ScreeningFailureReason.ZeroTradesIS,       now.AddDays(-3));

        var agg = new RejectionReasonAggregator(ctx, NullLogger<RejectionReasonAggregator>.Instance);
        var result = await agg.LoadAsync(CancellationToken.None);

        Assert.Equal(RejectionClass.Overfit, result[StrategyType.RSIReversion]);
    }

    [Fact]
    public async Task MixedDistribution_ClassifiedAsMixed()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        SeedFailures(inner, StrategyType.BreakoutScalper, 6, ScreeningFailureReason.ZeroTradesIS, now.AddDays(-1));
        SeedFailures(inner, StrategyType.BreakoutScalper, 6, ScreeningFailureReason.Degradation,   now.AddDays(-2));

        var agg = new RejectionReasonAggregator(ctx, NullLogger<RejectionReasonAggregator>.Instance);
        var result = await agg.LoadAsync(CancellationToken.None);

        Assert.Equal(RejectionClass.Mixed, result[StrategyType.BreakoutScalper]);
    }

    [Fact]
    public async Task BelowMinSampleThreshold_ClassifiedAsUnknown()
    {
        var (ctx, inner) = NewCtx();
        var now = DateTime.UtcNow;
        SeedFailures(inner, StrategyType.MomentumTrend, 3, ScreeningFailureReason.ZeroTradesIS, now.AddDays(-1));

        var agg = new RejectionReasonAggregator(ctx, NullLogger<RejectionReasonAggregator>.Instance);
        var result = await agg.LoadAsync(CancellationToken.None);

        Assert.Equal(RejectionClass.Unknown, result[StrategyType.MomentumTrend]);
    }

    [Fact]
    public void ClassifyMap_IsExhaustiveOverScreeningFailureReason()
    {
        // Every enum value should map to one of the four RejectionClass values — either
        // known Underfit, known Overfit, or Unknown (explicit fall-through). If a new
        // failure reason is ever added without updating the aggregator, this test fails
        // loudly rather than silently classifying as Unknown.
        foreach (ScreeningFailureReason reason in Enum.GetValues<ScreeningFailureReason>())
        {
            var cls = RejectionReasonAggregator.Classify(reason.ToString());
            Assert.True(
                cls == RejectionClass.Underfit || cls == RejectionClass.Overfit || cls == RejectionClass.Unknown,
                $"{reason} mapped to unexpected class {cls}");
        }
    }
}
