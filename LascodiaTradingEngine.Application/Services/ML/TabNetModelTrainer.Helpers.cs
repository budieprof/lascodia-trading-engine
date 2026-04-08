using System.Numerics;
using System.Runtime.CompilerServices;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  SIMD-ACCELERATED PRIMITIVES
    //  Uses System.Numerics.Vector<double> for hardware-width auto-vectorization.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Dot product of w[0..len) and x[0..len) using SIMD.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SimdDot(double[] w, double[] x, int len)
    {
        int vecLen = Vector<double>.Count;
        int i = 0;
        double sum = 0;

        if (len >= vecLen)
        {
            var vSum = Vector<double>.Zero;
            int end = len - vecLen + 1;
            for (; i < end; i += vecLen)
                vSum += new Vector<double>(w, i) * new Vector<double>(x, i);
            sum = Vector.Dot(vSum, Vector<double>.One);
        }

        for (; i < len; i++)
            sum += w[i] * x[i];

        return sum;
    }

    /// <summary>dst[j] += a * w[j] for j in [0..len), SIMD-accelerated.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SimdMulAdd(double[] dst, double[] w, double a, int len)
    {
        int vecLen = Vector<double>.Count;
        int i = 0;
        var vA = new Vector<double>(a);

        if (len >= vecLen)
        {
            int end = len - vecLen + 1;
            for (; i < end; i += vecLen)
            {
                var vDst = new Vector<double>(dst, i);
                var vW   = new Vector<double>(w, i);
                (vDst + vW * vA).CopyTo(dst, i);
            }
        }

        for (; i < len; i++)
            dst[i] += w[i] * a;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  INFERENCE HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static double TabNetRawProb(float[] features, TabNetWeights w)
    {
        var priorBuf = new double[w.F];
        var attnBuf  = new double[w.F];
        var fwd = ForwardPass(features, w, priorBuf, attnBuf, false, 0, null);
        return fwd.Prob;
    }

    private static double TabNetCalibProb(float[] features, TabNetWeights w, double plattA, double plattB)
    {
        double raw = Math.Clamp(TabNetRawProb(features, w), ProbClampMin, 1.0 - ProbClampMin);
        return Sigmoid(plattA * Logit(raw) + plattB);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT SANITIZATION
    // ═══════════════════════════════════════════════════════════════════════

    private static int SanitizeWeights(TabNetWeights w)
    {
        int count = 0;
        if (w.InitialBnFcW.Length > 0) { foreach (var r in w.InitialBnFcW) count += SanitizeArr(r); count += SanitizeArr(w.InitialBnFcB); }
        count += SanitizeArr(w.OutputW);
        if (!double.IsFinite(w.OutputB)) { w.OutputB = 0.0; count++; }
        if (w.MagW.Length > 0) count += SanitizeArr(w.MagW);
        if (!double.IsFinite(w.MagB)) { w.MagB = 0.0; count++; }
        foreach (var l in w.SharedW) foreach (var r in l) count += SanitizeArr(r);
        foreach (var l in w.SharedB) count += SanitizeArr(l);
        foreach (var l in w.SharedGW) foreach (var r in l) count += SanitizeArr(r);
        foreach (var l in w.SharedGB) count += SanitizeArr(l);
        foreach (var s in w.StepW) foreach (var l in s) foreach (var r in l) count += SanitizeArr(r);
        foreach (var s in w.StepB) foreach (var l in s) count += SanitizeArr(l);
        foreach (var s in w.StepGW) foreach (var l in s) foreach (var r in l) count += SanitizeArr(r);
        foreach (var s in w.StepGB) foreach (var l in s) count += SanitizeArr(l);
        foreach (var s in w.AttnFcW) foreach (var r in s) count += SanitizeArr(r);
        foreach (var s in w.AttnFcB) count += SanitizeArr(s);
        foreach (var b in w.BnGamma) count += SanitizeArr(b);
        foreach (var b in w.BnBeta) count += SanitizeArr(b);
        return count;
    }

    private static int SanitizeArr(double[] arr)
    {
        int c = 0;
        for (int i = 0; i < arr.Length; i++)
            if (!double.IsFinite(arr[i])) { arr[i] = 0.0; c++; }
        return c;
    }

    private static void SanitizeFloatArr(float[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            if (!float.IsFinite(arr[i])) arr[i] = 0f;
    }

    private static void SanitizeSnapshotArrays(ModelSnapshot s)
    {
        if (s.MagWeights is { Length: > 0 }) SanitizeArr(s.MagWeights);
        if (s.MagQ90Weights is { Length: > 0 }) SanitizeArr(s.MagQ90Weights);
        if (s.FeatureImportance is { Length: > 0 }) SanitizeFloatArr(s.FeatureImportance);
        if (s.FeatureImportanceScores is { Length: > 0 }) SanitizeArr(s.FeatureImportanceScores);
        if (s.IsotonicBreakpoints is { Length: > 0 }) SanitizeArr(s.IsotonicBreakpoints);
        if (s.JackknifeResiduals is { Length: > 0 }) SanitizeArr(s.JackknifeResiduals);
        if (s.MetaLabelWeights is { Length: > 0 }) SanitizeArr(s.MetaLabelWeights);
        if (s.AbstentionWeights is { Length: > 0 }) SanitizeArr(s.AbstentionWeights);
        if (s.FeatureStabilityScores is { Length: > 0 }) SanitizeArr(s.FeatureStabilityScores);
        if (s.FeatureQuantileBreakpoints is not null)
            foreach (var bp in s.FeatureQuantileBreakpoints)
                if (bp is { Length: > 0 }) SanitizeArr(bp);

        // v3 weight arrays
        if (s.TabNetSharedWeights is not null)
            foreach (var l in s.TabNetSharedWeights) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetSharedBiases is not null)
            foreach (var l in s.TabNetSharedBiases) SanitizeArr(l);
        if (s.TabNetSharedGateWeights is not null)
            foreach (var l in s.TabNetSharedGateWeights) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetSharedGateBiases is not null)
            foreach (var l in s.TabNetSharedGateBiases) SanitizeArr(l);
        if (s.TabNetStepFcWeights is not null)
            foreach (var st in s.TabNetStepFcWeights) foreach (var l in st) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetStepFcBiases is not null)
            foreach (var st in s.TabNetStepFcBiases) foreach (var l in st) SanitizeArr(l);
        if (s.TabNetStepGateWeights is not null)
            foreach (var st in s.TabNetStepGateWeights) foreach (var l in st) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetStepGateBiases is not null)
            foreach (var st in s.TabNetStepGateBiases) foreach (var l in st) SanitizeArr(l);
        if (s.TabNetAttentionFcWeights is not null)
            foreach (var st in s.TabNetAttentionFcWeights) foreach (var r in st) SanitizeArr(r);
        if (s.TabNetAttentionFcBiases is not null)
            foreach (var st in s.TabNetAttentionFcBiases) SanitizeArr(st);
        if (s.TabNetBnGammas is not null) foreach (var b in s.TabNetBnGammas) SanitizeArr(b);
        if (s.TabNetBnBetas is not null) foreach (var b in s.TabNetBnBetas) SanitizeArr(b);
        if (s.TabNetBnRunningMeans is not null) foreach (var b in s.TabNetBnRunningMeans) SanitizeArr(b);
        if (s.TabNetBnRunningVars is not null) foreach (var b in s.TabNetBnRunningVars) SanitizeArr(b);
        if (s.TabNetOutputHeadWeights is { Length: > 0 }) SanitizeArr(s.TabNetOutputHeadWeights);
        if (s.TabNetInitialBnFcW is not null) foreach (var r in s.TabNetInitialBnFcW) SanitizeArr(r);
        if (s.TabNetInitialBnFcB is { Length: > 0 }) SanitizeArr(s.TabNetInitialBnFcB);
        if (s.TabNetPerStepAttention is not null) foreach (var r in s.TabNetPerStepAttention) SanitizeArr(r);
        if (s.TabNetAttentionEntropy is { Length: > 0 }) SanitizeArr(s.TabNetAttentionEntropy);

        // Legacy arrays
        if (s.Weights is not null)
            foreach (var w in s.Weights) if (w is { Length: > 0 }) SanitizeArr(w);
        if (s.Biases is { Length: > 0 }) SanitizeArr(s.Biases);

        // Scalar fields
        if (!double.IsFinite(s.WalkForwardSharpeTrend)) s.WalkForwardSharpeTrend = 0.0;
        if (!double.IsFinite(s.BrierSkillScore)) s.BrierSkillScore = 0.0;
        if (!double.IsFinite(s.ConformalQHat)) s.ConformalQHat = 0.5;
        if (!double.IsFinite(s.Ece)) s.Ece = 1.0;
        if (!double.IsFinite(s.OptimalThreshold)) s.OptimalThreshold = 0.5;
        if (!double.IsFinite(s.MetaLabelThreshold)) s.MetaLabelThreshold = 0.5;
        if (!double.IsFinite(s.AgeDecayLambda)) s.AgeDecayLambda = 0.0;
        if (!double.IsFinite(s.AdaptiveLabelSmoothing)) s.AdaptiveLabelSmoothing = 0.0;
        if (!double.IsFinite(s.ConformalCoverage)) s.ConformalCoverage = 0.0;
        if (!double.IsFinite(s.PlattA)) s.PlattA = 1.0;
        if (!double.IsFinite(s.PlattB)) s.PlattB = 0.0;
        if (!double.IsFinite(s.PlattABuy)) s.PlattABuy = 1.0;
        if (!double.IsFinite(s.PlattBBuy)) s.PlattBBuy = 0.0;
        if (!double.IsFinite(s.PlattASell)) s.PlattASell = 1.0;
        if (!double.IsFinite(s.PlattBSell)) s.PlattBSell = 0.0;
        if (!double.IsFinite(s.MagBias)) s.MagBias = 0.0;
        if (!double.IsFinite(s.MagQ90Bias)) s.MagQ90Bias = 0.0;
        if (!double.IsFinite(s.MetaLabelBias)) s.MetaLabelBias = 0.0;
        if (!double.IsFinite(s.AbstentionBias)) s.AbstentionBias = 0.0;
        if (!double.IsFinite(s.AbstentionThreshold)) s.AbstentionThreshold = 0.5;
        if (!double.IsFinite(s.AvgKellyFraction)) s.AvgKellyFraction = 0.0;
        if (!double.IsFinite(s.DecisionBoundaryMean)) s.DecisionBoundaryMean = 0.0;
        if (!double.IsFinite(s.DecisionBoundaryStd)) s.DecisionBoundaryStd = 0.0;
        if (!double.IsFinite(s.DurbinWatsonStatistic)) s.DurbinWatsonStatistic = 2.0;
        if (!double.IsFinite(s.TabNetOutputHeadBias)) s.TabNetOutputHeadBias = 0.0;
        if (!double.IsFinite(s.TabNetRelaxationGamma)) s.TabNetRelaxationGamma = 1.5;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FEATURE MASK & PRUNING
    // ═══════════════════════════════════════════════════════════════════════

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int F)
    {
        var mask = new bool[F];
        if (threshold <= 0) { Array.Fill(mask, true); return mask; }
        double equalShare = 1.0 / F;
        for (int i = 0; i < F; i++) mask[i] = importance[i] >= threshold * equalShare;
        return mask;
    }

    private static List<TrainingSample> ApplyMask(IReadOnlyList<TrainingSample> samples, bool[] mask)
    {
        int maskedF = mask.Count(m => m);
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var nf = new float[maskedF]; int ni = 0;
            for (int j = 0; j < mask.Length && j < s.Features.Length; j++)
                if (mask[j]) nf[ni++] = s.Features[j];
            result.Add(s with { Features = nf });
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  POLYNOMIAL FEATURE AUGMENTATION
    // ═══════════════════════════════════════════════════════════════════════

    private static int[] SelectPolyTopFeatureIndices(List<TrainingSample> samples, int F, ModelSnapshot? warmStart, int topN)
    {
        topN = Math.Min(topN, F); int n = samples.Count; double[] scores = new double[F];
        if (warmStart?.FeatureImportanceScores is { Length: > 0 } prior && prior.Length == F)
            for (int j = 0; j < F; j++) scores[j] = prior[j];
        else
        {
            double[] featureMeans = new double[F];
            for (int i = 0; i < n; i++) for (int j = 0; j < F; j++) featureMeans[j] += samples[i].Features[j];
            for (int j = 0; j < F; j++) featureMeans[j] /= n;
            for (int i = 0; i < n; i++) for (int j = 0; j < F; j++) { double d = samples[i].Features[j] - featureMeans[j]; scores[j] += d * d; }
            for (int j = 0; j < F; j++) scores[j] /= n;
        }
        return scores.Select((s, idx) => (Score: s, Idx: idx)).OrderByDescending(t => t.Score)
            .Take(topN).Select(t => t.Idx).OrderBy(i => i).ToArray();
    }

    private static List<TrainingSample> AugmentSamplesWithPoly(List<TrainingSample> samples, int origF, int[] topIdx)
    {
        int pairCount = topIdx.Length * (topIdx.Length - 1) / 2, newF = origF + pairCount;
        var augmented = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var nf = new float[newF];
            for (int j = 0; j < origF; j++) nf[j] = s.Features[j];
            int k = origF;
            for (int a = 0; a < topIdx.Length; a++)
                for (int b = a + 1; b < topIdx.Length; b++)
                    nf[k++] = s.Features[topIdx[a]] * s.Features[topIdx[b]];
            augmented.Add(s with { Features = nf });
        }
        return augmented;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MATH UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static double Sigmoid(double x)
        => 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -50, 50)));

    private static double Logit(double p)
        => Math.Log(p / (1.0 - p));

    private static double StdDev(IReadOnlyList<double> vals, double mean)
    {
        if (vals.Count < 2) return 0;
        double variance = vals.Sum(v => (v - mean) * (v - mean)) / (vals.Count - 1);
        return Math.Sqrt(variance);
    }

    private static double Variance(double[] vals, int start, int count)
    {
        if (count < 2) return 0;
        double mean = 0;
        for (int i = start; i < start + count; i++) mean += vals[i];
        mean /= count;
        double var_ = 0;
        for (int i = start; i < start + count; i++) var_ += (vals[i] - mean) * (vals[i] - mean);
        return var_ / (count - 1);
    }

    private static double[] XavierVec(Random rng, int fanIn, int fanOut, int dummy)
    {
        double scale = Math.Sqrt(2.0 / (fanIn + fanOut));
        return Enumerable.Range(0, fanIn).Select(_ => (rng.NextDouble() * 2 - 1) * scale).ToArray();
    }

    private static double[][] XavierMatrix(Random rng, int rows, int cols)
    {
        double scale = Math.Sqrt(2.0 / (rows + cols));
        var m = new double[rows][];
        for (int i = 0; i < rows; i++)
        {
            m[i] = new double[cols];
            for (int j = 0; j < cols; j++) m[i][j] = (rng.NextDouble() * 2 - 1) * scale;
        }
        return m;
    }

    private static double[] FcLinear(double[] input, int inDim, int outDim, double[][] w, double[] b)
    {
        var output = new double[outDim];
        for (int i = 0; i < outDim; i++)
        {
            output[i] = b[i];
            for (int j = 0; j < inDim && j < w[i].Length; j++) output[i] += w[i][j] * input[j];
        }
        return output;
    }

    private static double[] FcSigmoid(double[] input, int inDim, int outDim, double[][] w, double[] b)
    {
        var output = FcLinear(input, inDim, outDim, w, b);
        for (int i = 0; i < outDim; i++) output[i] = Sigmoid(output[i]);
        return output;
    }

    private static double ComputeMI(double[] a, double[] b, int bins)
    {
        double minA = a.Min(), maxA = a.Max(), minB = b.Min(), maxB = b.Max();
        double wA = (maxA - minA) / bins + Eps, wB = (maxB - minB) / bins + Eps;
        int n = a.Length; var joint = new int[bins, bins]; var mA = new int[bins]; var mB = new int[bins];
        for (int i = 0; i < n; i++)
        {
            int ia = Math.Clamp((int)((a[i] - minA) / wA), 0, bins - 1), ib = Math.Clamp((int)((b[i] - minB) / wB), 0, bins - 1);
            joint[ia, ib]++; mA[ia]++; mB[ib]++;
        }
        double mi = 0;
        for (int i = 0; i < bins; i++)
            for (int j = 0; j < bins; j++)
            {
                if (joint[i, j] == 0) continue;
                double pxy = (double)joint[i, j] / n, px = (double)mA[i] / n, py = (double)mB[j] / n;
                mi += pxy * Math.Log(pxy / (px * py + Eps) + Eps);
            }
        return Math.Max(0, mi);
    }

    private static double ComputeEntropy(double[] vals, int bins)
    {
        double min = vals.Min(), max = vals.Max(), width = (max - min) / bins + Eps;
        int n = vals.Length; var counts = new int[bins];
        for (int i = 0; i < n; i++) counts[Math.Clamp((int)((vals[i] - min) / width), 0, bins - 1)]++;
        double h = 0;
        for (int i = 0; i < bins; i++) { if (counts[i] == 0) continue; double p = (double)counts[i] / n; h -= p * Math.Log(p); }
        return h;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DEEP CLONE HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static double[][] CloneDim2(double[][] src) =>
        src.Select(r => (double[])r.Clone()).ToArray();

    private static double[][][] CloneDim3(double[][][] src) =>
        src.Select(m => m.Select(r => (double[])r.Clone()).ToArray()).ToArray();

    private static double[][][][] CloneDim4(double[][][][] src) =>
        src.Select(s => s.Select(m => m.Select(r => (double[])r.Clone()).ToArray()).ToArray()).ToArray();

    private static double[][] DeepClone2(double[][] src) => CloneDim2(src);
    private static double[][][] DeepClone3(double[][][] src) => CloneDim3(src);
    private static double[][][][] DeepClone4(double[][][][] src) => CloneDim4(src);

    // ═══════════════════════════════════════════════════════════════════════
    //  ZERO-ALLOCATION HELPERS (same shape as source, all values zero)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[][] ZeroDim2(double[][] src) =>
        src.Select(r => new double[r.Length]).ToArray();

    private static double[][][] ZeroDim3(double[][][] src) =>
        src.Select(m => m.Select(r => new double[r.Length]).ToArray()).ToArray();

    private static double[][][][] ZeroDim4(double[][][][] src) =>
        src.Select(s => s.Select(m => m.Select(r => new double[r.Length]).ToArray()).ToArray()).ToArray();

    private static void CopyArray(double[] src, double[] dst)
    {
        int len = Math.Min(src.Length, dst.Length);
        Array.Copy(src, dst, len);
    }

    private static void CopyMatrix(double[][] src, double[][] dst)
    {
        int rows = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < rows; i++) CopyArray(src[i], dst[i]);
    }
}
