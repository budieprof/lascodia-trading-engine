using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

/// <summary>
/// Covers the rejection-reason → mutation-strength mapping used by
/// <see cref="EvolutionaryStrategyGenerator"/>. Mutation uses different step sizes depending
/// on the dominant rejection class for a strategy type over the rolling 30-day window:
/// Underfit → wider step (break out of dead zones), Overfit → narrower step (refine locally),
/// Mixed / Unknown → baseline.
/// </summary>
public class MutationStrengthResolutionTests
{
    [Fact]
    public void UnknownType_ReturnsBaseline()
    {
        var strength = EvolutionaryStrategyGenerator.ResolveMutationStrength(
            StrategyType.MovingAverageCrossover,
            new Dictionary<StrategyType, RejectionClass>());

        Assert.Equal(0.15, strength, precision: 4);
    }

    [Fact]
    public void Underfit_WidensMutationStep()
    {
        var classes = new Dictionary<StrategyType, RejectionClass>
        {
            [StrategyType.BreakoutScalper] = RejectionClass.Underfit,
        };
        var strength = EvolutionaryStrategyGenerator.ResolveMutationStrength(
            StrategyType.BreakoutScalper, classes);

        Assert.Equal(0.25, strength, precision: 4);
    }

    [Fact]
    public void Overfit_NarrowsMutationStep()
    {
        var classes = new Dictionary<StrategyType, RejectionClass>
        {
            [StrategyType.RSIReversion] = RejectionClass.Overfit,
        };
        var strength = EvolutionaryStrategyGenerator.ResolveMutationStrength(
            StrategyType.RSIReversion, classes);

        Assert.Equal(0.08, strength, precision: 4);
    }

    [Fact]
    public void MixedOrUnknown_ReturnsBaseline()
    {
        var classes = new Dictionary<StrategyType, RejectionClass>
        {
            [StrategyType.MomentumTrend]          = RejectionClass.Mixed,
            [StrategyType.BollingerBandReversion] = RejectionClass.Unknown,
        };

        Assert.Equal(0.15, EvolutionaryStrategyGenerator.ResolveMutationStrength(
            StrategyType.MomentumTrend, classes), precision: 4);
        Assert.Equal(0.15, EvolutionaryStrategyGenerator.ResolveMutationStrength(
            StrategyType.BollingerBandReversion, classes), precision: 4);
    }

    [Fact]
    public void MutateParameters_RespectsProvidedStrength()
    {
        // When strength is tight (~0.01), mutated integer params should land within ±1
        // of their originals for any RNG seed; when wide (~0.50), the spread should be larger.
        var rng = new Random(42);
        var tight = EvolutionaryStrategyGenerator.MutateParameters(
            """{"FastPeriod":20,"SlowPeriod":100}""", rng, strength: 0.01, out _);
        Assert.NotNull(tight);
        Assert.Contains("FastPeriod", tight);

        var wide = EvolutionaryStrategyGenerator.MutateParameters(
            """{"FastPeriod":20,"SlowPeriod":100}""", new Random(42), strength: 0.5, out _);
        Assert.NotNull(wide);
        // At ±50% the slow period should move more than at ±1% — not a tight statistical
        // claim, but enough to catch a regression where the strength parameter is ignored.
        Assert.NotEqual(tight, wide);
    }
}
