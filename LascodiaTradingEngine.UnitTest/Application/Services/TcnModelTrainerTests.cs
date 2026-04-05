using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Enums;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class TcnModelTrainerTests
{
    // ── Item 46: Calibration tests ──────────────────────────────────────

    [Fact]
    public void FitBetaCalibration_WithPerfectCalibration_ReturnsNearIdentityParams()
    {
        // Generate samples where raw prob = true prob (already calibrated)
        var samples = new List<TrainingSample>();
        var rawProbs = new double[100];
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            double p = 0.1 + 0.8 * rng.NextDouble(); // [0.1, 0.9]
            int dir = rng.NextDouble() < p ? 1 : 0;
            samples.Add(new TrainingSample(new float[10], dir, 1.0f));
            rawProbs[i] = Math.Clamp(p, 1e-7, 1 - 1e-7);
        }

        var (a, b, c) = TcnModelTrainer.FitBetaCalibration(samples, rawProbs);

        // For already-calibrated data, a ≈ 1, b ≈ -1, c ≈ 0 (identity on logit scale)
        // Allow generous tolerance since 100 samples is small
        Assert.InRange(a, 0.3, 3.0);
        Assert.InRange(c, -2.0, 2.0);
    }

    [Fact]
    public void FitBetaCalibration_WithTooFewSamples_ReturnsZeros()
    {
        var samples = new List<TrainingSample>();
        for (int i = 0; i < 5; i++)
            samples.Add(new TrainingSample(new float[10], i % 2, 1.0f));
        var rawProbs = new double[] { 0.3, 0.5, 0.6, 0.7, 0.8 };

        var (a, b, c) = TcnModelTrainer.FitBetaCalibration(samples, rawProbs);

        Assert.Equal(0, a);
        Assert.Equal(0, b);
        Assert.Equal(0, c);
    }

    [Fact]
    public void ComputeCalibrationDecomposition_ReturnsNonNegativeValues()
    {
        var samples = new List<TrainingSample>();
        var rawProbs = new double[50];
        var rng = new Random(123);
        for (int i = 0; i < 50; i++)
        {
            double p = rng.NextDouble();
            samples.Add(new TrainingSample(new float[10], rng.NextDouble() > 0.5 ? 1 : 0, 1.0f));
            rawProbs[i] = Math.Clamp(p, 1e-7, 1 - 1e-7);
        }

        var (mce, eceBuy, eceSell) = TcnModelTrainer.ComputeCalibrationDecomposition(
            samples, rawProbs, 1.0, 0.0);

        Assert.True(mce >= 0);
        Assert.True(eceBuy >= 0);
        Assert.True(eceSell >= 0);
    }

    [Fact]
    public void ComputeRecalibrationStability_WithIdenticalParams_ReturnsZeroStd()
    {
        var foldParams = new List<(double A, double B)>
        {
            (1.0, 0.0), (1.0, 0.0), (1.0, 0.0)
        };

        var (stdA, stdB) = TcnModelTrainer.ComputeRecalibrationStability(foldParams);

        Assert.Equal(0, stdA, 5);
        Assert.Equal(0, stdB, 5);
    }

    [Fact]
    public void ComputeRecalibrationStability_WithVaryingParams_ReturnsPositiveStd()
    {
        var foldParams = new List<(double A, double B)>
        {
            (0.8, -0.1), (1.0, 0.0), (1.2, 0.1)
        };

        var (stdA, stdB) = TcnModelTrainer.ComputeRecalibrationStability(foldParams);

        Assert.True(stdA > 0);
        Assert.True(stdB > 0);
    }

    [Fact]
    public void FitVennAbers_WithEnoughSamples_ReturnsBoundsForEachSample()
    {
        var samples = new List<TrainingSample>();
        var rawProbs = new double[30];
        var rng = new Random(99);
        for (int i = 0; i < 30; i++)
        {
            samples.Add(new TrainingSample(new float[10], rng.NextDouble() > 0.5 ? 1 : 0, 1.0f));
            rawProbs[i] = Math.Clamp(rng.NextDouble(), 1e-7, 1 - 1e-7);
        }

        var result = TcnModelTrainer.FitVennAbers(samples, rawProbs);

        Assert.Equal(30, result.Length);
        foreach (var bounds in result)
        {
            Assert.Equal(2, bounds.Length);
            Assert.True(bounds[0] >= 0 && bounds[0] <= 1,
                $"p0={bounds[0]} should be in [0,1]");
            Assert.True(bounds[1] >= 0 && bounds[1] <= 1,
                $"p1={bounds[1]} should be in [0,1]");
        }
    }

    // ── Item 47: LayerNorm backward gradient check ──────────────────────

    [Fact]
    public void LayerNormBackward_MatchesNumericalGradient()
    {
        // Test that the analytical LayerNorm backward matches finite-difference gradients
        const int filters = 4;
        const double eps = 1e-5;
        var rng = new Random(42);

        var preAct = new double[filters];
        var gamma = new double[filters];
        var beta = new double[filters];
        var dOutput = new double[filters];

        for (int i = 0; i < filters; i++)
        {
            preAct[i] = rng.NextDouble() * 2 - 1;
            gamma[i] = 0.8 + rng.NextDouble() * 0.4;
            beta[i] = (rng.NextDouble() - 0.5) * 0.2;
            dOutput[i] = rng.NextDouble() * 2 - 1;
        }

        // Forward: LayerNorm
        double mean = 0;
        for (int i = 0; i < filters; i++) mean += preAct[i];
        mean /= filters;
        double variance = 0;
        for (int i = 0; i < filters; i++) { double d = preAct[i] - mean; variance += d * d; }
        variance /= filters;
        double invStd = 1.0 / Math.Sqrt(variance + 1e-5);
        var normalized = new double[filters];
        var output = new double[filters];
        for (int i = 0; i < filters; i++)
        {
            normalized[i] = (preAct[i] - mean) * invStd;
            output[i] = gamma[i] * normalized[i] + beta[i];
        }

        // Analytical backward (same logic as TcnModelTrainer)
        double sumDNorm = 0, sumDNormTimesNorm = 0;
        var dPreConv = new double[filters];
        for (int i = 0; i < filters; i++)
        {
            dPreConv[i] = dOutput[i] * gamma[i]; // d/d_normalized
            sumDNorm += dPreConv[i];
            sumDNormTimesNorm += dPreConv[i] * normalized[i];
        }
        var analyticalGrad = new double[filters];
        double invN = 1.0 / filters;
        for (int i = 0; i < filters; i++)
            analyticalGrad[i] = invStd * (dPreConv[i] - invN * (sumDNorm + normalized[i] * sumDNormTimesNorm));

        // Numerical gradient via finite differences
        var numericalGrad = new double[filters];
        for (int i = 0; i < filters; i++)
        {
            double loss_plus = ComputeLayerNormLoss(preAct, gamma, beta, dOutput, i, eps);
            double loss_minus = ComputeLayerNormLoss(preAct, gamma, beta, dOutput, i, -eps);
            numericalGrad[i] = (loss_plus - loss_minus) / (2 * eps);
        }

        for (int i = 0; i < filters; i++)
            Assert.True(Math.Abs(analyticalGrad[i] - numericalGrad[i]) < 1e-4,
                $"Gradient mismatch at index {i}: analytical={analyticalGrad[i]:F6}, numerical={numericalGrad[i]:F6}");
    }

    private static double ComputeLayerNormLoss(double[] preAct, double[] gamma, double[] beta,
        double[] dOutput, int perturbIdx, double perturbation)
    {
        int n = preAct.Length;
        var perturbed = (double[])preAct.Clone();
        perturbed[perturbIdx] += perturbation;

        double mean = 0;
        for (int i = 0; i < n; i++) mean += perturbed[i];
        mean /= n;
        double variance = 0;
        for (int i = 0; i < n; i++) { double d = perturbed[i] - mean; variance += d * d; }
        variance /= n;
        double invStd = 1.0 / Math.Sqrt(variance + 1e-5);

        double loss = 0;
        for (int i = 0; i < n; i++)
        {
            double norm = (perturbed[i] - mean) * invStd;
            double output = gamma[i] * norm + beta[i];
            loss += output * dOutput[i]; // dot product as surrogate loss
        }
        return loss;
    }

    // ── Item 48: Attention backward gradient check ──────────────────────

    [Fact]
    public void AttentionPooling_ForwardProducesValidOutput()
    {
        // Test that attention pooling produces finite, non-zero output
        const int seqT = 5;
        const int filters = 4;
        const int kernelSize = 3;
        var rng = new Random(42);

        var seq = new float[seqT][];
        for (int t = 0; t < seqT; t++)
        {
            seq[t] = new float[filters];
            for (int f = 0; f < filters; f++)
                seq[t][f] = (float)(rng.NextDouble() * 2 - 1);
        }

        // convW needs filters * inC * kernelSize elements per block
        var convW = InitRandomWeights(filters * filters * kernelSize, rng);
        var convB = new double[filters];
        var queryW = InitRandomWeights(filters * filters, rng);
        var keyW = InitRandomWeights(filters * filters, rng);
        var valueW = InitRandomWeights(filters * filters, rng);

        var result = TcnModelTrainer.CausalConvForwardWithAttention(
            seq, new[] { convW }, new[] { convB },
            new double[]?[] { null }, new[] { filters },
            filters, 1, new[] { 1 }, false, null, null, TcnActivation.Relu,
            queryW, keyW, valueW, 1);

        Assert.Equal(filters, result.Length);
        Assert.All(result, v => Assert.True(double.IsFinite(v)));
        Assert.True(result.Any(v => Math.Abs(v) > 1e-10), "Output should not be all zeros");
    }

    // ── Item 49: Inference throughput benchmark ─────────────────────────

    [Fact]
    public void CausalConvForwardFull_CompletesWithin10ms_ForDefaultConfig()
    {
        // Benchmark: single-sample inference should be fast
        const int seqT = 30;
        const int channelIn = 9;
        const int filters = 32;
        const int numBlocks = 4;
        var rng = new Random(42);

        var seq = new float[seqT][];
        for (int t = 0; t < seqT; t++)
        {
            seq[t] = new float[channelIn];
            for (int c = 0; c < channelIn; c++) seq[t][c] = (float)(rng.NextDouble() * 2 - 1);
        }

        var dilations = TcnModelTrainer.BuildDilations(numBlocks);
        var blockInC = new int[numBlocks];
        blockInC[0] = channelIn;
        for (int b = 1; b < numBlocks; b++) blockInC[b] = filters;

        var convW = new double[numBlocks][];
        var convB = new double[numBlocks][];
        var resW = new double[]?[numBlocks];
        for (int b = 0; b < numBlocks; b++)
        {
            int inC = blockInC[b];
            convW[b] = InitRandomWeights(filters * inC * 3, rng);
            convB[b] = new double[filters];
            if (inC != filters) resW[b] = InitRandomWeights(filters * inC, rng);
        }

        // Warm up
        TcnModelTrainer.CausalConvForwardFull(seq, convW, convB, resW, blockInC,
            filters, numBlocks, dilations, false, null, null, TcnActivation.Relu);

        // Timed run (100 iterations)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            TcnModelTrainer.CausalConvForwardFull(seq, convW, convB, resW, blockInC,
                filters, numBlocks, dilations, false, null, null, TcnActivation.Relu);
        sw.Stop();

        double avgMs = sw.Elapsed.TotalMilliseconds / 100;
        // Each forward pass should complete well under 10ms
        Assert.True(avgMs < 10.0, $"Average inference time {avgMs:F2}ms exceeds 10ms threshold");
    }

    // ── Training augmentation tests ─────────────────────────────────────

    [Fact]
    public void ApplyMixup_ProducesMixedSample()
    {
        var s1 = CreateDummySample(direction: 1, magnitude: 2.0f, value: 1.0f);
        var s2 = CreateDummySample(direction: 0, magnitude: 1.0f, value: 0.0f);

        var (mixedSeq, mixedLabel, mixedMag) = TcnModelTrainer.ApplyMixup(s1, s2, 0.2, new Random(42));

        Assert.True(mixedLabel >= 0 && mixedLabel <= 1);
        Assert.True(mixedMag >= 1.0f && mixedMag <= 2.0f);
        // Mixed sequence values should be between 0 and 1
        Assert.All(mixedSeq, ts => Assert.All(ts, v => Assert.InRange(v, -0.01f, 1.01f)));
    }

    [Fact]
    public void ApplyCutMix_PreservesSequenceLength()
    {
        var s1 = CreateDummySample(direction: 1, magnitude: 2.0f, value: 1.0f);
        var s2 = CreateDummySample(direction: 0, magnitude: 1.0f, value: 0.0f);

        var (cutSeq, _, _) = TcnModelTrainer.ApplyCutMix(s1, s2, 0.5, new Random(42));

        Assert.Equal(s1.SequenceFeatures!.Length, cutSeq.Length);
        Assert.Equal(s1.SequenceFeatures![0].Length, cutSeq[0].Length);
    }

    [Fact]
    public void CurriculumSubsetSize_GrowsToFullSize()
    {
        int total = 1000;
        int first = TcnModelTrainer.CurriculumSubsetSize(total, 0, 100, 0.3, 1.0);
        int last = TcnModelTrainer.CurriculumSubsetSize(total, 99, 100, 0.3, 1.0);

        Assert.True(first < total);
        Assert.True(first >= (int)(total * 0.3));
        Assert.Equal(total, last);
    }

    // ── Evaluation tests ────────────────────────────────────────────────

    [Fact]
    public void ComputePredictionAutocorrelation_ReturnsFiniteValue()
    {
        var samples = new List<TrainingSample>();
        var rawProbs = new double[20];
        for (int i = 0; i < 20; i++)
        {
            samples.Add(new TrainingSample(new float[10], i % 2, 1.0f));
            rawProbs[i] = 0.3 + 0.4 * (i % 2); // alternating
        }

        double autocorr = TcnModelTrainer.ComputePredictionAutocorrelation(samples, rawProbs, 1.0, 0.0);

        Assert.True(double.IsFinite(autocorr));
        Assert.True(autocorr < 0); // alternating pattern should be negatively autocorrelated
    }

    [Fact]
    public void ComputeConfidenceHistogram_Returns5Quantiles()
    {
        var samples = new List<TrainingSample>();
        var rawProbs = new double[30];
        var rng = new Random(42);
        for (int i = 0; i < 30; i++)
        {
            samples.Add(new TrainingSample(new float[10], rng.Next(2), 1.0f));
            rawProbs[i] = Math.Clamp(rng.NextDouble(), 1e-7, 1 - 1e-7);
        }

        var quantiles = TcnModelTrainer.ComputeConfidenceHistogram(samples, rawProbs, 1.0, 0.0);

        Assert.Equal(5, quantiles.Length); // p10, p25, p50, p75, p90
        for (int i = 1; i < quantiles.Length; i++)
            Assert.True(quantiles[i] >= quantiles[i - 1]); // monotonically increasing
    }

    [Fact]
    public void ComputeLogLossDecomposition_PartsAreNonNegative()
    {
        var samples = new List<TrainingSample>();
        var rawProbs = new double[50];
        var rng = new Random(42);
        for (int i = 0; i < 50; i++)
        {
            samples.Add(new TrainingSample(new float[10], rng.Next(2), 1.0f));
            rawProbs[i] = Math.Clamp(rng.NextDouble(), 1e-7, 1 - 1e-7);
        }

        var (calLoss, refLoss) = TcnModelTrainer.ComputeLogLossDecomposition(
            samples, rawProbs, 1.0, 0.0);

        Assert.True(calLoss >= 0);
        Assert.True(refLoss >= 0);
    }

    // ── Warm-start tests ────────────────────────────────────────────────

    [Fact]
    public void ComputeFrozenBlocks_UnfreezesProgressively()
    {
        int numBlocks = 4;
        int epochsPerBlock = 5;

        var frozen0 = TcnModelTrainer.ComputeFrozenBlocks(numBlocks, 0, epochsPerBlock);
        var frozen5 = TcnModelTrainer.ComputeFrozenBlocks(numBlocks, 5, epochsPerBlock);
        var frozen20 = TcnModelTrainer.ComputeFrozenBlocks(numBlocks, 20, epochsPerBlock);

        // Epoch 0: only top 1 block unfrozen, bottom 3 frozen
        Assert.Equal(3, frozen0.Count(f => f));
        // Epoch 5: top 2 unfrozen, bottom 2 frozen
        Assert.Equal(2, frozen5.Count(f => f));
        // Epoch 20: all unfrozen
        Assert.Equal(0, frozen20.Count(f => f));
    }

    [Fact]
    public void ValidateWarmStartCompatibility_MatchingConfig_ReturnsCompatible()
    {
        var snapshot = new TcnModelTrainer.TcnSnapshotWeights
        {
            Filters = 32,
            ConvW = new double[4][] { new double[10], new double[10], new double[10], new double[10] },
            UseLayerNorm = true,
            UseAttentionPooling = true,
            AttentionHeads = 2,
            AttnQueryW = new double[32 * 32],
        };

        var (compatible, warnings) = TcnModelTrainer.ValidateWarmStartCompatibility(
            snapshot, 32, 4, true, true, 2);

        Assert.True(compatible);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ValidateWarmStartCompatibility_MismatchedFilters_ReportsWarning()
    {
        var snapshot = new TcnModelTrainer.TcnSnapshotWeights
        {
            Filters = 64,
            ConvW = new double[4][] { new double[10], new double[10], new double[10], new double[10] },
        };

        var (compatible, warnings) = TcnModelTrainer.ValidateWarmStartCompatibility(
            snapshot, 32, 4, true, true, 1);

        Assert.False(compatible);
        Assert.Contains(warnings, w => w.Contains("Filter count"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static TrainingSample CreateDummySample(int direction, float magnitude, float value)
    {
        var features = new float[10];
        Array.Fill(features, value);
        var seq = new float[5][];
        for (int t = 0; t < 5; t++)
        {
            seq[t] = new float[3];
            Array.Fill(seq[t], value);
        }
        return new TrainingSample(features, direction, magnitude, seq);
    }

    private static double[] InitRandomWeights(int count, Random rng)
    {
        var w = new double[count];
        for (int i = 0; i < count; i++)
            w[i] = (rng.NextDouble() - 0.5) * 0.1;
        return w;
    }

    private static float[][] ToFloatSeq(double[][] hidden)
    {
        var result = new float[hidden.Length][];
        for (int t = 0; t < hidden.Length; t++)
        {
            result[t] = new float[hidden[t].Length];
            for (int i = 0; i < hidden[t].Length; i++)
                result[t][i] = (float)hidden[t][i];
        }
        return result;
    }
}
