using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services;

public sealed partial class BaggedLogisticTrainer
{
    /// <summary>
    /// Multi-signal stationarity diagnostics per feature (5 tests):
    /// 1. Lag-1 autocorrelation (|rho_1| > 0.97)
    /// 2. PSI between first/second half (5-bin, threshold > 0.25)
    /// 3. CUSUM change-point (normalized, threshold > 1.36)
    /// 4. ADF-like OLS (beta >= 0 || |t| &lt; 2.86)
    /// 5. KPSS-like partial sum (threshold > 0.463)
    /// Feature flagged when >=3/5 trigger. Gate: PASS (&lt;15%), WARN (15-50%), REJECT (>=50%).
    /// </summary>
    private static BaggedLogisticDriftArtifact ComputeBaggedLogisticDriftDiagnostics(
        List<TrainingSample> trainSet,
        int                  F,
        string[]             featureNames,
        double               fracDiffD)
    {
        if (trainSet.Count < 30)
            return new BaggedLogisticDriftArtifact();

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
                double x = trainSet[i].Features[fi];
                double y = trainSet[i + 1].Features[fi];
                sX += x; sY += y; sXY += x * y; sX2 += x * x; sY2 += y * y;
            }
            double varXc = sX2 - sX * sX / nc;
            double varYc = sY2 - sY * sY / nc;
            double denomC = Math.Sqrt(Math.Max(0, varXc * varYc));
            double rho = denomC > 1e-12 ? (sXY - sX * sY / nc) / denomC : 0;
            if (Math.Abs(rho) > 0.97) signals++;
            sumAcf += Math.Abs(rho);

