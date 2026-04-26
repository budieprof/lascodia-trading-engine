using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

/// <summary>
/// Unit tests for <see cref="MLCalibratedEdgeEvaluator"/> exercising the math
/// directly without spinning up the worker. Independently testable collaborator —
/// proves the extracted-class testability win.
/// </summary>
public sealed class MLCalibratedEdgeEvaluatorTest
{
    private static readonly DateTime Now = new(2026, 04, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ComputeSummary_AllCorrectPredictions_ProducesPositiveEdge()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        var samples = new List<CalibratedEdgeSample>
        {
            new(0.90, 0.50, ActualBuy: true, AbsMagnitudePips: 10, Now, Now),
            new(0.85, 0.50, ActualBuy: true, AbsMagnitudePips: 12, Now, Now),
            new(0.10, 0.50, ActualBuy: false, AbsMagnitudePips: 8, Now, Now),
        };

        var summary = evaluator.ComputeSummary(samples);

        Assert.Equal(3, summary.ResolvedCount);
        Assert.True(summary.ExpectedValuePips > 0, $"Expected positive EV, got {summary.ExpectedValuePips}");
        Assert.Equal(1.0, summary.WinRate, 6);
    }

    [Fact]
    public void ComputeSummary_AllWrongPredictions_ProducesNegativeEdge()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        var samples = new List<CalibratedEdgeSample>
        {
            new(0.90, 0.50, ActualBuy: false, AbsMagnitudePips: 10, Now, Now),
            new(0.85, 0.50, ActualBuy: false, AbsMagnitudePips: 12, Now, Now),
            new(0.10, 0.50, ActualBuy: true, AbsMagnitudePips: 8, Now, Now),
        };

        var summary = evaluator.ComputeSummary(samples);

