using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ───────────────────────────────────────────────────────────────────
    //  Item 21: SHAP-style channel attribution
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes approximate Shapley values for the top-K most important channels.
    /// Uses exact enumeration of 2^K subsets (K ≤ 6 for tractability).
    /// For each subset S, evaluates model accuracy with only channels in S active.
    /// Shapley value = weighted average of marginal contributions across all subsets.
    /// </summary>
    internal static double[] ComputeShapleyValues(
        List<TrainingSample> testSet, TcnWeights tcn, double plattA, double plattB,
        int filters, bool useAttentionPool, float[] permImportance, int topK = 6)
    {
        int channelCount = testSet[0].SequenceFeatures![0].Length;
        topK = Math.Min(topK, Math.Min(channelCount, 6));

        // Find top-K channel indices by permutation importance
        var ranked = permImportance
            .Select((imp, idx) => (imp, idx))
            .OrderByDescending(x => x.imp)
            .Take(topK)
            .Select(x => x.idx)
            .ToArray();

        int subsets = 1 << topK;
        var subsetAccuracies = new double[subsets];

        // Evaluate each subset
        for (int mask = 0; mask < subsets; mask++)
        {
            var channelMask = new bool[channelCount];
            // Active all channels not in top-K (they're always included)
            for (int c = 0; c < channelCount; c++) channelMask[c] = true;
            // Only include top-K channels that are in this subset mask
            for (int k = 0; k < topK; k++)
            {
                if ((mask & (1 << k)) == 0)
                    channelMask[ranked[k]] = false;
            }

            int correct = 0;
            for (int si = 0; si < testSet.Count; si++)
            {
                var origSeq = testSet[si].SequenceFeatures!;
                var maskedSeq = new float[origSeq.Length][];
                for (int t = 0; t < origSeq.Length; t++)
                {
                    maskedSeq[t] = (float[])origSeq[t].Clone();
                    for (int c = 0; c < maskedSeq[t].Length; c++)
                        if (!channelMask[c]) maskedSeq[t][c] = 0f;
                }
                var sample = testSet[si] with { SequenceFeatures = maskedSeq };
                double p = Math.Clamp(TcnProb(sample, tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
                double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(p) + plattB);
                if ((calibP >= 0.5) == (testSet[si].Direction == 1)) correct++;
            }
            subsetAccuracies[mask] = (double)correct / testSet.Count;
        }

        // Compute Shapley values
        var shapley = new double[channelCount];
        for (int k = 0; k < topK; k++)
        {
            double sv = 0;
            int playerBit = 1 << k;
            for (int mask = 0; mask < subsets; mask++)
            {
                if ((mask & playerBit) != 0) continue; // only coalitions without player k
                int coalSize = 0;
                for (int b = 0; b < topK; b++) if ((mask & (1 << b)) != 0) coalSize++;

                // Weight: |S|!(K-|S|-1)! / K!
                double weight = Factorial(coalSize) * Factorial(topK - coalSize - 1) / Factorial(topK);
                double marginal = subsetAccuracies[mask | playerBit] - subsetAccuracies[mask];
                sv += weight * marginal;
            }
            shapley[ranked[k]] = sv;
        }

        return shapley;
    }

    private static double Factorial(int n)
    {
        double f = 1;
        for (int i = 2; i <= n; i++) f *= i;
        return f;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 22: PICP (Prediction Interval Coverage Probability)
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Measures actual coverage of conformal prediction intervals on the test set.
    /// Returns the fraction of samples where the true class is contained in the prediction set.
    /// </summary>
    internal static double ComputePicp(
        List<TrainingSample> testSet, double[] rawProbs, double plattA, double plattB, double conformalQHat)
    {
        if (testSet.Count == 0) return 0;
        int covered = 0;
        for (int i = 0; i < testSet.Count; i++)
        {
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProbs[i]) + plattB);
            double trueClassConf = testSet[i].Direction == 1 ? calibP : 1 - calibP;
            // Covered if nonconformity score ≤ qHat
            if (1.0 - trueClassConf <= conformalQHat) covered++;
        }
        return (double)covered / testSet.Count;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 23: Reliability diagram serialisation
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes reliability diagram bin data (confidence, accuracy, count per bin).
    /// Uses adaptive bin edges to ensure minimum samples per bin.
    /// </summary>
    internal static (double[] BinConfidence, double[] BinAccuracy, int[] BinCounts) ComputeReliabilityDiagram(
        List<TrainingSample> samples, double[] rawProbs, double plattA, double plattB, int numBins = 10)
    {
        if (samples.Count < numBins * 2) return ([], [], []);

        var pairs = new (double CalibP, bool IsPositive)[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProbs[i]) + plattB);
            pairs[i] = (calibP, samples[i].Direction == 1);
        }
        Array.Sort(pairs, (a, b) => a.CalibP.CompareTo(b.CalibP));

        int samplesPerBin = samples.Count / numBins;
        var conf = new double[numBins];
        var acc = new double[numBins];
        var counts = new int[numBins];

        for (int b = 0; b < numBins; b++)
        {
            int start = b * samplesPerBin;
            int end = b == numBins - 1 ? samples.Count : (b + 1) * samplesPerBin;
            int count = end - start;
            double sumConf = 0; int sumPos = 0;
            for (int i = start; i < end; i++)
            {
                sumConf += pairs[i].CalibP;
                if (pairs[i].IsPositive) sumPos++;
            }
            conf[b] = count > 0 ? sumConf / count : 0;
            acc[b] = count > 0 ? (double)sumPos / count : 0;
            counts[b] = count;
        }

        return (conf, acc, counts);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 24: Log-loss decomposition (Murphy decomposition)
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decomposes total log-loss into calibration loss + refinement loss (Murphy decomposition).
    /// Calibration loss: Σ n_b × KL(o_b || c_b) where o_b = observed freq, c_b = mean confidence.
    /// Refinement loss: Σ n_b × H(o_b) where H is binary entropy.
    /// </summary>
    internal static (double CalibrationLoss, double RefinementLoss) ComputeLogLossDecomposition(
        List<TrainingSample> samples, double[] rawProbs, double plattA, double plattB, int numBins = 10)
    {
        if (samples.Count < 20) return (0, 0);

        var binConf = new double[numBins]; var binPos = new int[numBins]; var binCount = new int[numBins];
        for (int i = 0; i < samples.Count; i++)
        {
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProbs[i]) + plattB);
            int bin = Math.Clamp((int)(calibP * numBins), 0, numBins - 1);
            binConf[bin] += calibP; binCount[bin]++;
            if (samples[i].Direction == 1) binPos[bin]++;
        }

        double calLoss = 0, refLoss = 0;
        int n = samples.Count;
        for (int b = 0; b < numBins; b++)
        {
            if (binCount[b] == 0) continue;
            double weight = (double)binCount[b] / n;
            double c = binConf[b] / binCount[b]; // mean confidence
            double o = (double)binPos[b] / binCount[b]; // observed frequency

            // Calibration: KL(o || c)
            if (o > 0 && o < 1 && c > 0 && c < 1)
                calLoss += weight * (o * Math.Log(o / c) + (1 - o) * Math.Log((1 - o) / (1 - c)));

            // Refinement: binary entropy H(o)
            if (o > 0 && o < 1)
                refLoss += weight * -(o * Math.Log(o) + (1 - o) * Math.Log(1 - o));
        }

        return (calLoss, refLoss);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 25: Post-isotonic ECE
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes ECE after applying the full isotonic calibration pipeline (Platt + PAVA).
    /// </summary>
    internal static double ComputePostIsotonicEce(
        List<TrainingSample> testSet, double[] rawProbs, double plattA, double plattB,
        double[] isotonicBreakpoints)
    {
        if (testSet.Count < 20 || isotonicBreakpoints.Length == 0) return 0;
        const int B = 10;
        var binConf = new double[B]; var binPos = new int[B]; var binCount = new int[B];

        for (int i = 0; i < testSet.Count; i++)
        {
            double plattP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProbs[i]) + plattB);
            double isoP = ApplyIsotonicMapping(plattP, isotonicBreakpoints);
            int bin = Math.Clamp((int)(isoP * B), 0, B - 1);
            binConf[bin] += isoP; binCount[bin]++;
            if (testSet[i].Direction == 1) binPos[bin]++;
        }

        double ece = 0;
        for (int b = 0; b < B; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConf[b] / binCount[b];
            double avgAcc = (double)binPos[b] / binCount[b];
            ece += Math.Abs(avgConf - avgAcc) * binCount[b] / testSet.Count;
        }
        return ece;
    }

    /// <summary>Applies isotonic mapping from PAVA breakpoints via linear interpolation.</summary>
    private static double ApplyIsotonicMapping(double p, double[] breakpoints)
    {
        if (breakpoints.Length < 2) return p;
        // breakpoints are interleaved [x0, y0, x1, y1, ...]
        for (int i = 0; i < breakpoints.Length - 2; i += 2)
        {
            double x0 = breakpoints[i], y0 = breakpoints[i + 1];
            double x1 = breakpoints[i + 2], y1 = breakpoints[i + 3];
            if (p >= x0 && p <= x1)
            {
                if (Math.Abs(x1 - x0) < 1e-10) return y0;
                return y0 + (y1 - y0) * (p - x0) / (x1 - x0);
            }
        }
        // Extrapolate: return last y value
        return breakpoints[breakpoints.Length - 1];
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 26: Temporal stability of predictions
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes lag-1 autocorrelation of predicted probabilities on the test set.
    /// High autocorrelation suggests the model reacts slowly to regime changes.
    /// </summary>
    internal static double ComputePredictionAutocorrelation(
        List<TrainingSample> testSet, double[] rawProbs, double plattA, double plattB)
    {
        if (testSet.Count < 10) return 0;
        int n = testSet.Count;
        var calibP = new double[n];
        double mean = 0;
        for (int i = 0; i < n; i++)
        {
            calibP[i] = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProbs[i]) + plattB);
            mean += calibP[i];
        }
        mean /= n;

        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            double d = calibP[i] - mean;
            den += d * d;
            if (i > 0) num += (calibP[i] - mean) * (calibP[i - 1] - mean);
        }
        return den > 1e-10 ? num / den : 0;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 27: Prediction confidence histogram
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes quantile summary of |calibP - 0.5| on the test set: [p10, p25, p50, p75, p90].
    /// Measures the distribution of prediction confidence.
    /// </summary>
    internal static double[] ComputeConfidenceHistogram(
        List<TrainingSample> testSet, double[] rawProbs, double plattA, double plattB)
    {
        if (testSet.Count < 10) return [];
        var distances = new double[testSet.Count];
        for (int i = 0; i < testSet.Count; i++)
        {
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProbs[i]) + plattB);
            distances[i] = Math.Abs(calibP - 0.5);
        }
        Array.Sort(distances);

        return [
            Percentile(distances, 0.10),
            Percentile(distances, 0.25),
            Percentile(distances, 0.50),
            Percentile(distances, 0.75),
            Percentile(distances, 0.90),
        ];
    }

    private static double Percentile(double[] sorted, double p)
    {
        double idx = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        double frac = idx - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}