            // 2. PSI between first/second half
            const int PsiBins = 5;
            var bins1 = new int[PsiBins];
            var bins2 = new int[PsiBins];
            double fMin = double.MaxValue, fMax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[fi];
                if (v < fMin) fMin = v;
                if (v > fMax) fMax = v;
            }
            double binW = (fMax - fMin + 1e-12) / PsiBins;
            for (int i = 0; i < half; i++)
                bins1[Math.Clamp((int)((trainSet[i].Features[fi] - fMin) / binW), 0, PsiBins - 1)]++;
            for (int i = half; i < n; i++)
                bins2[Math.Clamp((int)((trainSet[i].Features[fi] - fMin) / binW), 0, PsiBins - 1)]++;
            double psi = 0;
            for (int b = 0; b < PsiBins; b++)
            {
                double p1 = (bins1[b] + 0.5) / (half + PsiBins * 0.5);
                double p2 = (bins2[b] + 0.5) / ((n - half) + PsiBins * 0.5);
                psi += (p2 - p1) * Math.Log(p2 / p1);
            }
            if (psi > 0.25) signals++;
            sumPsi += psi;

            // 3. CUSUM change-point
            double mean = 0;
            for (int i = 0; i < n; i++) mean += trainSet[i].Features[fi];
            mean /= n;
            double cumSum = 0, maxCusum = 0;
            for (int i = 0; i < n; i++)
            {
                cumSum += trainSet[i].Features[fi] - mean;
                double absCusum = Math.Abs(cumSum);
                if (absCusum > maxCusum) maxCusum = absCusum;
            }
            double cpNorm = n > 0 ? maxCusum / Math.Sqrt(n) : 0;
            if (cpNorm > 1.36) signals++;
            sumCp += cpNorm;

            // 4. ADF-like: OLS delta_y = alpha + beta * y_{t-1}
            double syy = 0, sxy = 0, sy = 0, sx = 0, sx2 = 0;
            int adfN = n - 1;
            for (int i = 0; i < adfN; i++)
            {
                double yi = trainSet[i + 1].Features[fi] - trainSet[i].Features[fi];
                double xi = trainSet[i].Features[fi];
                sy += yi; sx += xi; sxy += xi * yi; sx2 += xi * xi; syy += yi * yi;
            }
            double adfDenom = adfN * sx2 - sx * sx;
            double beta = Math.Abs(adfDenom) > 1e-12 ? (adfN * sxy - sx * sy) / adfDenom : 0;
            double alpha = (sy - beta * sx) / adfN;
            double sse = 0;
            for (int i = 0; i < adfN; i++)
            {
                double yi = trainSet[i + 1].Features[fi] - trainSet[i].Features[fi];
                double pred = alpha + beta * trainSet[i].Features[fi];
                double e = yi - pred;
                sse += e * e;
            }
            double seBeta = Math.Abs(adfDenom) > 1e-12 && adfN > 2
                ? Math.Sqrt(sse / (adfN - 2) * adfN / adfDenom) : double.MaxValue;
            double tStat = seBeta < 1e-12 ? 0 : Math.Abs(beta / seBeta);
            if (beta >= 0 || tStat < 2.86) signals++;
            sumAdf += tStat;

            // 5. KPSS-like partial sum
            double partialSum = 0, kpssSum = 0;
            double variance = 0;
            for (int i = 0; i < n; i++) { double d = trainSet[i].Features[fi] - mean; variance += d * d; }
            variance /= n;
            if (variance > 1e-15)
            {
                for (int i = 0; i < n; i++)
                {
                    partialSum += trainSet[i].Features[fi] - mean;
                    kpssSum += (partialSum / Math.Sqrt(n * variance)) * (partialSum / Math.Sqrt(n * variance));
                }
                kpssSum /= n;
            }
            if (kpssSum > 0.463) signals++;
            sumKpss += kpssSum;

            if (signals >= 3)
            {
                flagged++;
                string name = fi < featureNames.Length ? featureNames[fi] : $"F{fi}";
                if (flaggedNames.Count < 10) flaggedNames.Add(name);
            }
        }

        double fraction = F > 0 ? (double)flagged / F : 0;
        string action = fraction >= 0.50 ? "REJECT" : fraction >= 0.15 ? "WARN" : "PASS";

        return new BaggedLogisticDriftArtifact
        {
            NonStationaryFeatureCount    = flagged,
            TotalFeatureCount            = F,
            NonStationaryFraction        = fraction,
            GateTriggered                = fraction >= 0.15,
            GateAction                   = action,
            FlaggedFeatures              = [.. flaggedNames],
            MeanLag1Autocorrelation      = F > 0 ? sumAcf / F : 0,
            MeanPopulationStabilityIndex = F > 0 ? sumPsi / F : 0,
            MeanChangePointScore         = F > 0 ? sumCp  / F : 0,
            MeanAdfLikeStatistic         = F > 0 ? sumAdf / F : 0,
            MeanKpssLikeStatistic        = F > 0 ? sumKpss / F : 0,
            FracDiffDApplied             = fracDiffD,
        };
    }

    // ── Snapshot sanitization helpers ──────────────────────────────────────

    private static void SanitizeBaggedLogisticSnapshotArrays(ModelSnapshot snapshot)
    {
        SanitizeFloatArr(snapshot.Means);
        SanitizeFloatArr(snapshot.Stds);
        SanitizeDoubleArr(snapshot.MagWeights);
        SanitizeDoubleArr(snapshot.MagAugWeights);
        SanitizeDoubleArr(snapshot.IsotonicBreakpoints);
        SanitizeDoubleArr(snapshot.MetaLabelWeights);
        SanitizeDoubleArr(snapshot.AbstentionWeights);
        SanitizeDoubleArr(snapshot.MagQ90Weights);
        SanitizeDoubleArr(snapshot.JackknifeResiduals);
        SanitizeDoubleArr(snapshot.FeatureImportanceScores);
        SanitizeDoubleArr(snapshot.DriftDetectionFeatureMeans);
        SanitizeDoubleArr(snapshot.DriftDetectionFeatureStds);
        SanitizeDoubleArr(snapshot.FeatureVariances);
        SanitizeDoubleArr(snapshot.ReliabilityBinConfidence);
        SanitizeDoubleArr(snapshot.ReliabilityBinAccuracy);
        SanitizeFloatArr(snapshot.FeatureImportance);
        if (snapshot.Weights is not null)
            foreach (var w in snapshot.Weights) SanitizeDoubleArr(w);
        if (snapshot.FeatureQuantileBreakpoints is not null)
            foreach (var bp in snapshot.FeatureQuantileBreakpoints) SanitizeDoubleArr(bp);
    }

    private static void SanitizeDoubleArr(double[]? arr)
    {
        if (arr is null) return;
        for (int i = 0; i < arr.Length; i++)
            if (!double.IsFinite(arr[i])) arr[i] = 0.0;
    }

    private static void SanitizeFloatArr(float[]? arr)
    {
        if (arr is null) return;
        for (int i = 0; i < arr.Length; i++)
            if (!float.IsFinite(arr[i])) arr[i] = 0f;
    }

    private static double SafeBL(double v, double fallback = 0.0)
        => double.IsFinite(v) ? v : fallback;

    // ── Calibration residual stats ────────────────────────────────────────

    /// <summary>
    /// Computes mean and std of |calibratedProb - trueLabel| over the calibration set.
    /// Threshold = mean + 2*std, used as OOD residual outlier gate.
    /// </summary>
    private static (double Mean, double Std, double Threshold) ComputeCalibrationResidualStats(
        List<TrainingSample>  calSet,
        Func<float[], double> calibProb)
    {
        if (calSet.Count < 10) return (0.0, 0.0, 1.0);

        var residuals = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = calibProb(calSet[i].Features);
            double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals[i] = Math.Abs(p - y);
        }

        double mean = 0.0;
        foreach (double r in residuals) mean += r;
        mean /= residuals.Length;

        double variance = 0.0;
        foreach (double r in residuals) { double d = r - mean; variance += d * d; }
        double std = residuals.Length > 1 ? Math.Sqrt(variance / (residuals.Length - 1)) : 0.0;

        return (mean, std, mean + 2.0 * std);
    }

    // ── Feature variances ─────────────────────────────────────────────────

    private static double[] ComputeFeatureVariances(List<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 2) return new double[F];

        var variances = new double[F];
        int n = trainSet.Count;

        for (int j = 0; j < F; j++)
        {
            double sum = 0.0, sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[j];
                sum   += v;
                sumSq += v * v;
            }
            double mean = sum / n;
            variances[j] = sumSq / n - mean * mean;
        }
        return variances;
    }

    // ── Reliability diagram (10 equal-width bins) ─────────────────────────

    /// <summary>
    /// Bins calibrated probabilities into equal-width bins and computes per-bin
    /// mean confidence vs. mean accuracy (positive-class frequency).
    /// </summary>
    private static (double[] BinConf, double[] BinAcc, int[] BinCounts) ComputeReliabilityDiagram(
        List<TrainingSample>  testSet,
        Func<float[], double> calibProb,
        int                   bins = 10)
    {
        var binConf   = new double[bins];
        var binAcc    = new double[bins];
        var binCounts = new int[bins];

        if (testSet.Count < bins)
            return (binConf, binAcc, binCounts);

        foreach (var s in testSet)
        {
            double p = calibProb(s.Features);
            int b = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[b] += p;
            if (s.Direction > 0) binAcc[b]++;
            binCounts[b]++;
        }

        for (int b = 0; b < bins; b++)
        {
            if (binCounts[b] > 0)
            {
                binConf[b] /= binCounts[b];
                binAcc[b]  /= binCounts[b];
            }
        }
        return (binConf, binAcc, binCounts);
    }

    // ── Murphy decomposition (Brier = calibration + refinement) ───────────

    /// <summary>
    /// Decomposes the Brier score into calibration loss and refinement loss
    /// using binned Murphy decomposition.
    /// </summary>
    private static (double CalibrationLoss, double RefinementLoss) ComputeMurphyDecomposition(
        List<TrainingSample>  testSet,
        Func<float[], double> calibProb,
        int                   bins = 10)
    {
        if (testSet.Count < bins) return (0.0, 0.0);

        var binSumP = new double[bins];
        var binSumY = new double[bins];
        var binCnt  = new int[bins];

        foreach (var s in testSet)
        {
            double p = calibProb(s.Features);
            int b = Math.Clamp((int)(p * bins), 0, bins - 1);
            binSumP[b] += p;
            binSumY[b] += s.Direction > 0 ? 1.0 : 0.0;
            binCnt[b]++;
        }

        int    n   = testSet.Count;
        double cal = 0.0, ref_ = 0.0;
        for (int b = 0; b < bins; b++)
        {
            if (binCnt[b] == 0) continue;
            double meanP = binSumP[b] / binCnt[b];
            double meanY = binSumY[b] / binCnt[b];
            double w     = (double)binCnt[b] / n;
            cal  += w * (meanY - meanP) * (meanY - meanP);
            ref_ += w * meanY * (1.0 - meanY);
        }
        return (cal, ref_);
    }

    // ── Prediction stability score ────────────────────────────────────────

    /// <summary>
    /// Mean |p - 0.5| over the test set. Higher values indicate the model is more
    /// decisive (predictions far from the decision boundary).
    /// </summary>
    private static double ComputePredictionStabilityScore(
        List<TrainingSample>  testSet,
        Func<float[], double> calibProb)
    {
        if (testSet.Count == 0) return 0.0;

        double sum = 0.0;
        foreach (var s in testSet)
        {
            double p = calibProb(s.Features);
            sum += Math.Abs(p - 0.5);
        }
        return sum / testSet.Count;
    }

    // ── Warm-start artifact builder ───────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="BaggedLogisticWarmStartArtifact"/> summarising the warm-start
    /// outcome for this training run.
    /// </summary>
    private static BaggedLogisticWarmStartArtifact BuildBaggedLogisticWarmStartArtifact(
        bool     attempted,
        bool     compatible,
        int      reusedLearnerCount,
        int      totalParentLearners,
        string[] issues)
    {
        return new BaggedLogisticWarmStartArtifact
        {
            Attempted            = attempted,
            Compatible           = compatible,
            ReusedLearnerCount   = reusedLearnerCount,
            TotalParentLearners  = totalParentLearners,
            ReuseRatio           = totalParentLearners > 0
                                       ? (double)reusedLearnerCount / totalParentLearners
                                       : 0.0,
            CompatibilityIssues  = issues,
        };
    }
}
