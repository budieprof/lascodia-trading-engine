using LascodiaTradingEngine.Application.Common.Drift;

namespace LascodiaTradingEngine.UnitTest.Application.Common.Drift;

public sealed class AdwinDetectorTest
{
    [Fact]
    public void Evaluate_ReturnsNull_BelowMinObservations()
    {
        var outcomes = Enumerable.Repeat(true, AdwinDetector.MinRequiredObservations - 1).ToArray();

        var result = AdwinDetector.Evaluate(outcomes, delta: 0.002);

        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_StableStream_DoesNotDetectDrift()
    {
        var rng = new Random(42);
        var outcomes = Enumerable.Range(0, 200).Select(_ => rng.NextDouble() < 0.55).ToArray();

        var result = AdwinDetector.Evaluate(outcomes, delta: 0.002);

        Assert.NotNull(result);
        Assert.False(result.Value.DriftDetected);
        Assert.Equal(0.0, result.Value.AccuracyDrop);
    }

    [Fact]
    public void Evaluate_DegradingStream_DetectsDriftAndReportsDrop()
    {
        // 50 correct then 50 wrong = sharp drop from 1.0 to 0.0.
        var outcomes = Enumerable.Repeat(true, 50)
            .Concat(Enumerable.Repeat(false, 50))
            .ToArray();

        var result = AdwinDetector.Evaluate(outcomes, delta: 0.002);

        Assert.NotNull(result);
        Assert.True(result.Value.DriftDetected);
        Assert.True(result.Value.AccuracyDrop > 0.5);
        Assert.True(result.Value.SelectedEvidence.Window1Mean > result.Value.SelectedEvidence.Window2Mean);
        Assert.True(result.Value.SelectedEvidence.EpsilonCut > 0);
    }

    [Fact]
    public void Evaluate_ImprovingStream_DoesNotFlagAsDegradation()
    {
        // 60 wrong then 40 right — improvement, not degradation.
        var outcomes = Enumerable.Repeat(false, 60)
            .Concat(Enumerable.Repeat(true, 40))
            .ToArray();

        var result = AdwinDetector.Evaluate(outcomes, delta: 0.002);

        Assert.NotNull(result);
        Assert.False(result.Value.DriftDetected);
        // Audit evidence should still capture the strongest absolute change.
        Assert.True(result.Value.SelectedEvidence.Window2Mean > result.Value.SelectedEvidence.Window1Mean);
    }

    [Fact]
    public void Evaluate_EpsilonCut_PinnedToCanonicalFormula()
    {
        // Pin the bound for a fixed input so any future tweak to the formula is
        // caught by CI. Bifet-Gavaldà (2007) §3.2 with Bonferroni δ' = δ / n:
        //   ε = sqrt( (1/(2·n0) + 1/(2·n1)) · ln(2 · n / δ) )
        // For n0 = n1 = 30, δ = 0.002, n = 60:
        //   ε = sqrt( (1/60 + 1/60) · ln(60_000) )
        //     = sqrt( (1/30) · ln(60_000) )
        //     ≈ 0.5961
        var outcomes = new bool[60];
        for (int i = 0; i < 60; i++) outcomes[i] = i < 45;  // 45-true / 15-false; deterministic

        var result = AdwinDetector.Evaluate(outcomes, delta: 0.002);

        Assert.NotNull(result);
        // The selected split may not be exactly 30 (we pick the strongest); recompute the
        // expected ε for the chosen split to make the assertion robust to internal split
        // selection but still pinned to the canonical formula.
        var selected = result.Value.SelectedEvidence;
        int n0 = selected.SplitIndex;
        int n1 = outcomes.Length - selected.SplitIndex;
        double expectedEpsilon = Math.Sqrt(
            (1.0 / (2.0 * n0) + 1.0 / (2.0 * n1)) *
            Math.Log(2.0 * outcomes.Length / 0.002));
        Assert.Equal(expectedEpsilon, selected.EpsilonCut, 10);
    }

    [Fact]
    public void Evaluate_Throws_OnInvalidDelta()
    {
        var outcomes = Enumerable.Repeat(true, AdwinDetector.MinRequiredObservations).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => AdwinDetector.Evaluate(outcomes, delta: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdwinDetector.Evaluate(outcomes, delta: 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdwinDetector.Evaluate(outcomes, delta: double.NaN));
    }

    [Fact]
    public void Evaluate_TighterDelta_RaisesEpsilonAndReducesFalsePositives()
    {
        // 60 correct / 40 wrong — clearly degrading at 0.05 confidence, but at a much
        // smaller δ (= harder evidence requirement) the epsilon cut should grow and
        // the score should shrink. Sanity check that δ is monotone in ε.
        var outcomes = Enumerable.Repeat(true, 60).Concat(Enumerable.Repeat(false, 40)).ToArray();

        var loose = AdwinDetector.Evaluate(outcomes, delta: 0.05).GetValueOrDefault();
        var tight = AdwinDetector.Evaluate(outcomes, delta: 0.0001).GetValueOrDefault();

        Assert.True(tight.SelectedEvidence.EpsilonCut > loose.SelectedEvidence.EpsilonCut);
    }
}
