using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

public class GaussianProcessSurrogateTest
{
    // -- Test 1: Fit with known points, predict at known x, verify mean close to y

    [Fact]
    public void Fit_WithKnownPoints_PredictsMean()
    {
        // Arrange — fit a GP on 3 known 1D points: (0.0, 0.0), (0.5, 1.0), (1.0, 0.0)
        var gp = new GaussianProcessSurrogate(lengthScale: 0.5, noise: 1e-4, kappa: 2.0);

        var X = new double[][]
        {
            new[] { 0.0 },
            new[] { 0.5 },
            new[] { 1.0 },
        };
        var y = new double[] { 0.0, 1.0, 0.0 };

        gp.Fit(X, y);

        // Act — predict at x = 0.5, where the training data says y = 1.0
        var (mean, variance) = gp.Predict(new[] { 0.5 });

        // Assert — the posterior mean should be close to the observed value (1.0)
        // with very low variance (since we are predicting at an observed point)
        Assert.True(Math.Abs(mean - 1.0) < 0.05,
            $"Expected GP mean near 1.0 at the observed point, got {mean}.");
        Assert.True(variance < 0.01,
            $"Expected very low variance at an observed point, got {variance}.");
    }

    // -- Test 2: RankByUcb returns descending order (first index has highest UCB)

    [Fact]
    public void RankByUcb_ReturnsDescendingOrder()
    {
        // Arrange — fit on a few points, then rank candidate points by UCB
        var gp = new GaussianProcessSurrogate(lengthScale: 0.5, noise: 1e-4, kappa: 2.0);

        var X = new double[][]
        {
            new[] { 0.0 },
            new[] { 0.5 },
            new[] { 1.0 },
        };
        var y = new double[] { 0.1, 0.8, 0.2 };

        gp.Fit(X, y);

        // Candidates include an unobserved point (0.25) and the observed peak (0.5)
        var candidates = new double[][]
        {
            new[] { 0.0 },   // low observed y
            new[] { 0.25 },  // unobserved — moderate mean, higher uncertainty
            new[] { 0.5 },   // observed peak — high mean, low uncertainty
            new[] { 0.75 },  // unobserved — moderate mean, higher uncertainty
            new[] { 1.0 },   // low observed y
        };

        // Act
        var ranked = gp.RankByUcb(candidates).ToArray();

        // Assert — the result should be indices in descending UCB order
        Assert.Equal(candidates.Length, ranked.Length);

        // The first ranked index should have the highest UCB
        double prevUcb = double.MaxValue;
        foreach (var idx in ranked)
        {
            double ucb = gp.Ucb(candidates[idx]);
            Assert.True(ucb <= prevUcb + 1e-10,
                $"UCB ranking is not descending: index {idx} has UCB {ucb} which exceeds previous {prevUcb}.");
            prevUcb = ucb;
        }
    }

    // -- Test 3: Predict returns positive variance

    [Fact]
    public void Predict_ReturnsPositiveVariance()
    {
        // Arrange — fit on a small dataset
        var gp = new GaussianProcessSurrogate(lengthScale: 0.5, noise: 1e-4, kappa: 2.0);

        var X = new double[][]
        {
            new[] { 0.0 },
            new[] { 1.0 },
        };
        var y = new double[] { 0.0, 1.0 };

        gp.Fit(X, y);

        // Act — predict at an unobserved point far from training data
        var (mean, variance) = gp.Predict(new[] { 0.5 });

        // Assert — variance should always be strictly positive
        Assert.True(variance > 0.0,
            $"Expected positive variance, got {variance}.");

        // Also check a point far from observed data has higher variance
        var (_, varianceFar) = gp.Predict(new[] { 5.0 });
        Assert.True(varianceFar > 0.0,
            $"Expected positive variance at a distant point, got {varianceFar}.");

        // The distant point should have higher variance than the interpolated point
        Assert.True(varianceFar > variance,
            $"Expected variance at x=5.0 ({varianceFar}) to exceed variance at x=0.5 ({variance}).");
    }
}
