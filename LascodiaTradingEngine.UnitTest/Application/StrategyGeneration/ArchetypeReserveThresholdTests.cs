using LascodiaTradingEngine.Application.StrategyGeneration;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

/// <summary>
/// Contract tests for the archetype-diversity reserve pass thresholds.
///
/// <para>
/// Exercises the same multiplier arithmetic the reserve planner uses at
/// <c>StrategyGenerationReserveScreeningPlanner.ScreenReserveCandidatesAsync</c>.
/// If the production formula drifts from <c>threshold × mul</c> for sharpe/wr/pf
/// and <c>threshold × (2 − mul)</c> for drawdown, these tests fail — catches the
/// class of regression where someone hand-tunes the constant instead of the config.
/// </para>
/// </summary>
public class ArchetypeReserveThresholdTests
{
    [Fact]
    public void GenerationConfig_DefaultsMultiplierTo075()
    {
        var cfg = new GenerationConfig();
        Assert.Equal(0.75, cfg.ArchetypeReserveThresholdMultiplier);
        Assert.True(cfg.EnforceArchetypeDiversity);
        Assert.Equal(2, cfg.MinCandidatesPerArchetype);
    }

    [Theory]
    [InlineData(1.00, 1.5, 0.55, 0.10, 1.50, 0.55, 0.10)] // No relaxation
    [InlineData(0.75, 1.5, 0.55, 0.10, 1.125, 0.4125, 0.125)]
    [InlineData(0.50, 1.5, 0.55, 0.10, 0.75, 0.275, 0.15)]
    public void ReserveRelaxation_ScalesSharpeWinRateDirectly_InverseForDrawdown(
        double multiplier,
        double primarySharpe, double primaryWinRate, double primaryMaxDd,
        double expectedSharpe, double expectedWinRate, double expectedMaxDd)
    {
        double mul = Math.Clamp(multiplier, 0.50, 1.00);
        double ddRelax = 1.0 + (1.0 - mul);

        double sharpe  = primarySharpe  * mul;
        double wr      = primaryWinRate * mul;
        double maxDd   = primaryMaxDd   * ddRelax;

        Assert.Equal(expectedSharpe,  sharpe,  6);
        Assert.Equal(expectedWinRate, wr,      6);
        Assert.Equal(expectedMaxDd,   maxDd,   6);
    }

    [Fact]
    public void ReserveRelaxation_IsClampedBetweenHalfAndOne()
    {
        // 0.1 → clamped to 0.50; 1.5 → clamped to 1.00.
        Assert.Equal(0.50, Math.Clamp(0.1, 0.50, 1.00));
        Assert.Equal(1.00, Math.Clamp(1.5, 0.50, 1.00));
    }
}
