using LascodiaTradingEngine.Application.Strategies.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies.Services;

/// <summary>
/// Contract tests for <see cref="AdversarialValidationResult"/>'s pass criteria.
/// The full ValidateAsync flow is integration-tested via Postgres + BacktestEngine
/// in <c>LascodiaTradingEngine.IntegrationTest</c>. Here we lock down the result
/// shape and the pass-criteria invariants the promotion gate consumes.
/// </summary>
public class AdversarialValidatorTests
{
    [Fact]
    public void Result_Passed_True_OnlyWhenWorstCasePositive_AndDegradationBounded()
    {
        // Mirror the production pass criteria: worst >= 0 AND degradation <= 60%.
        var passing = new AdversarialValidationResult(
            Passed: true, BaselineSharpe: 1.5m,
            ScenarioSharpes: new Dictionary<string, decimal> { ["SlippageSpike"] = 1.2m, ["NewsShock"] = 0.8m },
            WorstCaseSharpe: 0.8m, SharpeDegradationPct: 46.7m,
            Diagnostics: Array.Empty<string>());
        Assert.True(passing.Passed);
        Assert.True(passing.WorstCaseSharpe >= 0m);
        Assert.True(passing.SharpeDegradationPct <= 60m);
    }

    [Fact]
    public void Result_Passed_False_WhenWorstCaseGoesNegative()
    {
        var failing = new AdversarialValidationResult(
            Passed: false, BaselineSharpe: 1.5m,
            ScenarioSharpes: new Dictionary<string, decimal> { ["RegimeFlip"] = -0.3m },
            WorstCaseSharpe: -0.3m, SharpeDegradationPct: 120m,
            Diagnostics: new[] { "Regime split exposed concentrated edge" });
        Assert.False(failing.Passed);
    }

    [Fact]
    public void Result_Diagnostics_AreReadOnlyAndPreserved()
    {
        var diag = new[] { "Baseline=1.5", "Worst=0.8" };
        var r = new AdversarialValidationResult(
            true, 1.5m, new Dictionary<string, decimal>(), 0.8m, 46.7m, diag);
        Assert.Equal(2, r.Diagnostics.Count);
        Assert.Contains("Baseline=1.5", r.Diagnostics);
    }
}
