using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    /// <summary>
    /// Multi-signal stationarity diagnostics per feature (5 tests).
    /// Feature flagged when ≥3/5 trigger. PASS (&lt;15%), WARN (15-50%), REJECT (≥50%).
    /// </summary>
    private static TcnDriftArtifact ComputeTcnDriftDiagnostics(
        List<TrainingSample> trainSet, int F, string[] featureNames, double fracDiffD)
    {
        if (trainSet.Count < 30) return new TcnDriftArtifact();

        int n = trainSet.Count; int half = n / 2;
        int flagged = 0; var flaggedNames = new List<string>();
        double sumAcf = 0, sumPsi = 0, sumCp = 0, sumAdf = 0, sumKpss = 0;

        for (int fi = 0; fi < F; fi++)
        {
            int signals = 0;

            // 1. Lag-1 autocorrelation
            double sX = 0, sY = 0, sXY = 0, sX2 = 0, sY2 = 0; int nc = n - 1;
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

            // 2. PSI
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
            if (psi > 0.25) signals++; sumPsi += psi;

            // 3. CUSUM
            double mean = 0; for (int i = 0; i < n; i++) mean += trainSet[i].Features[fi]; mean /= n;
            double cumSum = 0, maxCusum = 0;
            for (int i = 0; i < n; i++) { cumSum += trainSet[i].Features[fi] - mean; double a = Math.Abs(cumSum); if (a > maxCusum) maxCusum = a; }
            double cpNorm = maxCusum / Math.Sqrt(n);
            if (cpNorm > 1.36) signals++; sumCp += cpNorm;

            // 4. ADF-like
            double syy = 0, sxy = 0, sy = 0, sx = 0, sx2 = 0; int adfN = n - 1;
            for (int i = 0; i < adfN; i++)
            {
                double yi = trainSet[i + 1].Features[fi] - trainSet[i].Features[fi], xi = trainSet[i].Features[fi];
                sy += yi; sx += xi; sxy += xi * yi; sx2 += xi * xi;
            }
            double adfDenom = adfN * sx2 - sx * sx;
            double beta = Math.Abs(adfDenom) > 1e-12 ? (adfN * sxy - sx * sy) / adfDenom : 0;
            double alpha = (sy - beta * sx) / adfN;
            double sse = 0;
            for (int i = 0; i < adfN; i++) { double e = (trainSet[i + 1].Features[fi] - trainSet[i].Features[fi]) - (alpha + beta * trainSet[i].Features[fi]); sse += e * e; }
            double seBeta = Math.Abs(adfDenom) > 1e-12 && adfN > 2 ? Math.Sqrt(sse / (adfN - 2) * adfN / adfDenom) : double.MaxValue;
            double tStat = seBeta < 1e-12 ? 0 : Math.Abs(beta / seBeta);
            if (beta >= 0 || tStat < 2.86) signals++; sumAdf += tStat;

            // 5. KPSS-like
            double partialSum = 0, kpssSum = 0, variance = 0;
            for (int i = 0; i < n; i++) { double d = trainSet[i].Features[fi] - mean; variance += d * d; } variance /= n;
            if (variance > 1e-15)
            {
                for (int i = 0; i < n; i++) { partialSum += trainSet[i].Features[fi] - mean; kpssSum += (partialSum / Math.Sqrt(n * variance)) * (partialSum / Math.Sqrt(n * variance)); }
                kpssSum /= n;
            }
            if (kpssSum > 0.463) signals++; sumKpss += kpssSum;

            if (signals >= 3) { flagged++; if (flaggedNames.Count < 10) flaggedNames.Add(fi < featureNames.Length ? featureNames[fi] : $"F{fi}"); }
        }

        double fraction = F > 0 ? (double)flagged / F : 0;
        return new TcnDriftArtifact
        {
            NonStationaryFeatureCount = flagged, TotalFeatureCount = F, NonStationaryFraction = fraction,
            GateTriggered = fraction >= 0.15, GateAction = fraction >= 0.50 ? "REJECT" : fraction >= 0.15 ? "WARN" : "PASS",
            FlaggedFeatures = [.. flaggedNames],
            MeanLag1Autocorrelation = F > 0 ? sumAcf / F : 0, MeanPopulationStabilityIndex = F > 0 ? sumPsi / F : 0,
            MeanChangePointScore = F > 0 ? sumCp / F : 0, MeanAdfLikeStatistic = F > 0 ? sumAdf / F : 0,
            MeanKpssLikeStatistic = F > 0 ? sumKpss / F : 0, FracDiffDApplied = fracDiffD,
        };
    }

    // ── Snapshot sanitization ─────────────────────────────────────────────────

    private static void SanitizeTcnSnapshotArrays(ModelSnapshot snapshot)
    {
        SanitizeFloatArr(snapshot.Means);
        SanitizeFloatArr(snapshot.Stds);
        SanitizeFloatArr(snapshot.SeqMeans);
        SanitizeFloatArr(snapshot.SeqStds);
        SanitizeDoubleArr(snapshot.MagWeights);
        SanitizeDoubleArr(snapshot.IsotonicBreakpoints);
        SanitizeDoubleArr(snapshot.MetaLabelWeights);
        SanitizeDoubleArr(snapshot.AbstentionWeights);
        SanitizeDoubleArr(snapshot.MagQ90Weights);
        SanitizeDoubleArr(snapshot.FeatureImportanceScores);
        SanitizeDoubleArr(snapshot.ReliabilityBinConfidence);
        SanitizeDoubleArr(snapshot.ReliabilityBinAccuracy);
        SanitizeDoubleArr(snapshot.ShapleyChannelValues);
        SanitizeDoubleArr(snapshot.ConfidenceHistogramQuantiles);
        SanitizeFloatArr(snapshot.FeatureImportance);
        if (snapshot.FeatureQuantileBreakpoints is not null)
            foreach (var bp in snapshot.FeatureQuantileBreakpoints) SanitizeDoubleArr(bp);
        if (snapshot.VennAbersMultiP is not null)
            foreach (var vp in snapshot.VennAbersMultiP) SanitizeDoubleArr(vp);
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

    private static double SafeTcn(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;

    // ── Calibration residual stats ────────────────────────────────────────────

    private static (double Mean, double Std, double Threshold) ComputeCalibrationResidualStats(
        List<TrainingSample> calSet, double[] rawProbs, TcnCalibrationArtifacts calibration)
    {
        if (calSet.Count < 10 || rawProbs.Length < calSet.Count) return (0.0, 0.0, 1.0);
        var residuals = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = ApplyTcnCalibration(rawProbs[i], calibration);
            residuals[i] = Math.Abs(p - (calSet[i].Direction > 0 ? 1.0 : 0.0));
        }
        double mean = 0; foreach (double r in residuals) mean += r; mean /= residuals.Length;
        double variance = 0; foreach (double r in residuals) { double d = r - mean; variance += d * d; }
        double std = residuals.Length > 1 ? Math.Sqrt(variance / (residuals.Length - 1)) : 0.0;
        return (mean, std, mean + 2.0 * std);
    }

    // ── Feature variances ─────────────────────────────────────────────────────

    private static double[] ComputeFeatureVariances(List<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 2) return new double[F];
        var v = new double[F]; int n = trainSet.Count;
        for (int j = 0; j < F; j++)
        {
            double sum = 0, sumSq = 0;
            for (int i = 0; i < n; i++) { double val = trainSet[i].Features[j]; sum += val; sumSq += val * val; }
            double mean = sum / n; v[j] = sumSq / n - mean * mean;
        }
        return v;
    }

    // ── Cross-fit adaptive heads ──────────────────────────────────────────────

    private (double[] AbstentionWeights, double AbstentionBias, double AbstentionThreshold, bool Used, int FoldCount)
        CrossFitTcnAbstention(
            List<TrainingSample> calSet,
            double[]             calRawProbs,
            TcnCalibrationArtifacts calibration,
            int                  minSamples = 20)
    {
        const int K = 3;
        int n = calSet.Count;
        if (n < minSamples * K) return ([], 0.0, 0.5, false, 0);

        int foldSize = n / K;
        const int absDim = 3;
        var absWeightsAccum = new double[absDim];
        double absBiasAccum = 0.0;
        int validFolds = 0;

        for (int fold = 0; fold < K; fold++)
        {
            int testStart = fold * foldSize;
            int testEnd = fold == K - 1 ? n : (fold + 1) * foldSize;

            var foldCal = new List<TrainingSample>(n - (testEnd - testStart));
            var foldProbs = new List<double>(n - (testEnd - testStart));
            for (int i = 0; i < testStart; i++) { foldCal.Add(calSet[i]); foldProbs.Add(calRawProbs[i]); }
            for (int i = testEnd; i < n; i++) { foldCal.Add(calSet[i]); foldProbs.Add(calRawProbs[i]); }

            if (foldCal.Count < minSamples) continue;

            var (aw, ab, at) = FitTcnAbstentionModel(foldCal, [.. foldProbs], calibration);
            for (int j = 0; j < absDim && j < aw.Length; j++) absWeightsAccum[j] += aw[j];
            absBiasAccum += ab;
            validFolds++;
        }

        if (validFolds == 0) return ([], 0.0, 0.5, false, 0);

        for (int j = 0; j < absDim; j++) absWeightsAccum[j] /= validFolds;
        absBiasAccum /= validFolds;

        return (absWeightsAccum, absBiasAccum, 0.5, true, validFolds);
    }
}
