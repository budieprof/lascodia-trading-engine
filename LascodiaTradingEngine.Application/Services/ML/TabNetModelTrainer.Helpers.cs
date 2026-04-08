using System.Numerics;
using System.Runtime.CompilerServices;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

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

    private static double TabNetCalibProb(float[] features, TabNetWeights w, ModelSnapshot calibrationSnapshot)
    {
        double raw = Math.Clamp(TabNetRawProb(features, w), ProbClampMin, 1.0 - ProbClampMin);
        return InferenceHelpers.ApplyDeployedCalibration(raw, calibrationSnapshot);
    }

    private static ModelSnapshot BuildCalibrationSnapshot(
        double plattA,
        double plattB,
        double temperatureScale = 0.0,
        double plattABuy = 0.0,
        double plattBBuy = 0.0,
        double plattASell = 0.0,
        double plattBSell = 0.0,
        double[]? isotonicBreakpoints = null,
        double conditionalRoutingThreshold = 0.5)
    {
        return new ModelSnapshot
        {
            PlattA = double.IsFinite(plattA) ? plattA : 1.0,
            PlattB = double.IsFinite(plattB) ? plattB : 0.0,
            TemperatureScale = double.IsFinite(temperatureScale) && temperatureScale > 0.0 ? temperatureScale : 0.0,
            PlattABuy = double.IsFinite(plattABuy) ? plattABuy : 0.0,
            PlattBBuy = double.IsFinite(plattBBuy) ? plattBBuy : 0.0,
            PlattASell = double.IsFinite(plattASell) ? plattASell : 0.0,
            PlattBSell = double.IsFinite(plattBSell) ? plattBSell : 0.0,
            ConditionalCalibrationRoutingThreshold = double.IsFinite(conditionalRoutingThreshold)
                ? Math.Clamp(conditionalRoutingThreshold, 0.01, 0.99)
                : 0.5,
            IsotonicBreakpoints = isotonicBreakpoints is { Length: > 0 }
                ? (double[])isotonicBreakpoints.Clone()
                : [],
        };
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
        static void SanitizeMetricSummary(TabNetMetricSummary? summary)
        {
            if (summary is null)
                return;

            if (!double.IsFinite(summary.Threshold)) summary.Threshold = 0.5;
            if (!double.IsFinite(summary.Accuracy)) summary.Accuracy = 0.0;
            if (!double.IsFinite(summary.Precision)) summary.Precision = 0.0;
            if (!double.IsFinite(summary.Recall)) summary.Recall = 0.0;
            if (!double.IsFinite(summary.F1)) summary.F1 = 0.0;
            if (!double.IsFinite(summary.ExpectedValue)) summary.ExpectedValue = 0.0;
            if (!double.IsFinite(summary.BrierScore)) summary.BrierScore = 1.0;
            if (!double.IsFinite(summary.WeightedAccuracy)) summary.WeightedAccuracy = summary.Accuracy;
            if (!double.IsFinite(summary.SharpeRatio)) summary.SharpeRatio = 0.0;
            if (!double.IsFinite(summary.Ece)) summary.Ece = 1.0;
        }

        s.FeaturePipelineTransforms ??= [];
        s.FeaturePipelineDescriptors ??= [];
        if (s.TabNetAuditFindings is null) s.TabNetAuditFindings = [];
        SanitizeMetricSummary(s.TabNetSelectionMetrics);
        SanitizeMetricSummary(s.TabNetCalibrationMetrics);
        SanitizeMetricSummary(s.TabNetTestMetrics);
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
        if (s.TabNetPerStepSparsity is { Length: > 0 }) SanitizeArr(s.TabNetPerStepSparsity);
        if (s.TabNetBnDriftByLayer is { Length: > 0 }) SanitizeArr(s.TabNetBnDriftByLayer);
        if (s.TabNetActivationCentroid is { Length: > 0 }) SanitizeArr(s.TabNetActivationCentroid);

        // Legacy arrays
        if (s.Weights is not null)
            foreach (var w in s.Weights) if (w is { Length: > 0 }) SanitizeArr(w);
        if (s.Biases is { Length: > 0 }) SanitizeArr(s.Biases);

        // Scalar fields
        if (!double.IsFinite(s.WalkForwardSharpeTrend)) s.WalkForwardSharpeTrend = 0.0;
        if (!double.IsFinite(s.BrierSkillScore)) s.BrierSkillScore = 0.0;
        if (!double.IsFinite(s.ConformalQHat)) s.ConformalQHat = 0.5;
        if (!double.IsFinite(s.ConformalQHatBuy)) s.ConformalQHatBuy = s.ConformalQHat;
        if (!double.IsFinite(s.ConformalQHatSell)) s.ConformalQHatSell = s.ConformalQHat;
        if (!double.IsFinite(s.Ece)) s.Ece = 1.0;
        if (!double.IsFinite(s.OptimalThreshold)) s.OptimalThreshold = 0.5;
        if (!double.IsFinite(s.MetaLabelThreshold)) s.MetaLabelThreshold = 0.5;
        if (!double.IsFinite(s.AgeDecayLambda)) s.AgeDecayLambda = 0.0;
        if (!double.IsFinite(s.AdaptiveLabelSmoothing)) s.AdaptiveLabelSmoothing = 0.0;
        if (!double.IsFinite(s.ConformalCoverage)) s.ConformalCoverage = 0.0;
        if (!double.IsFinite(s.PlattA)) s.PlattA = 1.0;
        if (!double.IsFinite(s.PlattB)) s.PlattB = 0.0;
        if (!double.IsFinite(s.PlattABuy)) s.PlattABuy = 0.0;
        if (!double.IsFinite(s.PlattBBuy)) s.PlattBBuy = 0.0;
        if (!double.IsFinite(s.PlattASell)) s.PlattASell = 0.0;
        if (!double.IsFinite(s.PlattBSell)) s.PlattBSell = 0.0;
        if (!double.IsFinite(s.ConditionalCalibrationRoutingThreshold)) s.ConditionalCalibrationRoutingThreshold = 0.5;
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
        if (!double.IsFinite(s.TabNetActivationDistanceMean)) s.TabNetActivationDistanceMean = 0.0;
        if (!double.IsFinite(s.TabNetActivationDistanceStd)) s.TabNetActivationDistanceStd = 0.0;
        if (!double.IsFinite(s.TabNetAttentionEntropyThreshold)) s.TabNetAttentionEntropyThreshold = 0.0;
        if (!double.IsFinite(s.TabNetUncertaintyThreshold)) s.TabNetUncertaintyThreshold = 0.0;
        if (!double.IsFinite(s.TabNetWarmStartReuseRatio)) s.TabNetWarmStartReuseRatio = 0.0;
        if (!double.IsFinite(s.TabNetPruningScoreDelta)) s.TabNetPruningScoreDelta = 0.0;
        if (!double.IsFinite(s.TabNetTrainInferenceParityMaxError)) s.TabNetTrainInferenceParityMaxError = 0.0;
        if (!double.IsFinite(s.TabNetCalibrationResidualMean)) s.TabNetCalibrationResidualMean = 0.0;
        if (!double.IsFinite(s.TabNetCalibrationResidualStd)) s.TabNetCalibrationResidualStd = 0.0;
        if (!double.IsFinite(s.TabNetCalibrationResidualThreshold)) s.TabNetCalibrationResidualThreshold = 0.0;
        if (s.TabNetDriftArtifact is not null)
        {
            if (!double.IsFinite(s.TabNetDriftArtifact.NonStationaryFeatureFraction)) s.TabNetDriftArtifact.NonStationaryFeatureFraction = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MeanLag1Autocorrelation)) s.TabNetDriftArtifact.MeanLag1Autocorrelation = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MaxLag1Autocorrelation)) s.TabNetDriftArtifact.MaxLag1Autocorrelation = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MeanVarianceRatioDistance)) s.TabNetDriftArtifact.MeanVarianceRatioDistance = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MaxVarianceRatioDistance)) s.TabNetDriftArtifact.MaxVarianceRatioDistance = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MeanPopulationStabilityIndex)) s.TabNetDriftArtifact.MeanPopulationStabilityIndex = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MaxPopulationStabilityIndex)) s.TabNetDriftArtifact.MaxPopulationStabilityIndex = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MeanChangePointScore)) s.TabNetDriftArtifact.MeanChangePointScore = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MaxChangePointScore)) s.TabNetDriftArtifact.MaxChangePointScore = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MeanAdfLikeStatistic)) s.TabNetDriftArtifact.MeanAdfLikeStatistic = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MaxAdfLikeStatistic)) s.TabNetDriftArtifact.MaxAdfLikeStatistic = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MeanKpssLikeStatistic)) s.TabNetDriftArtifact.MeanKpssLikeStatistic = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MaxKpssLikeStatistic)) s.TabNetDriftArtifact.MaxKpssLikeStatistic = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MeanRecentMeanShiftScore)) s.TabNetDriftArtifact.MeanRecentMeanShiftScore = 0.0;
            if (!double.IsFinite(s.TabNetDriftArtifact.MaxRecentMeanShiftScore)) s.TabNetDriftArtifact.MaxRecentMeanShiftScore = 0.0;
        }
        if (s.TabNetCalibrationArtifact is not null)
        {
            if (string.IsNullOrWhiteSpace(s.TabNetCalibrationArtifact.CalibrationSelectionStrategy))
                s.TabNetCalibrationArtifact.CalibrationSelectionStrategy = "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS";
            if (!double.IsFinite(s.TabNetCalibrationArtifact.DiagnosticsSelectedGlobalNll))
                s.TabNetCalibrationArtifact.DiagnosticsSelectedGlobalNll = 0.0;
            if (!double.IsFinite(s.TabNetCalibrationArtifact.DiagnosticsSelectedStackNll))
                s.TabNetCalibrationArtifact.DiagnosticsSelectedStackNll = 0.0;
            if (!double.IsFinite(s.TabNetCalibrationArtifact.ConditionalRoutingThreshold))
                s.TabNetCalibrationArtifact.ConditionalRoutingThreshold = 0.5;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FEATURE MASK & PRUNING
    // ═══════════════════════════════════════════════════════════════════════

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int F, int minimumRetainedFeatures = 1)
    {
        var mask = new bool[F];
        if (threshold <= 0) { Array.Fill(mask, true); return mask; }
        double equalShare = 1.0 / F;
        for (int i = 0; i < F; i++) mask[i] = importance[i] >= threshold * equalShare;

        minimumRetainedFeatures = Math.Clamp(minimumRetainedFeatures, 1, Math.Max(1, F));
        int retained = mask.Count(m => m);
        if (retained >= minimumRetainedFeatures)
            return mask;

        Array.Clear(mask);
        foreach (int idx in Enumerable.Range(0, F)
                     .OrderByDescending(i => importance[i])
                     .ThenBy(i => i)
                     .Take(minimumRetainedFeatures))
        {
            mask[idx] = true;
        }

        return mask;
    }

    private static List<TrainingSample> ApplyMask(IReadOnlyList<TrainingSample> samples, bool[] mask)
    {
        if (mask.Length == 0 || mask.All(m => m))
        {
            var passthrough = new List<TrainingSample>(samples.Count);
            foreach (var s in samples) passthrough.Add(s);
            return passthrough;
        }

        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var nf = (float[])s.Features.Clone();
            for (int j = 0; j < mask.Length && j < nf.Length; j++)
            {
                if (!mask[j])
                    nf[j] = 0f;
            }
            result.Add(s with { Features = nf });
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  POLYNOMIAL FEATURE AUGMENTATION
    // ═══════════════════════════════════════════════════════════════════════

    private readonly record struct TabNetFeatureExpansionPlan(int[] TopFeatureIndices, int[][] ProductTerms)
    {
        public int AddedFeatureCount => ProductTerms.Length;
        public bool IsEnabled => ProductTerms.Length > 0;

        public static TabNetFeatureExpansionPlan Empty => new([], []);
    }

    private static int[] SelectPolyTopFeatureIndices(List<TrainingSample> samples, int F, ModelSnapshot? warmStart, int topN)
    {
        topN = Math.Min(topN, F);
        if (topN < 2 || samples.Count == 0)
            return [];

        int n = Math.Min(samples.Count, 600);
        int bins = Math.Max(4, (int)Math.Ceiling(1 + Math.Log2(n)));
        var labels = new double[n];
        var featureColumns = new double[F][];
        var variances = new double[F];
        double[] prior = warmStart?.FeatureImportanceScores is { Length: > 0 } warmPrior && warmPrior.Length >= F
            ? warmPrior
            : new double[F];

        for (int j = 0; j < F; j++)
            featureColumns[j] = new double[n];

        for (int i = 0; i < n; i++)
        {
            labels[i] = samples[i].Direction > 0 ? 1.0 : 0.0;
            for (int j = 0; j < F; j++)
                featureColumns[j][i] = samples[i].Features[j];
        }

        double maxPrior = prior.Length > 0 ? prior.Take(F).DefaultIfEmpty(0.0).Max() : 0.0;
        var relevance = new double[F];
        for (int j = 0; j < F; j++)
        {
            double mean = 0.0;
            for (int i = 0; i < n; i++) mean += featureColumns[j][i];
            mean /= n;

            double variance = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = featureColumns[j][i] - mean;
                variance += d * d;
            }
            variances[j] = variance / n;

            double mi = ComputeMI(featureColumns[j], labels, bins);
            double warmBias = maxPrior > 1e-10 && j < prior.Length ? prior[j] / maxPrior : 0.0;
            relevance[j] = mi + 0.15 * warmBias + 0.05 * variances[j];
        }

        var selected = new List<int>(topN);
        var selectedSet = new HashSet<int>(topN);
        while (selected.Count < topN)
        {
            int bestIdx = -1;
            double bestScore = double.NegativeInfinity;
            for (int j = 0; j < F; j++)
            {
                if (selectedSet.Contains(j))
                    continue;

                double redundancy = 0.0;
                if (selected.Count > 0)
                {
                    for (int si = 0; si < selected.Count; si++)
                        redundancy += ComputeMI(featureColumns[j], featureColumns[selected[si]], bins);
                    redundancy /= selected.Count;
                }

                double score = relevance[j] - 0.35 * redundancy;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = j;
                }
            }

            if (bestIdx < 0)
                break;
            selected.Add(bestIdx);
            selectedSet.Add(bestIdx);
        }

        return selected.OrderBy(i => i).ToArray();
    }

    private static TabNetFeatureExpansionPlan BuildFeatureExpansionPlan(
        List<TrainingSample> samples, int F, ModelSnapshot? warmStart, int topN, int maxTerms)
    {
        int[] topIdx = SelectPolyTopFeatureIndices(samples, F, warmStart, topN);
        if (topIdx.Length < 2 || samples.Count == 0)
            return TabNetFeatureExpansionPlan.Empty;

        int n = Math.Min(samples.Count, 600);
        int bins = Math.Max(4, (int)Math.Ceiling(1 + Math.Log2(n)));
        var labels = new double[n];
        var featureColumns = new double[F][];
        for (int j = 0; j < F; j++)
            featureColumns[j] = new double[n];

        for (int i = 0; i < n; i++)
        {
            labels[i] = samples[i].Direction > 0 ? 1.0 : 0.0;
            for (int j = 0; j < F; j++)
                featureColumns[j][i] = samples[i].Features[j];
        }

        double[] prior = warmStart?.FeatureImportanceScores is { Length: > 0 } warmPrior && warmPrior.Length >= F
            ? warmPrior
            : new double[F];
        double maxPrior = prior.Length > 0 ? prior.Take(F).DefaultIfEmpty(0.0).Max() : 0.0;

        var candidates = new List<(int[] Term, double[] Values, double Score)>();
        void AddCandidate(int[] term)
        {
            var values = new double[n];
            for (int i = 0; i < n; i++)
            {
                double product = 1.0;
                for (int t = 0; t < term.Length; t++)
                    product *= featureColumns[term[t]][i];
                values[i] = product;
            }

            double mi = ComputeMI(values, labels, bins);
            double mean = 0.0;
            for (int i = 0; i < n; i++) mean += values[i];
            mean /= n;
            double variance = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = values[i] - mean;
                variance += d * d;
            }
            variance /= Math.Max(1, n);

            double warmBias = 0.0;
            for (int i = 0; i < term.Length; i++)
                warmBias += maxPrior > 1e-10 && term[i] < prior.Length ? prior[term[i]] / maxPrior : 0.0;
            warmBias /= term.Length;

            double degreePenalty = term.Length > 2 ? 0.02 * (term.Length - 2) : 0.0;
            double duplicatePenalty = term.Distinct().Count() < term.Length ? 0.01 : 0.0;
            double score = mi + 0.05 * variance + 0.08 * warmBias - degreePenalty - duplicatePenalty;
            if (score > 1e-6)
                candidates.Add((term, values, score));
        }

        for (int a = 0; a < topIdx.Length; a++)
        {
            for (int b = a + 1; b < topIdx.Length; b++)
                AddCandidate([topIdx[a], topIdx[b]]);
        }

        for (int a = 0; a < topIdx.Length; a++)
            AddCandidate([topIdx[a], topIdx[a]]);

        int tripleLimit = Math.Min(topIdx.Length, 4);
        for (int a = 0; a < tripleLimit; a++)
        {
            for (int b = a + 1; b < tripleLimit; b++)
            {
                for (int c = b + 1; c < tripleLimit; c++)
                    AddCandidate([topIdx[a], topIdx[b], topIdx[c]]);
            }
        }

        if (candidates.Count == 0)
            return TabNetFeatureExpansionPlan.Empty;

        maxTerms = Math.Max(1, Math.Min(maxTerms, candidates.Count));
        var selectedTerms = new List<int[]>(maxTerms);
        var selectedValues = new List<double[]>(maxTerms);

        while (selectedTerms.Count < maxTerms)
        {
            int bestIdx = -1;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                double score = candidates[i].Score;
                if (selectedValues.Count > 0)
                {
                    double redundancy = 0.0;
                    for (int j = 0; j < selectedValues.Count; j++)
                        redundancy += ComputeMI(candidates[i].Values, selectedValues[j], bins);
                    redundancy /= selectedValues.Count;
                    score -= 0.25 * redundancy;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
                break;

            selectedTerms.Add(candidates[bestIdx].Term);
            selectedValues.Add(candidates[bestIdx].Values);
            candidates.RemoveAt(bestIdx);
        }

        if (selectedTerms.Count == 0)
            return TabNetFeatureExpansionPlan.Empty;

        int[][] orderedTerms = selectedTerms
            .OrderBy(term => term.Length)
            .ThenBy(term => term.Distinct().Count() < term.Length ? 1 : 0)
            .ThenBy(term => string.Join(",", term))
            .Select(term => (int[])term.Clone())
            .ToArray();

        return new TabNetFeatureExpansionPlan(topIdx, orderedTerms);
    }

    private static List<TrainingSample> AugmentSamplesWithPoly(
        List<TrainingSample> samples, int origF, TabNetFeatureExpansionPlan plan)
    {
        if (!plan.IsEnabled)
            return samples;

        int newF = origF + plan.AddedFeatureCount;
        var augmented = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var nf = new float[newF];
            for (int j = 0; j < origF; j++) nf[j] = s.Features[j];
            int k = origF;
            for (int t = 0; t < plan.ProductTerms.Length && k < nf.Length; t++)
            {
                float product = 1f;
                var term = plan.ProductTerms[t];
                for (int i = 0; i < term.Length; i++)
                    product *= s.Features[term[i]];
                nf[k++] = product;
            }
            augmented.Add(s with { Features = nf });
        }
        return augmented;
    }

    private static string[] BuildTabNetFeatureNames(int rawFeatureCount, TabNetFeatureExpansionPlan plan)
    {
        var baseNames = new string[rawFeatureCount];
        for (int i = 0; i < rawFeatureCount; i++)
            baseNames[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";

        if (!plan.IsEnabled)
            return baseNames;

        var names = new string[rawFeatureCount + plan.AddedFeatureCount];
        Array.Copy(baseNames, names, rawFeatureCount);

        int k = rawFeatureCount;
        for (int t = 0; t < plan.ProductTerms.Length && k < names.Length; t++)
        {
            var term = plan.ProductTerms[t];
            names[k++] = string.Join("_x_", term.Select(idx =>
                idx < baseNames.Length ? baseNames[idx] : $"F{idx}"));
        }

        return names;
    }

    private static (float[] Means, float[] Stds) BuildTabNetSnapshotStats(
        float[] rawMeans, float[] rawStds, int rawFeatureCount, TabNetFeatureExpansionPlan plan)
    {
        if (!plan.IsEnabled)
            return (rawMeans, rawStds);

        var means = new float[rawFeatureCount + plan.AddedFeatureCount];
        var stds = new float[rawFeatureCount + plan.AddedFeatureCount];

        Array.Copy(rawMeans, means, Math.Min(rawFeatureCount, rawMeans.Length));
        Array.Copy(rawStds, stds, Math.Min(rawFeatureCount, rawStds.Length));
        for (int i = rawFeatureCount; i < stds.Length; i++)
            stds[i] = 1f;

        return (means, stds);
    }

    private bool ShouldAcceptFeatureExpansion(
        List<TrainingSample> baseSamples,
        List<TrainingSample> expandedSamples,
        TrainingHyperparams hp,
        int baseFeatureCount,
        int expandedFeatureCount,
        int nSteps,
        int hiddenDim,
        int attentionDim,
        int sharedLayers,
        int stepLayers,
        double gamma,
        bool useSparsemax,
        bool useGlu,
        double lr,
        double sparsityCoeff,
        double bnMomentum,
        TabNetRunContext runContext,
        CancellationToken ct)
    {
        if (expandedFeatureCount <= baseFeatureCount ||
            baseSamples.Count < Math.Max(80, hp.MinSamples) ||
            hp.WalkForwardFolds <= 0)
        {
            return true;
        }

        var evalHp = hp with
        {
            WalkForwardFolds = Math.Clamp(Math.Min(hp.WalkForwardFolds, 2), 1, 2),
            MaxEpochs = Math.Max(4, Math.Min(8, hp.MaxEpochs > 0 ? hp.MaxEpochs / 2 : 6)),
            EarlyStoppingPatience = Math.Max(2, hp.EarlyStoppingPatience / 2),
            MaxBadFoldFraction = 1.0,
            MaxFoldDrawdown = 1.0,
            MinFoldCurveSharpe = -99.0,
            MinSharpeTrendSlope = -99.0,
        };

        int evalEpochs = Math.Max(4, Math.Min(evalHp.MaxEpochs, 8));
        int evalHidden = Math.Max(8, Math.Min(hiddenDim, 16));
        int evalAttention = Math.Max(1, Math.Min(attentionDim, evalHidden));
        int evalSteps = Math.Max(2, Math.Min(nSteps, 3));

        var (baseCv, _) = RunWalkForwardCV(
            baseSamples, evalHp, baseFeatureCount, evalSteps, evalHidden, evalAttention,
            sharedLayers, stepLayers, gamma, useSparsemax, useGlu, lr, sparsityCoeff, evalEpochs, bnMomentum, runContext, ct);
        var (expandedCv, _) = RunWalkForwardCV(
            expandedSamples, evalHp, expandedFeatureCount, evalSteps, evalHidden, evalAttention,
            sharedLayers, stepLayers, gamma, useSparsemax, useGlu, lr, sparsityCoeff, evalEpochs, bnMomentum, runContext, ct);

        if (baseCv.FoldCount == 0 || expandedCv.FoldCount == 0)
            return true;

        static double Score(WalkForwardResult result) =>
            result.AvgAccuracy +
            0.10 * result.AvgF1 +
            0.10 * Math.Tanh(result.AvgEV) +
            0.05 * Math.Tanh(result.AvgSharpe / 2.0) -
            0.05 * result.StdAccuracy;

        double baseScore = Score(baseCv);
        double expandedScore = Score(expandedCv);
        bool accept = expandedScore >= baseScore - 0.003;

        _logger.LogInformation(
            "TabNet feature expansion gate: accepted={Accepted} baseScore={BaseScore:F4} expandedScore={ExpandedScore:F4} F {BaseF}->{ExpandedF}",
            accept, baseScore, expandedScore, baseFeatureCount, expandedFeatureCount);

        return accept;
    }

    private static double[] ComputeFeatureVariances(IReadOnlyList<TrainingSample> samples, int featureCount)
    {
        if (samples.Count == 0 || featureCount <= 0)
            return [];

        var means = new double[featureCount];
        var m2 = new double[featureCount];
        int n = 0;

        foreach (var sample in samples)
        {
            n++;
            for (int j = 0; j < featureCount && j < sample.Features.Length; j++)
            {
                double x = sample.Features[j];
                double delta = x - means[j];
                means[j] += delta / n;
                double delta2 = x - means[j];
                m2[j] += delta * delta2;
            }
        }

        var variances = new double[featureCount];
        for (int j = 0; j < featureCount; j++)
            variances[j] = n > 1 ? Math.Max(0.0, m2[j] / (n - 1)) : 0.0;
        return variances;
    }

    internal static double EstimateWarmStartReuseRatio(
        ModelSnapshot snapshot, int featureCount, int nSteps, int hiddenDim, int sharedLayers, int stepLayers)
    {
        snapshot = TabNetSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        if (!TabNetSnapshotSupport.IsTabNet(snapshot))
            return 0.0;

        int attempted = 0;
        int reusable = 0;

        void AddMatrix(double[][]? src, int rows, int cols)
        {
            attempted += rows * cols;
            if (src is null)
                return;

            int activeRows = Math.Min(rows, src.Length);
            for (int i = 0; i < activeRows; i++)
                reusable += Math.Min(cols, src[i].Length);
        }

        void AddVector(double[]? src, int len)
        {
            attempted += len;
            if (src is not null)
                reusable += Math.Min(len, src.Length);
        }

        AddMatrix(snapshot.TabNetInitialBnFcW, featureCount, featureCount);
        AddVector(snapshot.TabNetInitialBnFcB, featureCount);

        for (int l = 0; l < sharedLayers; l++)
        {
            int inDim = l == 0 ? featureCount : hiddenDim;
            AddMatrix(l < (snapshot.TabNetSharedWeights?.Length ?? 0) ? snapshot.TabNetSharedWeights![l] : null, hiddenDim, inDim);
            AddVector(l < (snapshot.TabNetSharedBiases?.Length ?? 0) ? snapshot.TabNetSharedBiases![l] : null, hiddenDim);
            AddMatrix(l < (snapshot.TabNetSharedGateWeights?.Length ?? 0) ? snapshot.TabNetSharedGateWeights![l] : null, hiddenDim, inDim);
            AddVector(l < (snapshot.TabNetSharedGateBiases?.Length ?? 0) ? snapshot.TabNetSharedGateBiases![l] : null, hiddenDim);
        }

        for (int s = 0; s < nSteps; s++)
        {
            for (int l = 0; l < stepLayers; l++)
            {
                AddMatrix(
                    s < (snapshot.TabNetStepFcWeights?.Length ?? 0) && l < snapshot.TabNetStepFcWeights![s].Length
                        ? snapshot.TabNetStepFcWeights[s][l]
                        : null,
                    hiddenDim, hiddenDim);
                AddVector(
                    s < (snapshot.TabNetStepFcBiases?.Length ?? 0) && l < snapshot.TabNetStepFcBiases![s].Length
                        ? snapshot.TabNetStepFcBiases[s][l]
                        : null,
                    hiddenDim);
                AddMatrix(
                    s < (snapshot.TabNetStepGateWeights?.Length ?? 0) && l < snapshot.TabNetStepGateWeights![s].Length
                        ? snapshot.TabNetStepGateWeights[s][l]
                        : null,
                    hiddenDim, hiddenDim);
                AddVector(
                    s < (snapshot.TabNetStepGateBiases?.Length ?? 0) && l < snapshot.TabNetStepGateBiases![s].Length
                        ? snapshot.TabNetStepGateBiases[s][l]
                        : null,
                    hiddenDim);
            }

            int attDim = s < (snapshot.TabNetAttentionFcWeights?.Length ?? 0) &&
                         snapshot.TabNetAttentionFcWeights![s] is { Length: > 0 } attWeights &&
                         attWeights[0] is { Length: > 0 }
                ? attWeights[0].Length
                : hiddenDim;
            AddMatrix(
                s < (snapshot.TabNetAttentionFcWeights?.Length ?? 0) ? snapshot.TabNetAttentionFcWeights![s] : null,
                featureCount, attDim);
            AddVector(
                s < (snapshot.TabNetAttentionFcBiases?.Length ?? 0) ? snapshot.TabNetAttentionFcBiases![s] : null,
                featureCount);
        }

        return attempted > 0 ? (double)reusable / attempted : 0.0;
    }

    internal static double? ComputeRawProbabilityFromSnapshotForAudit(float[] features, ModelSnapshot snapshot)
    {
        return TryBuildWeightsFromSnapshot(snapshot, out var weights)
            ? TabNetRawProb(features, weights)
            : null;
    }

    private static bool TryBuildWeightsFromSnapshot(ModelSnapshot snapshot, out TabNetWeights weights)
    {
        weights = new TabNetWeights();
        snapshot = TabNetSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = TabNetSnapshotSupport.ValidateNormalizedSnapshot(snapshot, allowLegacyV2: false);
        if (!validation.IsValid)
            return false;

        int featureCount = snapshot.Features.Length > 0 ? snapshot.Features.Length : snapshot.Means.Length;
        int nSteps = snapshot.BaseLearnersK > 0 ? snapshot.BaseLearnersK : 3;
        int hiddenDim = snapshot.TabNetHiddenDim > 0 ? snapshot.TabNetHiddenDim : 24;
        int sharedLayers = snapshot.TabNetSharedWeights?.Length ?? 0;
        int stepLayers = snapshot.TabNetStepFcWeights is { Length: > 0 } stepW ? stepW[0].Length : 0;
        int attentionDim = snapshot.TabNetAttentionFcWeights is { Length: > 0 } attW && attW[0] is { Length: > 0 }
            ? attW[0][0].Length
            : hiddenDim;

        weights = new TabNetWeights
        {
            NSteps = nSteps,
            F = featureCount,
            HiddenDim = hiddenDim,
            AttentionDim = attentionDim,
            SharedLayers = sharedLayers,
            StepLayers = stepLayers,
            Gamma = snapshot.TabNetRelaxationGamma > 0 ? snapshot.TabNetRelaxationGamma : 1.5,
            UseSparsemax = snapshot.TabNetUseSparsemax,
            UseGlu = snapshot.TabNetUseGlu,
            InitialBnFcW = snapshot.TabNetInitialBnFcW ?? [],
            InitialBnFcB = snapshot.TabNetInitialBnFcB ?? [],
            SharedW = snapshot.TabNetSharedWeights ?? [],
            SharedB = snapshot.TabNetSharedBiases ?? [],
            SharedGW = snapshot.TabNetSharedGateWeights ?? [],
            SharedGB = snapshot.TabNetSharedGateBiases ?? [],
            StepW = snapshot.TabNetStepFcWeights ?? [],
            StepB = snapshot.TabNetStepFcBiases ?? [],
            StepGW = snapshot.TabNetStepGateWeights ?? [],
            StepGB = snapshot.TabNetStepGateBiases ?? [],
            AttnFcW = snapshot.TabNetAttentionFcWeights ?? [],
            AttnFcB = snapshot.TabNetAttentionFcBiases ?? [],
            BnGamma = snapshot.TabNetBnGammas ?? [],
            BnBeta = snapshot.TabNetBnBetas ?? [],
            BnMean = snapshot.TabNetBnRunningMeans ?? [],
            BnVar = snapshot.TabNetBnRunningVars ?? [],
            OutputW = snapshot.TabNetOutputHeadWeights ?? [],
            OutputB = snapshot.TabNetOutputHeadBias,
            MagW = snapshot.MagWeights ?? [],
            MagB = snapshot.MagBias,
            TotalBnLayers = (snapshot.TabNetBnGammas?.Length ?? 0),
        };

        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MATH UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static double Sigmoid(double x)
        => 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -50, 50)));

    private static double Logit(double p)
    {
        p = Math.Clamp(p, ProbClampMin, 1.0 - ProbClampMin);
        return Math.Log(p / (1.0 - p));
    }

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
        if (a.Length == 0 || b.Length == 0) return 0.0;
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
        if (vals.Length == 0) return 0.0;
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
