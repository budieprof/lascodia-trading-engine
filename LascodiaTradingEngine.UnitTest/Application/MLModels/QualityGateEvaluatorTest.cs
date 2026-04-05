using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

public class QualityGateEvaluatorTest
{
    private static QualityGateEvaluator.QualityGateInput DefaultPassingInput() => new(
        Accuracy: 0.65, ExpectedValue: 0.05, BrierScore: 0.20, SharpeRatio: 1.5,
        F1: 0.60, OobAccuracy: 0.62, WfStdAccuracy: 0.03, Ece: 0.08, BrierSkillScore: 0.15,
        MinAccuracy: 0.55, MinExpectedValue: 0.0, MaxBrierScore: 0.25, MinSharpeRatio: 0.5,
        MinF1Score: 0.30, MaxWfStdDev: 0.15, MaxEce: 0.15, MinBrierSkillScore: 0.0,
        MinQualityRetentionRatio: 0.85, ParentOobAccuracy: 0.60,
        IsTrending: false, TrendingMinAccuracy: 0.65, TrendingMinEV: 0.02,
        EvBypassMinEV: 0.10, EvBypassMinSharpe: 0.50, BrierBypassMinEV: 0.10, BrierBypassMinSharpe: 1.0);

    [Fact]
    public void AllGatesPass_ReturnsTrue()
    {
        var result = QualityGateEvaluator.Evaluate(DefaultPassingInput());

        Assert.True(result.Passed);
        Assert.True(result.F1Passed);
        Assert.False(result.BrierBypassed);
        Assert.False(result.QualityRegressionFailed);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void AccuracyBelowMin_ReturnsFalse()
    {
        var input = DefaultPassingInput() with { Accuracy = 0.45 };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("accuracy=", result.FailureReason);
    }

    [Fact]
    public void EVBelowMin_ReturnsFalse()
    {
        var input = DefaultPassingInput() with { ExpectedValue = -0.05 };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("ev=", result.FailureReason);
    }

    [Fact]
    public void BrierAboveMax_ReturnsFalse()
    {
        var input = DefaultPassingInput() with { BrierScore = 0.30 };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("brier=", result.FailureReason);
    }

    [Fact]
    public void SharpeBelow_ReturnsFalse()
    {
        var input = DefaultPassingInput() with { SharpeRatio = 0.3 };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("sharpe=", result.FailureReason);
    }

    [Fact]
    public void WfStdAboveMax_ReturnsFalse()
    {
        var input = DefaultPassingInput() with { WfStdAccuracy = 0.20 };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("wfStd=", result.FailureReason);
    }

    [Fact]
    public void BrierBypass_HighEVAndSharpe_Passes()
    {
        // Brier = 0.2550 exceeds MaxBrierScore = 0.25, but within 5% tolerance
        // when EV and Sharpe are high enough to trigger the bypass.
        var input = DefaultPassingInput() with
        {
            BrierScore = 0.2550,
            ExpectedValue = 0.15,
            SharpeRatio = 1.5
        };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.True(result.Passed);
        Assert.True(result.BrierBypassed);
        Assert.Equal(0.25 * 1.05, result.EffectiveBrierCeiling, 6);
    }

    [Fact]
    public void F1Bypass_TrendingRegime_Passes()
    {
        // F1 = 0.0 would normally fail, but in trending regime with high accuracy + EV it passes.
        var input = DefaultPassingInput() with
        {
            IsTrending = true,
            F1 = 0.0,
            Accuracy = 0.70,
            ExpectedValue = 0.05
        };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.True(result.Passed);
        Assert.True(result.F1Passed);
    }

    [Fact]
    public void F1Bypass_EVBased_Passes()
    {
        // F1 = 0.0 but EV = 0.12 >= 0.10 and Sharpe = 0.6 >= 0.50 triggers EV bypass.
        var input = DefaultPassingInput() with
        {
            F1 = 0.0,
            ExpectedValue = 0.12,
            SharpeRatio = 0.6
        };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.True(result.Passed);
        Assert.True(result.F1Passed);
        Assert.True(result.EvBypassF1);
    }

    [Fact]
    public void OobRegression_ReturnsFalse()
    {
        // OOB = 0.50, ParentOOB = 0.65, Ratio = 0.85 -> threshold = 0.65 * 0.85 = 0.5525
        // 0.50 < 0.5525 -> fails
        var input = DefaultPassingInput() with
        {
            OobAccuracy = 0.50,
            ParentOobAccuracy = 0.65,
            MinQualityRetentionRatio = 0.85
        };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.False(result.Passed);
        Assert.True(result.QualityRegressionFailed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("oobRegression", result.FailureReason);
    }

    [Fact]
    public void FailureReason_ContainsAllFailedGates()
    {
        // Trigger multiple failures simultaneously.
        var input = DefaultPassingInput() with
        {
            Accuracy = 0.45,       // below 0.55
            ExpectedValue = -0.05, // below 0.0
            SharpeRatio = 0.3,     // below 0.5
            WfStdAccuracy = 0.20   // above 0.15
        };

        var result = QualityGateEvaluator.Evaluate(input);

        Assert.False(result.Passed);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("accuracy=", result.FailureReason);
        Assert.Contains("ev=", result.FailureReason);
        Assert.Contains("sharpe=", result.FailureReason);
        Assert.Contains("wfStd=", result.FailureReason);
    }
}
