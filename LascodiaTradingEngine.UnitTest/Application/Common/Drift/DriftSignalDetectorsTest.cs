using LascodiaTradingEngine.Application.Common.Drift;

namespace LascodiaTradingEngine.UnitTest.Application.Common.Drift;

public sealed class DriftSignalDetectorsTest
{
    // ── EvaluateAccuracy ─────────────────────────────────────────────────────

    [Fact]
    public void EvaluateAccuracy_BelowThreshold_Triggers()
    {
        var sig = DriftSignalDetectors.EvaluateAccuracy(correct: 30, total: 100, threshold: 0.50);
        Assert.True(sig.Triggered);
        Assert.Equal(0.30, sig.Accuracy, 6);
        Assert.Equal(0.50, sig.Threshold, 6);
    }

    [Fact]
    public void EvaluateAccuracy_AtThreshold_DoesNotTrigger()
    {
        // accuracy strictly less-than threshold; equal is healthy
        var sig = DriftSignalDetectors.EvaluateAccuracy(correct: 50, total: 100, threshold: 0.50);
        Assert.False(sig.Triggered);
    }

    [Fact]
    public void EvaluateAccuracy_AboveThreshold_DoesNotTrigger()
    {
        var sig = DriftSignalDetectors.EvaluateAccuracy(correct: 75, total: 100, threshold: 0.50);
        Assert.False(sig.Triggered);
    }

    [Fact]
    public void EvaluateAccuracy_ZeroTotal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DriftSignalDetectors.EvaluateAccuracy(correct: 0, total: 0, threshold: 0.50));
    }

    // ── EvaluateBrier ────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateBrier_AboveThreshold_Triggers()
    {
        var sig = DriftSignalDetectors.EvaluateBrier(brierScore: 0.40, threshold: 0.30);
        Assert.True(sig.Triggered);
    }

    [Fact]
    public void EvaluateBrier_AtOrBelowThreshold_DoesNotTrigger()
    {
        Assert.False(DriftSignalDetectors.EvaluateBrier(0.30, 0.30).Triggered);
        Assert.False(DriftSignalDetectors.EvaluateBrier(0.10, 0.30).Triggered);
    }

    // ── EvaluateDisagreement ─────────────────────────────────────────────────

    [Fact]
    public void EvaluateDisagreement_AboveThresholdAndSufficientSample_Triggers()
    {
        var sig = DriftSignalDetectors.EvaluateDisagreement(
            meanDisagreement: 0.40, sampleCount: 50, minPredictions: 30, threshold: 0.35);
        Assert.True(sig.Triggered);
    }

    [Fact]
    public void EvaluateDisagreement_InsufficientSample_DoesNotTrigger()
    {
        // Even with high disagreement, sub-min-sample sample size should suppress.
        var sig = DriftSignalDetectors.EvaluateDisagreement(
            meanDisagreement: 0.99, sampleCount: 5, minPredictions: 30, threshold: 0.35);
        Assert.False(sig.Triggered);
    }

    [Fact]
    public void EvaluateDisagreement_BelowThreshold_DoesNotTrigger()
    {
        var sig = DriftSignalDetectors.EvaluateDisagreement(
            meanDisagreement: 0.20, sampleCount: 50, minPredictions: 30, threshold: 0.35);
        Assert.False(sig.Triggered);
    }

    // ── EvaluateRelativeDegradation ──────────────────────────────────────────

    [Fact]
    public void EvaluateRelativeDegradation_NoTrainingAccuracy_DoesNotTrigger()
    {
        var sig = DriftSignalDetectors.EvaluateRelativeDegradation(
            accuracy: 0.10, trainingAccuracy: null, degradationRatio: 0.85);
        Assert.False(sig.Triggered);
    }

    [Fact]
    public void EvaluateRelativeDegradation_LiveBelowEffectiveThreshold_Triggers()
    {
        // Training 0.70 × 0.85 = 0.595; live 0.50 < 0.595 → triggered
        var sig = DriftSignalDetectors.EvaluateRelativeDegradation(
            accuracy: 0.50, trainingAccuracy: 0.70, degradationRatio: 0.85);
        Assert.True(sig.Triggered);
        Assert.Equal(0.595, sig.EffectiveThreshold, 4);
    }

    [Fact]
    public void EvaluateRelativeDegradation_LiveAboveEffectiveThreshold_DoesNotTrigger()
    {
        var sig = DriftSignalDetectors.EvaluateRelativeDegradation(
            accuracy: 0.65, trainingAccuracy: 0.70, degradationRatio: 0.85);
        Assert.False(sig.Triggered);
    }

    // ── EvaluateSharpe ───────────────────────────────────────────────────────

    [Fact]
    public void EvaluateSharpe_NoTrainingSharpe_DoesNotTrigger()
    {
        var pnl = Enumerable.Repeat(1.0, 100).ToList();
        var sig = DriftSignalDetectors.EvaluateSharpe(
            pnl, trainingSharpe: null, degradationRatio: 0.6, minClosedTrades: 20);
        Assert.False(sig.Triggered);
    }

    [Fact]
    public void EvaluateSharpe_BelowMinTrades_DoesNotTrigger()
    {
        var pnl = Enumerable.Repeat(-1.0, 5).ToList(); // bad performance, small sample
        var sig = DriftSignalDetectors.EvaluateSharpe(
            pnl, trainingSharpe: 1.5, degradationRatio: 0.6, minClosedTrades: 20);
        Assert.False(sig.Triggered);
    }

    [Fact]
    public void EvaluateSharpe_LiveSharpeBelowEffectiveThreshold_Triggers()
    {
        // Live trades all losing → live sharpe is very negative; training 1.5 × 0.6 = 0.9
        var pnl = new List<double> { -10, -8, -12, -9, -11, -10, -9, -10, -11, -10, -9, -10, -8, -12, -11, -10, -9, -10, -11, -10, -9, -10, -11, -10, -9 };
        var sig = DriftSignalDetectors.EvaluateSharpe(
            pnl, trainingSharpe: 1.5, degradationRatio: 0.6, minClosedTrades: 20);
        Assert.True(sig.Triggered);
        Assert.True(sig.LiveSharpe < sig.EffectiveThreshold);
    }

    [Fact]
    public void EvaluateSharpe_StableProfitableLive_DoesNotTrigger()
    {
        // Consistently positive live trades → high live sharpe; should beat 1.5 × 0.6 = 0.9
        var pnl = new List<double> { 10, 9, 11, 10, 9, 11, 10, 12, 9, 10, 11, 10, 9, 10, 11, 12, 9, 10, 11, 10, 12, 9, 11, 10, 9 };
        var sig = DriftSignalDetectors.EvaluateSharpe(
            pnl, trainingSharpe: 1.5, degradationRatio: 0.6, minClosedTrades: 20);
        Assert.False(sig.Triggered);
        Assert.True(sig.LiveSharpe > sig.EffectiveThreshold);
    }

    [Fact]
    public void EvaluateSharpe_ZeroVarianceReturns_HandledGracefully()
    {
        // All identical positive returns → variance = 0, std = 0; live sharpe defaults to 0
        var pnl = Enumerable.Repeat(5.0, 30).ToList();
        var sig = DriftSignalDetectors.EvaluateSharpe(
            pnl, trainingSharpe: 1.5, degradationRatio: 0.6, minClosedTrades: 20);
        Assert.Equal(0.0, sig.LiveSharpe, 6);
        Assert.True(sig.Triggered); // 0 < 0.9, so this triggers
    }
}
