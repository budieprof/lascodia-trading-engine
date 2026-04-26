using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

/// <summary>
/// Unit tests for <see cref="MLCalibrationSignalEvaluator"/> that exercise the math
/// directly without spinning up the worker. These prove the extracted collaborator
/// can be tested in isolation — the testability win behind extracting it from the
/// partial-class helpers.
/// </summary>
public sealed class MLCalibrationSignalEvaluatorTest
{
    private static readonly DateTime Now = new(2026, 04, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ComputeSummary_PerfectlyCalibratedSamples_ProducesNearZeroEce()
    {
        // Confidence 0.5 with ~50% correctness should produce near-zero ECE.
        var evaluator = new MLCalibrationSignalEvaluator();
        var samples = new List<CalibrationSample>();
        for (int i = 0; i < 100; i++)
        {
            samples.Add(new CalibrationSample(
                Confidence: 0.5,
                Correct: i % 2 == 0,
                OutcomeAt: Now.AddMinutes(-i),
                PredictedAt: Now.AddMinutes(-i - 5)));
        }

        var summary = evaluator.ComputeSummary(
            samples, bootstrapResamples: 0, nowUtc: Now,
            timeDecayHalfLifeDays: 0, minSamplesForTimeDecay: 0,
            cachedStderr: null, modelId: 1);

        Assert.Equal(100, summary.ResolvedCount);
        Assert.True(summary.CurrentEce < 0.05, $"Expected ECE near 0 for perfectly-calibrated samples, got {summary.CurrentEce}");
        Assert.Equal(0.5, summary.MeanConfidence, 6);
        Assert.Equal(0.5, summary.Accuracy, 6);
    }

    [Fact]
    public void ComputeSummary_OverConfidentSamples_ProducesLargeEce()
    {
        // Confidence 0.95 with 30% correctness — model is severely over-confident.
        // ECE should be substantial (≥ 0.5).
        var evaluator = new MLCalibrationSignalEvaluator();
        var samples = new List<CalibrationSample>();
        for (int i = 0; i < 100; i++)
        {
            samples.Add(new CalibrationSample(
                Confidence: 0.95,
                Correct: i < 30,
                OutcomeAt: Now.AddMinutes(-i),
                PredictedAt: Now.AddMinutes(-i - 5)));
        }

        var summary = evaluator.ComputeSummary(
            samples, bootstrapResamples: 0, nowUtc: Now,
            timeDecayHalfLifeDays: 0, minSamplesForTimeDecay: 0,
            cachedStderr: null, modelId: 1);

        Assert.True(summary.CurrentEce > 0.5, $"Expected large ECE for over-confident samples, got {summary.CurrentEce}");
    }

    [Fact]
    public void BuildSignals_TrendInsideStderrBand_DoesNotTrigger()
    {
        // Trend delta exceeds the absolute degradation delta but doesn't clear the
        // K-sigma stderr bar → trendExceeded must be false. This is the regression
        // guard that prevents single-cycle noise from tripping the trend alert.
        var evaluator = new MLCalibrationSignalEvaluator();

        var signals = evaluator.BuildSignals(
            currentEce: 0.10,
            eceStderr: 0.05,                  // K-sigma bar = 1.0 * 0.05 = 0.05
            previousEce: 0.06,                // trend delta = 0.04
            baselineEce: null,
            maxEce: 0.20,                     // not exceeded
            degradationDelta: 0.03,           // exceeded by trend (0.04 > 0.03)
            regressionGuardK: 1.0);

        Assert.False(signals.ThresholdExceeded);
        Assert.False(signals.TrendExceeded);  // gated by stderr (0.04 < 0.05)
        Assert.False(signals.TrendStderrPasses);
    }

    [Fact]
    public void BuildSignals_TrendClearsBothGates_Triggers()
    {
        var evaluator = new MLCalibrationSignalEvaluator();

        var signals = evaluator.BuildSignals(
            currentEce: 0.20,
            eceStderr: 0.01,                  // K-sigma bar = 0.01
            previousEce: 0.05,                // trend delta = 0.15
            baselineEce: null,
            maxEce: 0.30,
            degradationDelta: 0.05,
            regressionGuardK: 1.0);

        Assert.True(signals.TrendExceeded);
        Assert.True(signals.TrendStderrPasses);
    }

    [Fact]
    public void ResolveAlertState_ThresholdSeverity_MarksCritical()
    {
        // Current ECE crosses the 2× severe-tier multiplier → Critical.
        var evaluator = new MLCalibrationSignalEvaluator();
        var signals = evaluator.BuildSignals(
            currentEce: 0.50,
            eceStderr: 0.0,
            previousEce: null,
            baselineEce: null,
            maxEce: 0.20,                     // exceeded; 0.50 > 2 * 0.20 → Critical
            degradationDelta: 0.05,
            regressionGuardK: 1.0);

        var state = evaluator.ResolveAlertState(0.50, signals, maxEce: 0.20, degradationDelta: 0.05);
        Assert.Equal(MLCalibrationMonitorAlertState.Critical, state);
    }

    [Fact]
    public void ResolveAlertState_ThresholdBetweenWarningAndSevere_MarksWarning()
    {
        var evaluator = new MLCalibrationSignalEvaluator();
        var signals = evaluator.BuildSignals(
            currentEce: 0.30,                 // exceeded but < 2×
            eceStderr: 0.0,
            previousEce: null,
            baselineEce: null,
            maxEce: 0.20,
            degradationDelta: 0.05,
            regressionGuardK: 1.0);

        var state = evaluator.ResolveAlertState(0.30, signals, maxEce: 0.20, degradationDelta: 0.05);
        Assert.Equal(MLCalibrationMonitorAlertState.Warning, state);
    }

    [Fact]
    public void DetermineSeverity_CriticalState_ReturnsCriticalSeverity()
    {
        var evaluator = new MLCalibrationSignalEvaluator();
        var summary = new CalibrationSummary(100, 0.10, 0.85, 0.75,
            Now.AddDays(-1), Now, [], [], [], 0.01);
        var signals = new CalibrationSignals(0.05, null, 0.05, 0.0, true, true, false, true);

        var severity = evaluator.DetermineSeverity(
            MLCalibrationMonitorAlertState.Critical, summary, signals,
            maxEce: 0.10, degradationDelta: 0.03);

        Assert.Equal(AlertSeverity.Critical, severity);
    }

    [Fact]
    public void ComputeSummary_BootstrapDeterministicAcrossInvocations()
    {
        // Same inputs (samples, modelId, resamples) must produce same stderr — this
        // is the property that lets two replicas of the same model agree on a cold-
        // cache stderr. FNV-1a-mixed seed.
        var evaluator = new MLCalibrationSignalEvaluator();
        var samples = new List<CalibrationSample>();
        for (int i = 0; i < 50; i++)
        {
            samples.Add(new CalibrationSample(
                Confidence: 0.7,
                Correct: i % 3 != 0,
                OutcomeAt: Now.AddMinutes(-i),
                PredictedAt: Now.AddMinutes(-i - 5)));
        }

        var summary1 = evaluator.ComputeSummary(samples, 200, Now, 0, 0, null, modelId: 7);
        var summary2 = evaluator.ComputeSummary(samples, 200, Now, 0, 0, null, modelId: 7);

        Assert.Equal(summary1.EceStderr, summary2.EceStderr, 12);

        // Different modelId → different RNG sequence → different stderr (with high probability).
        var summary3 = evaluator.ComputeSummary(samples, 200, Now, 0, 0, null, modelId: 99);
        Assert.NotEqual(summary1.EceStderr, summary3.EceStderr);
    }

    [Fact]
    public void ComputeSummary_CachedStderrSkipsBootstrap()
    {
        // When cachedStderr is non-null, that exact value flows through to summary —
        // no bootstrap re-run. Hot-cache path.
        var evaluator = new MLCalibrationSignalEvaluator();
        var samples = new List<CalibrationSample>
        {
            new(0.7, true, Now, Now),
            new(0.7, false, Now, Now),
        };

        var summary = evaluator.ComputeSummary(
            samples, bootstrapResamples: 9999, nowUtc: Now,
            timeDecayHalfLifeDays: 0, minSamplesForTimeDecay: 0,
            cachedStderr: 0.0123456789, modelId: 1);

        Assert.Equal(0.0123456789, summary.EceStderr, 12);
    }
}