        Assert.True(summary.ExpectedValuePips < 0, $"Expected negative EV, got {summary.ExpectedValuePips}");
        Assert.Equal(0.0, summary.WinRate, 6);
    }

    [Fact]
    public void ComputeSummary_EmptyInput_ReturnsZeroes()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        var summary = evaluator.ComputeSummary(new List<CalibratedEdgeSample>());

        Assert.Equal(0, summary.ResolvedCount);
        Assert.Equal(0, summary.ExpectedValuePips, 12);
        Assert.Equal(0, summary.WinRate, 12);
    }

    [Fact]
    public void ResolveAlertState_NegativeEvWithStderrAboveZero_KSigmaGateSuppresses()
    {
        // EV = -0.5 but stderr = 1.0 → upper bound = -0.5 + 1.0 * 1.0 = 0.5 > 0 → not Critical.
        // The K-sigma gate prevents a single noisy small-sample window from tripping
        // Critical on a model that's actually break-even.
        var evaluator = new MLCalibratedEdgeEvaluator();
        var state = evaluator.ResolveAlertState(
            expectedValuePips: -0.5,
            evStderr: 1.0,
            warnExpectedValuePips: 0.5,
            regressionGuardK: 1.0);
        Assert.NotEqual(MLCalibratedEdgeAlertState.Critical, state);
    }

    [Fact]
    public void ResolveAlertState_NegativeEvWithSmallStderr_KSigmaGatePasses()
    {
        // EV = -0.5 with stderr = 0.1 → upper bound = -0.5 + 0.1 = -0.4 ≤ 0 → Critical.
        // Strong signal: EV is negative with confidence.
        var evaluator = new MLCalibratedEdgeEvaluator();
        var state = evaluator.ResolveAlertState(
            expectedValuePips: -0.5,
            evStderr: 0.1,
            warnExpectedValuePips: 0.5,
            regressionGuardK: 1.0);
        Assert.Equal(MLCalibratedEdgeAlertState.Critical, state);
    }

    [Fact]
    public void ResolveAlertState_KZero_RecoversOriginalEvOnlySemantics()
    {
        // K = 0 → gate collapses to the pre-K-sigma behaviour: EV ≤ 0 → Critical
        // regardless of stderr. Used by tests that want to exercise the EV-only path.
        var evaluator = new MLCalibratedEdgeEvaluator();
        var state = evaluator.ResolveAlertState(
            expectedValuePips: -0.5,
            evStderr: 100.0,            // huge stderr would otherwise suppress
            warnExpectedValuePips: 0.5,
            regressionGuardK: 0.0);     // gate disabled
        Assert.Equal(MLCalibratedEdgeAlertState.Critical, state);
    }

    [Fact]
    public void ComputeSummary_BootstrapResamples_ProducesNonZeroStderr()
    {
        // 50 samples with mixed outcomes → bootstrap stderr should be non-zero. Same
        // input + same modelId reproduces the same stderr (FNV-1a deterministic seed).
        var evaluator = new MLCalibratedEdgeEvaluator();
        var samples = new List<CalibratedEdgeSample>();
        for (int i = 0; i < 50; i++)
        {
            samples.Add(new CalibratedEdgeSample(
                ServedBuyProbability: 0.7,
                DecisionThreshold: 0.5,
                ActualBuy: i % 3 != 0,
                AbsMagnitudePips: 10,
                OutcomeAt: Now.AddMinutes(-i),
                PredictedAt: Now.AddMinutes(-i - 5)));
        }

        var summary1 = evaluator.ComputeSummary(samples, bootstrapResamples: 200, modelId: 7);
        var summary2 = evaluator.ComputeSummary(samples, bootstrapResamples: 200, modelId: 7);

        Assert.True(summary1.EvStderr > 0, $"Expected non-zero stderr; got {summary1.EvStderr}");
        Assert.Equal(summary1.EvStderr, summary2.EvStderr, 12);

        // Different modelId → different RNG sequence → different stderr (with high prob).
        var summary3 = evaluator.ComputeSummary(samples, bootstrapResamples: 200, modelId: 99);
        Assert.NotEqual(summary1.EvStderr, summary3.EvStderr);
    }

    [Fact]
    public void ResolveAlertState_NegativeEv_IsCritical()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        Assert.Equal(MLCalibratedEdgeAlertState.Critical,
            evaluator.ResolveAlertState(expectedValuePips: -0.5, warnExpectedValuePips: 0.5));
    }

    [Fact]
    public void ResolveAlertState_BelowWarnFloor_IsWarning()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        Assert.Equal(MLCalibratedEdgeAlertState.Warning,
            evaluator.ResolveAlertState(expectedValuePips: 0.3, warnExpectedValuePips: 0.5));
    }

    [Fact]
    public void ResolveAlertState_AboveWarnFloor_IsNone()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        Assert.Equal(MLCalibratedEdgeAlertState.None,
            evaluator.ResolveAlertState(expectedValuePips: 1.5, warnExpectedValuePips: 0.5));
    }

    [Fact]
    public void DetermineSeverity_CriticalState_ReturnsCriticalSeverity()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        var summary = new LiveEdgeSummary(10, -0.5, 0.3, 0.4, 10, Now, Now);

        var severity = evaluator.DetermineSeverity(
            MLCalibratedEdgeAlertState.Critical, summary, warnExpectedValuePips: 0.5);

        Assert.Equal(AlertSeverity.Critical, severity);
    }

    [Fact]
    public void DetermineSeverity_WarningWellBelowFloor_ReturnsHigh()
    {
        // EV ≤ warn × 0.5 → High severity.
        var evaluator = new MLCalibratedEdgeEvaluator();
        var summary = new LiveEdgeSummary(10, 0.20, 0.5, 0.4, 10, Now, Now);

        var severity = evaluator.DetermineSeverity(
            MLCalibratedEdgeAlertState.Warning, summary, warnExpectedValuePips: 0.50);

        Assert.Equal(AlertSeverity.High, severity);
    }

    [Fact]
    public void DetermineSeverity_WarningJustBelowFloor_ReturnsMedium()
    {
        var evaluator = new MLCalibratedEdgeEvaluator();
        var summary = new LiveEdgeSummary(10, 0.40, 0.5, 0.4, 10, Now, Now);

        var severity = evaluator.DetermineSeverity(
            MLCalibratedEdgeAlertState.Warning, summary, warnExpectedValuePips: 0.50);

        Assert.Equal(AlertSeverity.Medium, severity);
    }
}
