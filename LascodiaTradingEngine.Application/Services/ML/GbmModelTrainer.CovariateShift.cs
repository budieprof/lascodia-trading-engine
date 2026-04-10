using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;
using TorchSharp;
using static TorchSharp.torch;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  DENSITY-RATIO (Item 27: MLP), COVARIATE SHIFT (Item 29: continuous)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 27: Density-ratio with 2-layer MLP discriminator.</summary>
    private static double[] ComputeDensityRatioImportanceWeights(
        List<TrainingSample> trainSet, int featureCount, int windowDays, int barsPerDay, int baseSeed = 0)
    {
        int effectiveBarsPerDay = barsPerDay > 0 ? barsPerDay : 24;
        int recentCount = Math.Min(trainSet.Count / 3, windowDays * effectiveBarsPerDay);
        if (recentCount < 20) return Enumerable.Repeat(1.0, trainSet.Count).ToArray();
        int cutoff = trainSet.Count - recentCount;

        // MLP: featureCount → 8 → 1
        int hiddenDim = Math.Min(8, featureCount);
        var wH = new double[featureCount * hiddenDim]; var bH = new double[hiddenDim];
        var wO = new double[hiddenDim]; double bO = 0;
        var rng = CreateSeededRandom(baseSeed, 42);
        for (int i = 0; i < wH.Length; i++) wH[i] = (rng.NextDouble() - 0.5) * 0.1;
        for (int i = 0; i < wO.Length; i++) wO[i] = (rng.NextDouble() - 0.5) * 0.1;
        var hidden = new double[hiddenDim];

        const double sgdLr = 0.005;
        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < trainSet.Count; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0;
                // Forward
                for (int h = 0; h < hiddenDim; h++)
                {
                    double z = bH[h];
                    for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                        z += wH[j * hiddenDim + h] * trainSet[i].Features[j];
                    hidden[h] = Math.Max(0, z);
                }
                double output = bO;
                for (int h = 0; h < hiddenDim; h++) output += wO[h] * hidden[h];
                double p = Sigmoid(output);
                double err = p - label;
                // Backward
                bO -= sgdLr * err;
                for (int h = 0; h < hiddenDim; h++)
                {
                    wO[h] -= sgdLr * err * hidden[h];
                    if (hidden[h] <= 0) continue;
                    double dh = err * wO[h];
                    bH[h] -= sgdLr * dh;
                    for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                        wH[j * hiddenDim + h] -= sgdLr * dh * trainSet[i].Features[j];
                }
            }
        }

        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            for (int h = 0; h < hiddenDim; h++)
            {
                double z = bH[h];
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    z += wH[j * hiddenDim + h] * trainSet[i].Features[j];
                hidden[h] = Math.Max(0, z);
            }
            double output = bO;
            for (int h = 0; h < hiddenDim; h++) output += wO[h] * hidden[h];
            double prob = Math.Clamp(Sigmoid(output), 0.01, 0.99);
            weights[i] = prob / (1 - prob);
        }

        double sum = weights.Sum();
        if (sum > 1e-15) for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
        return weights;
    }

    /// <summary>Item 29: Continuous novelty scoring for covariate shift weights.</summary>
    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBp, int featureCount)
    {
        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double totalNovelty = 0; int checkedCount = 0;
            for (int j = 0; j < featureCount && j < parentBp.Length; j++)
            {
                double[] bp = parentBp[j];
                if (bp.Length < 2) continue;
                checkedCount++;
                double v = trainSet[i].Features[j];
                // Continuous: distance from nearest boundary, normalised by range
                double range = bp[^1] - bp[0];
                if (range < 1e-15) continue;
                double distBelow = v < bp[0] ? (bp[0] - v) / range : 0;
                double distAbove = v > bp[^1] ? (v - bp[^1]) / range : 0;
                totalNovelty += distBelow + distAbove;
            }
            weights[i] = 1.0 + (checkedCount > 0 ? totalNovelty / checkedCount : 0);
        }
        double mean = weights.Average();
        if (mean > 1e-15) for (int i = 0; i < weights.Length; i++) weights[i] /= mean;
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  NON-STATIONARITY COUNTING
    // ═══════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        if (samples.Count < 50) return 0;
        int nonStationary = 0;
        int maxObs = Math.Min(samples.Count, 500);
        for (int j = 0; j < featureCount; j++)
        {
            var series = new double[maxObs];
            for (int i = 0; i < maxObs; i++) series[i] = samples[i].Features[j];
            if (IsNonStationary(series)) nonStationary++;
        }
        return nonStationary;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CONCEPT DRIFT GATE (Item 28)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 28: Sliding-window loss comparison — exclude stale early segments.</summary>
    private List<TrainingSample> ApplyConceptDriftGate(List<TrainingSample> samples, int featureCount, int minSamples)
    {
        int windowSize = Math.Max(minSamples, samples.Count / 5);
        if (samples.Count < windowSize * 2) return samples;

        // Compare loss of earliest window vs latest window using simple accuracy proxy
        var earlyWindow = samples[..windowSize];
        var lateWindow = samples[^windowSize..];

        int earlyBuyCount = earlyWindow.Count(s => s.Direction > 0);
        int lateBuyCount = lateWindow.Count(s => s.Direction > 0);

        double earlyBuyRate = (double)earlyBuyCount / windowSize;
        double lateBuyRate = (double)lateBuyCount / windowSize;

        // If distribution has shifted significantly, trim early data
        if (Math.Abs(earlyBuyRate - lateBuyRate) > 0.15)
        {
            int trimTo = Math.Max(minSamples, samples.Count / 2);
            _logger.LogInformation("GBM concept drift gate: trimming {Old}→{New} samples (buyRate drift {Early:P1}→{Late:P1})",
                samples.Count, trimTo, earlyBuyRate, lateBuyRate);
            return samples[^trimTo..];
        }
        return samples;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EFB: EXCLUSIVE FEATURE BUNDLING (Item 3)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 3: Build EFB mapping — bundles mutually exclusive features.</summary>
    private static (int[] Mapping, int EffectiveCount) BuildEfbMapping(
        List<TrainingSample> samples, int featureCount)
    {
        int n = Math.Min(samples.Count, 500);
        // Count non-zero overlap between features
        var conflictCount = new int[featureCount * featureCount];
        for (int i = 0; i < n; i++)
        {
            for (int a = 0; a < featureCount; a++)
            {
                if (Math.Abs(samples[i].Features[a]) < 1e-10) continue;
                for (int b2 = a + 1; b2 < featureCount; b2++)
                {
                    if (Math.Abs(samples[i].Features[b2]) < 1e-10) continue;
                    conflictCount[a * featureCount + b2]++;
                }
            }
        }

        // Greedy bundling: features with < 1% mutual non-zero rate can be bundled
        var mapping = new int[featureCount];
        for (int i = 0; i < featureCount; i++) mapping[i] = i; // default: map to self

        int maxConflicts = (int)(n * 0.01);
        var bundled = new bool[featureCount];
        int nextBundle = 0;
        var bundles = new List<List<int>>();

        for (int a = 0; a < featureCount; a++)
        {
            if (bundled[a]) continue;
            var bundle = new List<int> { a };
            for (int b2 = a + 1; b2 < featureCount; b2++)
            {
                if (bundled[b2]) continue;
                bool canBundle = true;
                foreach (int existing in bundle)
                {
                    int key = existing < b2 ? existing * featureCount + b2 : b2 * featureCount + existing;
                    if (conflictCount[key] > maxConflicts) { canBundle = false; break; }
                }
                if (canBundle) { bundle.Add(b2); bundled[b2] = true; }
            }
            foreach (int f in bundle) mapping[f] = nextBundle;
            bundled[a] = true;
            bundles.Add(bundle);
            nextBundle++;
        }

        return (mapping, nextBundle);
    }

    private static int[][] BuildEfbGroups(int[] mapping, int featureCount)
    {
        return mapping
            .Take(featureCount)
            .Select((bundle, featureIndex) => (bundle, featureIndex))
            .GroupBy(x => x.bundle)
            .Select(g => g.Select(x => x.featureIndex).OrderBy(i => i).ToArray())
            .Where(group => group.Length > 1)
            .ToArray();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MULTI-SIGNAL STATIONARITY DIAGNOSTICS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Multi-signal stationarity diagnostics per feature (5 tests).
    /// Feature flagged when ≥3/5 trigger. PASS (&lt;15%), WARN (15-50%), REJECT (≥50%).
    /// </summary>
    private static GbmDriftArtifact ComputeGbmDriftDiagnostics(
        List<TrainingSample> trainSet, int F, string[] featureNames, double fracDiffD)
    {
        if (trainSet.Count < 30) return new GbmDriftArtifact();

        int n = trainSet.Count;
        int half = n / 2;
        int flagged = 0;
        var flaggedNames = new List<string>();
        double sumAcf = 0, sumPsi = 0, sumCp = 0, sumAdf = 0, sumKpss = 0;

        for (int fi = 0; fi < F; fi++)
        {
            int signals = 0;

            // 1. Lag-1 autocorrelation
            double sX = 0, sY = 0, sXY = 0, sX2 = 0, sY2 = 0;
            int nc = n - 1;
            for (int i = 0; i < nc; i++)
            {
                double x = trainSet[i].Features[fi], y = trainSet[i + 1].Features[fi];
                sX += x; sY += y; sXY += x * y; sX2 += x * x; sY2 += y * y;
            }
            double varXc = sX2 - sX * sX / nc, varYc = sY2 - sY * sY / nc;
            double denomC = Math.Sqrt(Math.Max(0, varXc * varYc));
            double rho = denomC > 1e-12 ? (sXY - sX * sY / nc) / denomC : 0;
            if (Math.Abs(rho) > 0.97) signals++;
            sumAcf += Math.Abs(rho);

            // 2. PSI between first/second half
            const int PsiBins = 5;
            var bins1 = new int[PsiBins]; var bins2 = new int[PsiBins];
            double fMin = double.MaxValue, fMax = double.MinValue;
            for (int i = 0; i < n; i++) { double v = trainSet[i].Features[fi]; if (v < fMin) fMin = v; if (v > fMax) fMax = v; }
            double binW = (fMax - fMin + 1e-12) / PsiBins;
            for (int i = 0; i < half; i++) bins1[Math.Clamp((int)((trainSet[i].Features[fi] - fMin) / binW), 0, PsiBins - 1)]++;
            for (int i = half; i < n; i++) bins2[Math.Clamp((int)((trainSet[i].Features[fi] - fMin) / binW), 0, PsiBins - 1)]++;
            double psi = 0;
            for (int b = 0; b < PsiBins; b++)
            {
                double p1 = (bins1[b] + 0.5) / (half + PsiBins * 0.5);
                double p2 = (bins2[b] + 0.5) / ((n - half) + PsiBins * 0.5);
                psi += (p2 - p1) * Math.Log(p2 / p1);
            }
            if (psi > 0.25) signals++;
            sumPsi += psi;

            // 3. CUSUM
            double mean = 0;
            for (int i = 0; i < n; i++) mean += trainSet[i].Features[fi];
            mean /= n;
            double cumSum = 0, maxCusum = 0;
            for (int i = 0; i < n; i++) { cumSum += trainSet[i].Features[fi] - mean; double a = Math.Abs(cumSum); if (a > maxCusum) maxCusum = a; }
            double cpNorm = maxCusum / Math.Sqrt(n);
            if (cpNorm > 1.36) signals++;
            sumCp += cpNorm;

            // 4. ADF-like
            double syy = 0, sxy = 0, sy = 0, sx = 0, sx2 = 0;
            int adfN = n - 1;
            for (int i = 0; i < adfN; i++)
            {
                double yi = trainSet[i + 1].Features[fi] - trainSet[i].Features[fi];
                double xi = trainSet[i].Features[fi];
                sy += yi; sx += xi; sxy += xi * yi; sx2 += xi * xi;
            }
            double adfDenom = adfN * sx2 - sx * sx;
            double beta = Math.Abs(adfDenom) > 1e-12 ? (adfN * sxy - sx * sy) / adfDenom : 0;
            double alpha = (sy - beta * sx) / adfN;
            double sse = 0;
            for (int i = 0; i < adfN; i++) { double e = (trainSet[i + 1].Features[fi] - trainSet[i].Features[fi]) - (alpha + beta * trainSet[i].Features[fi]); sse += e * e; }
            double seBeta = Math.Abs(adfDenom) > 1e-12 && adfN > 2 ? Math.Sqrt(sse / (adfN - 2) * adfN / adfDenom) : double.MaxValue;
            double tStat = seBeta < 1e-12 ? 0 : Math.Abs(beta / seBeta);
            if (beta >= 0 || tStat < 2.86) signals++;
            sumAdf += tStat;

            // 5. KPSS-like
            double partialSum = 0, kpssSum = 0, variance = 0;
            for (int i = 0; i < n; i++) { double d = trainSet[i].Features[fi] - mean; variance += d * d; }
            variance /= n;
            if (variance > 1e-15)
            {
                for (int i = 0; i < n; i++) { partialSum += trainSet[i].Features[fi] - mean; kpssSum += (partialSum / Math.Sqrt(n * variance)) * (partialSum / Math.Sqrt(n * variance)); }
                kpssSum /= n;
            }
            if (kpssSum > 0.463) signals++;
            sumKpss += kpssSum;

            if (signals >= 3) { flagged++; if (flaggedNames.Count < 10) flaggedNames.Add(fi < featureNames.Length ? featureNames[fi] : $"F{fi}"); }
        }

        double fraction = F > 0 ? (double)flagged / F : 0;
        return new GbmDriftArtifact
        {
            NonStationaryFeatureCount = flagged, TotalFeatureCount = F, NonStationaryFraction = fraction,
            GateTriggered = fraction >= 0.15, GateAction = fraction >= 0.50 ? "REJECT" : fraction >= 0.15 ? "WARN" : "PASS",
            FlaggedFeatures = [.. flaggedNames],
            MeanLag1Autocorrelation = F > 0 ? sumAcf / F : 0, MeanPopulationStabilityIndex = F > 0 ? sumPsi / F : 0,
            MeanChangePointScore = F > 0 ? sumCp / F : 0, MeanAdfLikeStatistic = F > 0 ? sumAdf / F : 0,
            MeanKpssLikeStatistic = F > 0 ? sumKpss / F : 0, FracDiffDApplied = fracDiffD,
        };
    }

    // ── Adversarial validation (CPU fallback) ─────────────────────────────────

    private static double ComputeAdversarialAuc(List<TrainingSample> trainSet, List<TrainingSample> testSet, int F)
    {
        int n1 = testSet.Count; int n0 = Math.Min(trainSet.Count, n1 * 5); int n = n0 + n1;
        if (n < 20) return 0.5;
        var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;
        var w = new double[F]; double b = 0;
        for (int epoch = 0; epoch < 60; epoch++)
        {
            double dB = 0; var dW = new double[F];
            for (int i = 0; i < n; i++)
            {
                float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
                double label = i < n0 ? 0.0 : 1.0;
                double z = b; for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
                double p = 1.0 / (1.0 + Math.Exp(-z)); double err = p - label;
                dB += err; for (int j = 0; j < F && j < features.Length; j++) dW[j] += err * features[j];
            }
            b -= 0.005 * dB / n; for (int j = 0; j < F; j++) w[j] -= 0.005 * (dW[j] / n + 0.01 * w[j]);
        }
        var scores = new (double Score, int Label)[n];
        for (int i = 0; i < n; i++)
        {
            float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
            double z = b; for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
            scores[i] = (1.0 / (1.0 + Math.Exp(-z)), i < n0 ? 0 : 1);
        }
        Array.Sort(scores, (a, c) => c.Score.CompareTo(a.Score));
        long tp = 0, aucNum = 0;
        foreach (var (_, lbl) in scores) { if (lbl == 1) tp++; else aucNum += tp; }
        return (n1 > 0 && n0 > 0) ? (double)aucNum / ((long)n1 * n0) : 0.5;
    }

    // ── Snapshot sanitization helpers ──────────────────────────────────────────

    private static void SanitizeGbmSnapshotArrays(ModelSnapshot snapshot)
    {
        SanitizeFloatArr(snapshot.Means);
        SanitizeFloatArr(snapshot.Stds);
        SanitizeDoubleArr(snapshot.MagWeights);
        SanitizeDoubleArr(snapshot.IsotonicBreakpoints);
        SanitizeDoubleArr(snapshot.MetaLabelWeights);
        SanitizeDoubleArr(snapshot.AbstentionWeights);
        SanitizeDoubleArr(snapshot.MagQ90Weights);
        SanitizeDoubleArr(snapshot.JackknifeResiduals);
        SanitizeDoubleArr(snapshot.FeatureImportanceScores);
        SanitizeFloatArr(snapshot.GainWeightedImportance);
        SanitizeDoubleArr(snapshot.ReliabilityBinConfidence);
        SanitizeDoubleArr(snapshot.ReliabilityBinAccuracy);
        SanitizeFloatArr(snapshot.FeatureImportance);
        if (snapshot.FeatureQuantileBreakpoints is not null)
            foreach (var bp in snapshot.FeatureQuantileBreakpoints) SanitizeDoubleArr(bp);
    }

    private static void SanitizeDoubleArr(double[]? arr)
    {
        if (arr is null) return;
        for (int i = 0; i < arr.Length; i++) if (!double.IsFinite(arr[i])) arr[i] = 0.0;
    }

    private static void SanitizeFloatArr(float[]? arr)
    {
        if (arr is null) return;
        for (int i = 0; i < arr.Length; i++) if (!float.IsFinite(arr[i])) arr[i] = 0f;
    }

    private static double SafeGbm(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;
}
