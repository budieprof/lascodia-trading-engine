using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ───────────────────────────────────────────────────────────────────
    //  Item 13: Venn-ABERS calibration
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fits Venn-ABERS calibration on the calibration set.
    /// For each sample, fits isotonic regression on the remaining n-1 samples twice:
    /// once with the sample labelled 0, once labelled 1. Returns [p_lower, p_upper] bounds.
    /// For computational efficiency, uses a simplified one-pass approach with sorted raw probs.
    /// </summary>
    internal static double[][] FitVennAbers(List<TrainingSample> calSet, double[] rawProbs)
    {
        if (calSet.Count < 10) return [];
        int n = calSet.Count;

        // Sort by raw probability
        var sorted = new (double Prob, int Label, int Idx)[n];
        for (int i = 0; i < n; i++)
            sorted[i] = (rawProbs[i], calSet[i].Direction > 0 ? 1 : 0, i);
        Array.Sort(sorted, (a, b) => a.Prob.CompareTo(b.Prob));

        var result = new double[n][];

        // For each calibration sample, compute p0 and p1 using leave-one-out isotonic regression
        // Simplified: use the full isotonic regression and adjust for the LOO effect
        var probs = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++) { probs[i] = sorted[i].Prob; labels[i] = sorted[i].Label; }

        // Fit isotonic regression with label=0 for each sample (lower bound)
        // and label=1 (upper bound) using PAVA
        for (int i = 0; i < n; i++)
        {
            int origIdx = sorted[i].Idx;

            // p0: assume this sample is class 0
            double savedLabel = labels[i];
            labels[i] = 0;
            double p0 = IsotonicPredict(probs, labels, n, probs[i]);

            // p1: assume this sample is class 1
            labels[i] = 1;
            double p1 = IsotonicPredict(probs, labels, n, probs[i]);

            labels[i] = savedLabel; // restore

            result[origIdx] = [p0, p1];
        }

        return result;
    }

    /// <summary>
    /// Simple isotonic regression prediction at a query point using PAVA on pre-sorted data.
    /// </summary>
    private static double IsotonicPredict(double[] sortedX, double[] labels, int n, double query)
    {
        // PAVA: pool adjacent violators
        var blockSum = new double[n];
        var blockCount = new int[n];
        var blockRight = new int[n];
        for (int i = 0; i < n; i++) { blockSum[i] = labels[i]; blockCount[i] = 1; blockRight[i] = i; }

        int cur = 0;
        while (cur < n)
        {
            int next = blockRight[cur] + 1;
            if (next >= n) { cur = next; continue; }
            double avg1 = blockSum[cur] / blockCount[cur];
            double avg2 = blockSum[next] / blockCount[next];
            if (avg1 <= avg2) { cur = next; continue; }
            blockSum[cur] += blockSum[next];
            blockCount[cur] += blockCount[next];
            blockRight[cur] = blockRight[next];
            if (cur > 0)
            {
                int prev = cur - 1;
                while (prev > 0 && blockRight[prev - 1] >= prev) prev--;
                cur = prev;
            }
        }

        // Find the block containing query
        cur = 0;
        while (cur < n)
        {
            int right = blockRight[cur];
            if (sortedX[right] >= query || right == n - 1)
                return blockSum[cur] / blockCount[cur];
            cur = right + 1;
        }
        return 0.5;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 14: Beta calibration
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fits 3-parameter Beta calibration: P_cal = 1 / (1 + 1/(e^c * (p/(1-p))^a * ((1-p)/p)^b)).
    /// Simplifies to: logit(P_cal) = c + a * log(p) + b * log(1-p).
    /// Fits via SGD on the calibration set.
    /// </summary>
    internal static (double A, double B, double C) FitBetaCalibration(
        List<TrainingSample> calSet, double[] rawProbs)
    {
        if (calSet.Count < 10) return (0, 0, 0);

        double a = 1.0, b = -1.0, c = 0.0;
        const double lr = 0.01;
        const int epochs = 300;
        int n = calSet.Count;

        for (int ep = 0; ep < epochs; ep++)
        {
            double dA = 0, dB = 0, dC = 0;
            for (int i = 0; i < n; i++)
            {
                double p = rawProbs[i];
                double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
                double logP = Math.Log(Math.Max(p, 1e-10));
                double log1P = Math.Log(Math.Max(1 - p, 1e-10));
                double logit = c + a * logP + b * log1P;
                double pred = 1.0 / (1.0 + Math.Exp(-logit));
                double err = pred - y;
                dA += err * logP;
                dB += err * log1P;
                dC += err;
            }
            a -= lr * dA / n;
            b -= lr * dB / n;
            c -= lr * dC / n;
        }

        return (a, b, c);
    }

    /// <summary>
    /// Applies Beta calibration to a raw probability.
    /// </summary>
    internal static double ApplyBetaCalibration(double rawP, double a, double b, double c)
    {
        if (a == 0 && b == 0 && c == 0) return rawP;
        double logP = Math.Log(Math.Max(rawP, 1e-10));
        double log1P = Math.Log(Math.Max(1 - rawP, 1e-10));
        return 1.0 / (1.0 + Math.Exp(-(c + a * logP + b * log1P)));
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 15: Calibration error decomposition (MCE + class-wise ECE)
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes Maximum Calibration Error (MCE) -- the worst-bin |confidence - accuracy|.
    /// Also returns class-wise ECE for Buy and Sell predictions separately.
    /// </summary>
    internal static (double Mce, double EceBuy, double EceSell) ComputeCalibrationDecomposition(
        List<TrainingSample> samples,
        double[] rawProbs,
        in TcnCalibrationArtifacts calibration)
    {
        if (samples.Count < 20) return (0, 0, 0);
        const int B = 10;
        double decisionThreshold = CalibrationThreshold(calibration);

        // Global MCE
        var binConf = new double[B]; var binAcc = new int[B]; var binCount = new int[B];
        // Buy-side bins
        var buyConf = new double[B]; var buyAcc = new int[B]; var buyCount = new int[B];
        // Sell-side bins
        var sellConf = new double[B]; var sellAcc = new int[B]; var sellCount = new int[B];

        for (int i = 0; i < samples.Count; i++)
        {
            double p = ApplyTcnCalibration(rawProbs[i], calibration);
            int bin = Math.Clamp((int)(p * B), 0, B - 1);
            bool isPos = samples[i].Direction == 1;

            binConf[bin] += p; binCount[bin]++;
            if (isPos) binAcc[bin]++;

            if (p >= decisionThreshold) { buyConf[bin] += p; buyCount[bin]++; if (isPos) buyAcc[bin]++; }
            else
            {
                sellConf[bin] += 1.0 - p;
                sellCount[bin]++;
                if (!isPos) sellAcc[bin]++;
            }
        }

        double mce = 0;
        for (int b = 0; b < B; b++)
        {
            if (binCount[b] == 0) continue;
            double gap = Math.Abs(binConf[b] / binCount[b] - (double)binAcc[b] / binCount[b]);
            if (gap > mce) mce = gap;
        }

        double eceBuy = ComputeWeightedEce(buyConf, buyAcc, buyCount, B);
        double eceSell = ComputeWeightedEce(sellConf, sellAcc, sellCount, B);

        return (mce, eceBuy, eceSell);
    }

    /// <summary>
    /// Computes weighted Expected Calibration Error across bins.
    /// </summary>
    private static double ComputeWeightedEce(double[] conf, int[] acc, int[] count, int bins)
    {
        int total = 0;
        for (int b = 0; b < bins; b++) total += count[b];
        if (total == 0) return 0;
        double ece = 0;
        for (int b = 0; b < bins; b++)
        {
            if (count[b] == 0) continue;
            double avgConf = conf[b] / count[b];
            double avgAcc = (double)acc[b] / count[b];
            ece += Math.Abs(avgConf - avgAcc) * count[b] / total;
        }
        return ece;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Item 16: Recalibration stability metric
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the standard deviation of Platt A and B parameters across walk-forward fold results.
    /// High variance indicates the model's raw scores are unstable across time periods.
    /// </summary>
    internal static (double StdA, double StdB) ComputeRecalibrationStability(
        List<(double PlattA, double PlattB)> foldPlattParams)
    {
        if (foldPlattParams.Count < 2) return (0, 0);

        double meanA = 0, meanB = 0;
        foreach (var (a, b) in foldPlattParams) { meanA += a; meanB += b; }
        meanA /= foldPlattParams.Count; meanB /= foldPlattParams.Count;

        double varA = 0, varB = 0;
        foreach (var (a, b) in foldPlattParams)
        {
            varA += (a - meanA) * (a - meanA);
            varB += (b - meanB) * (b - meanB);
        }

        int n = foldPlattParams.Count;
        return (Math.Sqrt(varA / (n - 1)), Math.Sqrt(varB / (n - 1)));
    }
}
