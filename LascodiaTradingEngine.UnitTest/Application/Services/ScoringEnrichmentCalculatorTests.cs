using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class ScoringEnrichmentCalculatorTests
{
    // ────────────────────────────────────────────────────────────────────────
    //  ComputeConformalSet
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeConformalSet_Returns_Null_When_QHat_Out_Of_Range()
    {
        var (set, size) = ScoringEnrichmentCalculator.ComputeConformalSet(0.7, 0.0);
        Assert.Null(set);
        Assert.Null(size);

        (set, size) = ScoringEnrichmentCalculator.ComputeConformalSet(0.7, 1.0);
        Assert.Null(set);
        Assert.Null(size);
    }

    [Fact]
    public void ComputeConformalSet_Returns_Buy_When_High_Probability()
    {
        // calibP=0.9, qHat=0.2 → includeBuy=(0.9≥0.8)=true, includeSell=(0.9≤0.2)=false
        var (set, size) = ScoringEnrichmentCalculator.ComputeConformalSet(0.9, 0.2);
        Assert.Equal("Buy", set);
        Assert.Equal(1, size);
    }

    [Fact]
    public void ComputeConformalSet_Returns_Sell_When_Low_Probability()
    {
        // calibP=0.1, qHat=0.2 → includeBuy=(0.1≥0.8)=false, includeSell=(0.1≤0.2)=true
        var (set, size) = ScoringEnrichmentCalculator.ComputeConformalSet(0.1, 0.2);
        Assert.Equal("Sell", set);
        Assert.Equal(1, size);
    }

    [Fact]
    public void ComputeConformalSet_Returns_Ambiguous_When_Both_Directions_Plausible()
    {
        // calibP=0.5, qHat=0.6 → includeBuy=(0.5≥0.4)=true, includeSell=(0.5≤0.6)=true
        var (set, size) = ScoringEnrichmentCalculator.ComputeConformalSet(0.5, 0.6);
        Assert.Equal("Ambiguous", set);
        Assert.Equal(2, size);
    }

    [Fact]
    public void ComputeConformalSet_Returns_None_When_Neither_Direction()
    {
        // calibP=0.5, qHat=0.1 → includeBuy=(0.5≥0.9)=false, includeSell=(0.5≤0.1)=false
        var (set, size) = ScoringEnrichmentCalculator.ComputeConformalSet(0.5, 0.1);
        Assert.Equal("None", set);
        Assert.Equal(0, size);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeEntropy
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeEntropy_Maximum_At_Point_Five()
    {
        double entropy = ScoringEnrichmentCalculator.ComputeEntropy(0.5);
        Assert.Equal(1.0, entropy, precision: 5);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0.999)]
    public void ComputeEntropy_Near_Zero_At_Extreme_Probabilities(double calibP)
    {
        double entropy = ScoringEnrichmentCalculator.ComputeEntropy(calibP);
        Assert.True(entropy < 0.05, $"Expected near-zero entropy, got {entropy}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void ComputeEntropy_Clamps_Edge_Probabilities(double calibP)
    {
        double entropy = ScoringEnrichmentCalculator.ComputeEntropy(calibP);
        Assert.InRange(entropy, 0.0, 1.0);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeJackknifeInterval
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeJackknifeInterval_Returns_Null_With_Few_Residuals()
    {
        var result = ScoringEnrichmentCalculator.ComputeJackknifeInterval(new double[] { 1.0, 2.0 });
        Assert.Null(result);
    }

    [Fact]
    public void ComputeJackknifeInterval_Returns_Formatted_String()
    {
        var residuals = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();
        var result = ScoringEnrichmentCalculator.ComputeJackknifeInterval(residuals);

        Assert.NotNull(result);
        Assert.StartsWith("±", result);
        Assert.EndsWith("@90%", result);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeOodMahalanobis
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeOodMahalanobis_Returns_Null_When_Variances_Too_Short()
    {
        var (score, isOod) = ScoringEnrichmentCalculator.ComputeOodMahalanobis(
            new float[] { 1f, 2f }, 2, new double[] { 1.0 }, 3.0, 3.0);
        Assert.Null(score);
        Assert.False(isOod);
    }

    [Fact]
    public void ComputeOodMahalanobis_Detects_OOD_When_Score_Exceeds_Threshold()
    {
        // Feature = [10f] with variance = [1.0] → Mahalanobis = 10.0 > 3.0
        var (score, isOod) = ScoringEnrichmentCalculator.ComputeOodMahalanobis(
            new float[] { 10f }, 1, new double[] { 1.0 }, 3.0, 3.0);

        Assert.NotNull(score);
        Assert.True(isOod);
        Assert.True(score!.Value > 3.0);
    }

    [Fact]
    public void ComputeOodMahalanobis_Not_OOD_When_Normal()
    {
        // Feature = [1f] with variance = [1.0] → Mahalanobis = 1.0 < 3.0
        var (score, isOod) = ScoringEnrichmentCalculator.ComputeOodMahalanobis(
            new float[] { 1f }, 1, new double[] { 1.0 }, 3.0, 3.0);

        Assert.NotNull(score);
        Assert.False(isOod);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeMetaLabelScore
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeMetaLabelScore_Returns_Null_When_No_Weights()
    {
        var result = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
            0.7, 0.1, new float[] { 1f, 2f }, 2, Array.Empty<double>(), 0.0);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeMetaLabelScore_Returns_Value_Between_0_And_1()
    {
        // 2 meta features (calibP, ensembleStd) + min(5, 2) = 4 total weights needed
        var weights = new double[] { 1.0, -1.0, 0.5, 0.5 };
        var result = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
            0.7, 0.1, new float[] { 1f, 2f }, 2, weights, 0.0);

        Assert.NotNull(result);
        Assert.InRange(result!.Value, 0m, 1m);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeAbstentionScore
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeAbstentionScore_Returns_Null_When_No_MetaLabel()
    {
        var result = ScoringEnrichmentCalculator.ComputeAbstentionScore(
            0.7, 0.1, null, null, 0.5, new double[] { 1, 1, 1, 1, 1 }, 0.0);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeAbstentionScore_With_5_Weights()
    {
        var result = ScoringEnrichmentCalculator.ComputeAbstentionScore(
            0.7, 0.1, 0.8m, 1.0, 0.3, new double[] { 1, -1, 0.5, 0.2, -0.3 }, 0.0);

        Assert.NotNull(result);
        Assert.InRange(result!.Value, 0m, 1m);
    }

    [Fact]
    public void ComputeAbstentionScore_With_3_Weights()
    {
        var result = ScoringEnrichmentCalculator.ComputeAbstentionScore(
            0.7, 0.1, 0.8m, null, 0.3, new double[] { 1, -1, 0.5 }, 0.0);

        Assert.NotNull(result);
        Assert.InRange(result!.Value, 0m, 1m);
    }

    [Fact]
    public void ComputeAbstentionScore_Returns_Null_With_Wrong_Weight_Count()
    {
        var result = ScoringEnrichmentCalculator.ComputeAbstentionScore(
            0.7, 0.1, 0.8m, null, 0.3, new double[] { 1, -1 }, 0.0);
        Assert.Null(result);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeRegimeRoutingDecision
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeRegimeRoutingDecision_Regime_Specific()
    {
        var result = ScoringEnrichmentCalculator.ComputeRegimeRoutingDecision("Trending", "Trending");
        Assert.Equal("Regime:Trending", result);
    }

    [Fact]
    public void ComputeRegimeRoutingDecision_Global_Fallback()
    {
        var result = ScoringEnrichmentCalculator.ComputeRegimeRoutingDecision("Trending", null);
        Assert.Equal("Global", result);
    }

    [Fact]
    public void ComputeRegimeRoutingDecision_Fallback_When_No_Regime()
    {
        var result = ScoringEnrichmentCalculator.ComputeRegimeRoutingDecision(null, null);
        Assert.Equal("Fallback", result);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeSurvivalAnalysis
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeSurvivalAnalysis_Returns_Null_When_No_Hazard()
    {
        var (bars, rate) = ScoringEnrichmentCalculator.ComputeSurvivalAnalysis(
            new float[] { 1f }, 1, Array.Empty<double>(), Array.Empty<double>());
        Assert.Null(bars);
        Assert.Null(rate);
    }

    [Fact]
    public void ComputeSurvivalAnalysis_Returns_Estimates_With_Valid_Hazard()
    {
        // High hazard = quick arrival → estimatedBars should be small
        var hazard = new double[] { 0.5, 0.5, 0.5, 0.5, 0.5 };
        var (bars, rate) = ScoringEnrichmentCalculator.ComputeSurvivalAnalysis(
            new float[] { 0f }, 1, hazard, new double[] { 0.0 });

        Assert.NotNull(bars);
        Assert.NotNull(rate);
        Assert.True(bars!.Value <= hazard.Length);
        Assert.True(rate!.Value > 0);
    }

    [Fact]
    public void ComputeSurvivalAnalysis_Falls_Back_To_Max_Bars_When_No_Crossing()
    {
        // Very low hazard — survival never drops below 0.5
        var hazard = new double[] { 0.001, 0.001, 0.001 };
        var (bars, rate) = ScoringEnrichmentCalculator.ComputeSurvivalAnalysis(
            new float[] { 0f }, 1, hazard, new double[] { 0.0 });

        Assert.NotNull(bars);
        Assert.Equal(3.0, bars!.Value);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeCounterfactualJson
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeCounterfactualJson_Returns_Null_When_No_Weights()
    {
        var result = ScoringEnrichmentCalculator.ComputeCounterfactualJson(
            new float[] { 1f }, Array.Empty<double[]>(), Array.Empty<double>(),
            null, new[] { "f1" }, 1, 0.7, 0.5);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeCounterfactualJson_Returns_Json_With_Valid_Weights()
    {
        var weights = new[] { new double[] { 2.0, -1.0 } };
        var biases = new double[] { 0.0 };
        var features = new float[] { 1f, 1f };
        var names = new[] { "RSI", "ATR" };

        var result = ScoringEnrichmentCalculator.ComputeCounterfactualJson(
            features, weights, biases, null, names, 2, 0.6, 0.5);

        Assert.NotNull(result);
        Assert.Contains("RSI", result!);
    }

    [Fact]
    public void ComputeCounterfactualJson_Returns_Null_When_Extreme_Probability()
    {
        // calibP very close to 1 → gradient = p*(1-p) < 1e-10 → returns null
        var weights = new[] { new double[] { 1.0 } };
        var result = ScoringEnrichmentCalculator.ComputeCounterfactualJson(
            new float[] { 1f }, weights, new double[] { 0.0 },
            null, new[] { "f1" }, 1, 1.0 - 1e-12, 0.5);
        Assert.Null(result);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeShapContributionsJson
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeShapContributionsJson_Returns_Null_When_No_Feature_Names()
    {
        var result = ScoringEnrichmentCalculator.ComputeShapContributionsJson(
            new float[] { 1f }, new[] { new double[] { 1.0 } },
            null, Array.Empty<string>(), 1, Array.Empty<double>());
        Assert.Null(result);
    }

    [Fact]
    public void ComputeShapContributionsJson_Returns_Json_With_Weights()
    {
        var features = new float[] { 2f, 3f };
        var weights = new[] { new double[] { 0.5, -0.3 } };
        var names = new[] { "RSI", "ATR" };

        var result = ScoringEnrichmentCalculator.ComputeShapContributionsJson(
            features, weights, null, names, 2, Array.Empty<double>());

        Assert.NotNull(result);
        Assert.Contains("RSI", result!);
        Assert.Contains("ATR", result!);
    }

    [Fact]
    public void ComputeShapContributionsJson_Falls_Back_To_Importance_Scores()
    {
        var features = new float[] { 2f, 3f };
        var names = new[] { "RSI", "ATR" };
        var importance = new double[] { 0.7, 0.3 };

        var result = ScoringEnrichmentCalculator.ComputeShapContributionsJson(
            features, Array.Empty<double[]>(), null, names, 2, importance);

        Assert.NotNull(result);
        Assert.Contains("RSI", result!);
    }
}
