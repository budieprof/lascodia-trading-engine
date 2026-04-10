using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class ElmModelTrainer
{
    /// <summary>
    /// Multi-signal stationarity diagnostics per feature:
    /// 1. Lag-1 autocorrelation (|ρ₁| > 0.97)
    /// 2. PSI between first/second half (5-bin, threshold > 0.25)
    /// 3. CUSUM change-point (normalized, threshold > 1.36)
    /// 4. ADF-like OLS (β ≥ 0 || |t| < 2.86)
    /// 5. KPSS-like partial sum (threshold > 0.463)
    /// Feature flagged when ≥3/5 trigger. Gate: PASS (<15%), WARN (15-50%), REJECT (≥50%).
    /// </summary>
    private static ElmDriftArtifact ComputeElmDriftDiagnostics(
        List<TrainingSample> trainSet,
        int                  F,
        string[]             featureNames,
        double               fracDiffD)
    {
        if (trainSet.Count < 30)
            return new ElmDriftArtifact();

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

            // 4. ADF-like: OLS Δy = α + β·y_{t-1}
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
            if (beta >= 0 || tStat < 2.86) signals++; // explosive growth or cannot reject unit root
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

        return new ElmDriftArtifact
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

    private static void SanitizeElmSnapshotArrays(ModelSnapshot snapshot)
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
        if (snapshot.ElmInputWeights is not null)
            foreach (var w in snapshot.ElmInputWeights) SanitizeDoubleArr(w);
        if (snapshot.ElmInputBiases is not null)
            foreach (var w in snapshot.ElmInputBiases) SanitizeDoubleArr(w);
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

    private static double SafeElm(double v, double fallback = 0.0)
        => double.IsFinite(v) ? v : fallback;
}
