using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Optimization;

public class OptimizationHelperServicesTest
{
    [Theory]
    [InlineData("")]
    [InlineData("13/01-01/05")]
    [InlineData("abc")]
    [InlineData("01/32-02/01")]
    public void IsInBlackoutPeriod_MalformedInput_ReturnsFalse(string periods)
    {
        Assert.False(OptimizationPolicyHelpers.IsInBlackoutPeriod(periods));
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_IdenticalScores_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.70m, 0.70m, 0.70m },
            out var predictedDecline);

        Assert.False(result);
        Assert.Equal(0m, predictedDecline);
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_AscendingScores_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.80m, 0.70m, 0.60m },
            out _);

        Assert.False(result);
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_FewerThan3Snapshots_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.80m, 0.60m },
            out _);

        Assert.False(result);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 45)]
    [InlineData(5, 255)]
    public void GetRetryEligibilityWindow_ReturnsCorrectTimeSpan(int maxRetries, int expectedMinutes)
    {
        var result = OptimizationPolicyHelpers.GetRetryEligibilityWindow(maxRetries);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), result);
    }

    [Fact]
    public void AreParametersSimilarToAny_ReturnsTrue_ForMatchingCategoricalOnlyParameters()
    {
        const string candidateJson = """{"Mode":"Breakout","Session":"London"}""";
        var parsed = new List<Dictionary<string, JsonElement>>
        {
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(candidateJson)!
        };

        bool isSimilar = OptimizationPolicyHelpers.AreParametersSimilarToAny(candidateJson, parsed, 0.15);

        Assert.True(isSimilar);
    }

    [Fact]
    public void ParseFidelityRungs_MalformedValues_FallsBackToDefault()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs(
            "abc,def",
            NullLogger.Instance,
            "OptimizationHelperServicesTest");

        Assert.Equal([0.25, 0.50], result);
    }

    [Fact]
    public void ParseFidelityRungs_PartiallyValid_KeepsValidValues()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs(
            "abc,0.30,0.60",
            NullLogger.Instance,
            "OptimizationHelperServicesTest");

        Assert.Equal([0.30, 0.60], result);
    }

    [Fact]
    public void ParseFidelityRungs_OutOfRangeValues_Excluded()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs(
            "0,0.50,1.0,1.5",
            NullLogger.Instance,
            "OptimizationHelperServicesTest");

        Assert.Equal([0.50], result);
    }

    [Fact]
    public void DiffConfigSnapshots_NumericPrecision_NoFalsePositive()
    {
        string prior = """{"Version":1,"Config":{"TpeBudget":50,"EmbargoRatio":0.05}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":50,"EmbargoRatio":0.05}}""";

        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);

        Assert.Empty(result);
    }

    [Fact]
    public void DiffConfigSnapshots_DetectsChangedKey()
    {
        string prior = """{"Version":1,"Config":{"TpeBudget":50}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":100}}""";

        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);

        Assert.Single(result);
    }

    [Fact]
    public void DiffConfigSnapshots_DetectsAddedKey()
    {
        string prior = """{"Version":1,"Config":{"TpeBudget":50}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":50,"NewKey":true}}""";

        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);

        Assert.Single(result);
    }

    [Fact]
    public void HasLeaseOwnershipChanged_ReturnsTrue_WhenTokenOrStatusDiffers()
    {
        var expectedToken = Guid.NewGuid();

        Assert.False(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Running, expectedToken));
        Assert.True(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Running, Guid.NewGuid()));
        Assert.True(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Completed, expectedToken));
    }

    [Fact]
    public void ExecutionLeaseHeartbeatInterval_IsShorterThanLeaseDuration()
    {
        var interval = OptimizationRunLeaseManager.GetHeartbeatInterval();

        Assert.True(interval >= TimeSpan.FromMinutes(1));
        Assert.True(interval < TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void BuildRegimeIntervals_And_FilterCandlesByIntervals_ReturnExpectedSlice()
    {
        var snapshots = new List<MarketRegimeSnapshot>
        {
            new() { DetectedAt = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Trending },
            new() { DetectedAt = new DateTime(2026, 03, 03, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Ranging },
            new() { DetectedAt = new DateTime(2026, 03, 05, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Trending },
        };
        var candles = Enumerable.Range(0, 120)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.1m,
                IsClosed = true
            })
            .ToList();

        var intervals = OptimizationRegimeIntervalBuilder.BuildRegimeIntervals(
            snapshots,
            MarketRegime.Trending,
            snapshots[0].DetectedAt,
            new DateTime(2026, 03, 05, 0, 0, 0, DateTimeKind.Utc));
        var filtered = OptimizationRegimeIntervalBuilder.FilterCandlesByIntervals(candles, intervals);

        Assert.Equal(48, filtered.Count);
        Assert.Equal(new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc), filtered.First().Timestamp);
    }
}
