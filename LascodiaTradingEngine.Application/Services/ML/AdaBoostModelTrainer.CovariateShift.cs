using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    // ── Durbin-Watson autocorrelation test ─────────────────────────────────────

    /// <summary>
    /// Computes the Durbin-Watson statistic on magnitude regressor residuals over the
    /// training set. DW = Σ(e_t − e_{t-1})² / Σe_t².
    /// DW ≈ 2 → no autocorrelation; DW &lt; 1.5 → positive autocorrelation.
    /// Returns 2.0 when the training set is too small to compute reliably.
    /// </summary>
    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        int                  F)
    {
        if (trainSet.Count < 10) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < F && j < magWeights.Length; j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }

        double sumSqDiff = 0.0, sumSqRes = 0.0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double diff = residuals[i] - residuals[i - 1];
            sumSqDiff  += diff * diff;
        }
        foreach (double e in residuals) sumSqRes += e * e;

        return sumSqRes < 1e-15 ? 2.0 : sumSqDiff / sumSqRes;
    }

    // ── Density-ratio covariate reweighting (TorchSharp logistic, Adam + weight_decay) ──

    /// <summary>
    /// Trains a TorchSharp logistic discriminator to distinguish "recent" samples
    /// (label=1) from "historical" samples (label=0).  Returns importance weights
    /// w_i = p_i/(1−p_i) normalised to sum=1, blended into initial boosting weights
    /// to focus boosting on the current distribution.
    /// L2 regularisation is applied via Adam's <c>weight_decay</c> argument, which
    /// is more numerically stable than the per-sample gradient addition used by the
    /// previous hand-rolled SGD.  Batch training also enables vectorised operations.
    /// </summary>
    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet,
        int                  F,
        int                  recentWindowDays,
        int                  barsPerDay,
        CancellationToken    ct = default)
    {
        int n = trainSet.Count;
        if (n < 50) { var u = new double[n]; Array.Fill(u, 1.0 / n); return u; }

        // Treat last min(n/5, recentWindowDays×barsPerDay) samples as "recent"
        int resolvedBarsPerDay = barsPerDay > 0 ? barsPerDay : 24;
        int recentCount = Math.Max(10, Math.Min(n / 5, recentWindowDays * resolvedBarsPerDay));
        recentCount     = Math.Min(recentCount, n - 10);
        int histCount   = n - recentCount;

        var xArr = new float[n * F];
        var yArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            Array.Copy(trainSet[i].Features, 0, xArr, i * F, F);
            yArr[i] = i >= histCount ? 1f : 0f;
        }

        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.01, weight_decay: 0.01);

        // Hoist constant tensors out of the training loop to avoid per-epoch allocation
        using var xTConst = torch.tensor(xArr, device: CPU).reshape(n, F);
        using var yTConst = torch.tensor(yArr, device: CPU).reshape(n, 1);

        for (int epoch = 0; epoch < 40; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            opt.zero_grad();
            using var logit = torch.mm(xTConst, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yTConst);
            loss.backward();
            opt.step();
        }

        // Extract probabilities and compute importance ratios
        float[] scoreArr;
        using (no_grad())
        {
            using var logit = torch.mm(xTConst, wP) + bP;
            using var prob  = torch.sigmoid(logit).squeeze(1);
            scoreArr = prob.cpu().data<float>().ToArray();
        }

        var    weights = new double[n];
        double sum     = 0.0;
        for (int i = 0; i < n; i++)
        {
            double p     = scoreArr[i];
            double ratio = Math.Clamp(p / Math.Max(1.0 - p, 1e-6), 0.01, 10.0);
            weights[i]   = ratio;
            sum          += ratio;
        }
        if (sum > 0) for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    // ── Covariate shift weights ────────────────────────────────────────────────

    /// <summary>
    /// Computes per-sample novelty scores using the parent model's feature quantile
    /// breakpoints. Each sample's weight = 1 + fraction_of_features_outside_[q10,q90].
    /// Normalised to mean = 1.0 so the effective gradient scale is unchanged.
    /// </summary>
    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> samples,
        double[][]           parentQuantileBreakpoints,
        int                  F)
    {
        int n       = samples.Count;
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            float[] feat         = samples[i].Features;
            int     outsideCount = 0;
            int     checkedCount = 0;
            for (int j = 0; j < F; j++)
            {
                if (j >= parentQuantileBreakpoints.Length) continue;
                var bp = parentQuantileBreakpoints[j];
                if (bp.Length < 2) continue;
                double q10 = bp[0];
                double q90 = bp[^1];
                if (feat[j] < q10 || feat[j] > q90) outsideCount++;
                checkedCount++;
            }
            weights[i] = 1.0 + (checkedCount > 0 ? (double)outsideCount / checkedCount : 0.0);
        }

        double mean = 0;
        foreach (var w in weights) mean += w;
        mean /= n;
        if (mean > 1e-10) for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }

    // ── Adversarial validation (TorchSharp logistic, train vs test AUC) ──────

    /// <summary>
    /// Trains a TorchSharp logistic classifier to distinguish training-set samples
    /// (label=0) from test-set samples (label=1).  Returns the ROC-AUC as a measure
    /// of covariate shift: 0.50 = no shift (random), 1.0 = perfect discrimination.
    /// Uses L2 regularisation (weight_decay) to prevent overfitting on small sets.
    /// Train size is capped at 5× test size to keep class balance reasonable.
    /// </summary>
    private static double ComputeAdversarialAuc(
        List<TrainingSample>         trainSet,
        List<TrainingSample>         testSet,
        int                          F,
        ILogger<AdaBoostModelTrainer> logger,
        CancellationToken            ct = default)
    {
        int n1    = testSet.Count;
        int n0    = Math.Min(trainSet.Count, n1 * 5);
        int n     = n0 + n1;
        if (n < 20) return 0.5;

        // Use the most-recent n0 training samples (closest in time to test set)
        var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;

        var xArr = new float[n * F];
        var yArr = new float[n];

        for (int i = 0; i < n0; i++)
        {
            Array.Copy(trainSlice[i].Features, 0, xArr, i * F, F);
            yArr[i] = 0f;
        }
        for (int i = 0; i < n1; i++)
        {
            Array.Copy(testSet[i].Features, 0, xArr, (n0 + i) * F, F);
            yArr[n0 + i] = 1f;
        }

        // TorchSharp logistic regression: weight [F,1], bias [1]
        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.005, weight_decay: 0.01);

        using var xTConst = torch.tensor(xArr, device: CPU).reshape(n, F);
        using var yTConst = torch.tensor(yArr, device: CPU).reshape(n, 1);

        for (int epoch = 0; epoch < 60; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            opt.zero_grad();
            using var logit = torch.mm(xTConst, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yTConst);
            loss.backward();
            opt.step();
        }

        // Extract scores for Wilcoxon AUC
        float[] scoreArr;
        using (no_grad())
        {
            using var logit = torch.mm(xTConst, wP) + bP;
            using var prob  = torch.sigmoid(logit).squeeze(1);
            scoreArr = prob.cpu().data<float>().ToArray();
        }

        // ROC-AUC via Wilcoxon rank statistic: P(score(pos) > score(neg))
        var scores = new (float Score, int Label)[n];
        for (int i = 0; i < n; i++) scores[i] = (scoreArr[i], (int)yArr[i]);
        Array.Sort(scores, (a, b) => b.Score.CompareTo(a.Score)); // descending

        long tp = 0, aucNum = 0;
        foreach (var (_, lbl) in scores)
        {
            if (lbl == 1) tp++;
            else          aucNum += tp;
        }
        long pos = n1;
        long neg = n0;
        return (pos > 0 && neg > 0) ? (double)aucNum / (pos * neg) : 0.5;
    }

    // ── Multi-signal drift diagnostics ────────────────────────────────────────

    /// <summary>
    /// Computes multi-signal stationarity diagnostics per feature:
    /// 1. Lag-1 autocorrelation (|ρ₁| > 0.97)
    /// 2. PSI between first/second half of training window
    /// 3. CUSUM change-point score
    /// 4. ADF-like statistic (OLS Δy = α + β·y_{t-1})
    /// 5. KPSS-like partial sum statistic
    /// A feature is flagged when ≥3 of 5 signals trigger.
    /// Gate action: WARN if &lt;30% flagged, REJECT if ≥50% flagged.
    /// </summary>
    private static AdaBoostDriftArtifact ComputeDriftDiagnostics(
        List<TrainingSample> trainSet,
        int                  F,
        string[]             featureNames,
        double               fracDiffD)
    {
        if (trainSet.Count < 30)
            return new AdaBoostDriftArtifact();

        int n = trainSet.Count;
        int half = n / 2;
        int flagged = 0;
        var flaggedNames = new List<string>();
        double sumAcf = 0, sumPsi = 0, sumCp = 0, sumAdf = 0, sumKpss = 0;

        for (int fi = 0; fi < F; fi++)
        {
            int signals = 0;

            // 1. Lag-1 autocorrelation
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            int nc = n - 1;
            for (int i = 0; i < nc; i++)
            {
                double x = trainSet[i].Features[fi];
                double y = trainSet[i + 1].Features[fi];
                sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x; sumY2 += y * y;
            }
            double varXc  = sumX2 - sumX * sumX / nc;
            double varYc  = sumY2 - sumY * sumY / nc;
            double denomC = Math.Sqrt(Math.Max(0, varXc * varYc));
            double rho    = denomC > 1e-12 ? (sumXY - sumX * sumY / nc) / denomC : 0;
            if (Math.Abs(rho) > 0.97) signals++;
            sumAcf += Math.Abs(rho);

            // 2. PSI between first/second half (5 bins)
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
            if (cpNorm > 1.36) signals++; // ~5% critical value for Kolmogorov-Smirnov
            sumCp += cpNorm;

            // 4. ADF-like: OLS Δy = α + β·y_{t-1}, check |β/se(β)| > 2.86
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

            // 5. KPSS-like: partial sum of residuals from mean
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
            if (kpssSum > 0.463) signals++; // ~5% critical value
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

        return new AdaBoostDriftArtifact
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
}

