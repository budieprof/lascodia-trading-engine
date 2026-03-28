using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Extracted calibration routines for the ELM trainer: Platt scaling, isotonic (PAVA),
/// conformal prediction, temperature scaling, Kelly fraction, and decision boundary stats.
/// All methods are stateless and thread-safe.
/// </summary>
internal static class ElmCalibrationHelper
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Platt calibration
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb)
    {
        if (calSet.Count < 5) return (1.0, 0.0);

        double a = 1.0, b = 0.0;
        double bestA = a, bestB = b;
        const double baseLr = 0.01;
        const double l2Lambda = 1e-4;
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        double bestLoss = double.MaxValue;
        int patience = 0;
        const int maxPatience = 30;
        const int plattMaxEpochs = 300;

        double mA = 0, mB = 0, vA = 0, vB = 0;

        double[] rawLogits = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double raw = ensembleRawProb(
                calSet[i].Features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, null);
            rawLogits[i] = MLFeatureHelper.Logit(Math.Clamp(raw, 1e-7, 1.0 - 1e-7));
        }

        const int plattBatchSize = 256;
        bool useBatch = calSet.Count > plattBatchSize * 2;
        var batchRng = useBatch ? new Random(calSet.Count) : null;
        int[] batchOrder = useBatch ? Enumerable.Range(0, calSet.Count).ToArray() : [];

        int globalStep = 0;
        for (int epoch = 0; epoch < plattMaxEpochs; epoch++)
        {
            double lr = ElmMathHelper.CosineAnnealLr(baseLr, epoch, plattMaxEpochs);
            if (useBatch) ElmMathHelper.ShuffleArray(batchOrder, batchRng!);

            int batchCount = useBatch ? (calSet.Count + plattBatchSize - 1) / plattBatchSize : 1;
            for (int bi = 0; bi < batchCount; bi++)
            {
                int bStart = useBatch ? bi * plattBatchSize : 0;
                int bEnd = useBatch ? Math.Min(bStart + plattBatchSize, calSet.Count) : calSet.Count;
                int bLen = bEnd - bStart;

                double gradA = 0, gradB = 0;
                for (int bIdx = bStart; bIdx < bEnd; bIdx++)
                {
                    int i = useBatch ? batchOrder[bIdx] : bIdx;
                    double p = MLFeatureHelper.Sigmoid(a * rawLogits[i] + b);
                    double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
                    double err = p - y;
                    gradA += err * rawLogits[i];
                    gradB += err;
                }
                double gA = gradA / bLen + 2.0 * l2Lambda * a;
                double gB = gradB / bLen + 2.0 * l2Lambda * b;

                globalStep++;
                mA = beta1 * mA + (1 - beta1) * gA;
                mB = beta1 * mB + (1 - beta1) * gB;
                vA = beta2 * vA + (1 - beta2) * gA * gA;
                vB = beta2 * vB + (1 - beta2) * gB * gB;
                double mAHat = mA / (1 - Math.Pow(beta1, globalStep));
                double mBHat = mB / (1 - Math.Pow(beta1, globalStep));
                double vAHat = vA / (1 - Math.Pow(beta2, globalStep));
                double vBHat = vB / (1 - Math.Pow(beta2, globalStep));
                a -= lr * mAHat / (Math.Sqrt(vAHat) + eps);
                b -= lr * mBHat / (Math.Sqrt(vBHat) + eps);
            }

            double loss = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double p = MLFeatureHelper.Sigmoid(a * rawLogits[i] + b);
                double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
                loss -= y * Math.Log(Math.Max(p, 1e-10)) + (1 - y) * Math.Log(Math.Max(1 - p, 1e-10));
            }
            loss /= calSet.Count;
            loss += l2Lambda * (a * a + b * b);

            if (loss < bestLoss - 1e-7)
            {
                bestLoss = loss;
                bestA = a; bestB = b;
                patience = 0;
            }
            else if (++patience >= maxPatience)
            {
                break;
            }
        }
        return (bestA, bestB);
    }

    internal static (double A, double B) FitPlattScalingCV(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb,
        int cvFolds = 3)
    {
        if (calSet.Count < cvFolds * 20)
            return FitPlattScaling(calSet, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, ensembleRawProb);

        double sumA = 0, sumB = 0;
        int foldSize = calSet.Count / cvFolds;
        int completedFolds = 0;

        for (int fold = 0; fold < cvFolds; fold++)
        {
            int valStart = fold * foldSize;
            int valEnd = fold == cvFolds - 1 ? calSet.Count : valStart + foldSize;

            var trainFold = new List<TrainingSample>(calSet.Count - (valEnd - valStart));
            for (int i = 0; i < valStart; i++) trainFold.Add(calSet[i]);
            for (int i = valEnd; i < calSet.Count; i++) trainFold.Add(calSet[i]);

            if (trainFold.Count < 5) continue;

            var (foldA, foldB) = FitPlattScaling(
                trainFold, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, ensembleRawProb);
            sumA += foldA;
            sumB += foldB;
            completedFolds++;
        }

        return completedFolds > 0 ? (sumA / completedFolds, sumB / completedFolds) : (1.0, 0.0);
    }

    internal static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb)
    {
        var buySamples  = calSet.Where(s => s.Direction > 0).ToList();
        var sellSamples = calSet.Where(s => s.Direction <= 0).ToList();

        var (aBuy, bBuy)   = buySamples.Count >= 5
            ? FitPlattScaling(buySamples, weights, biases, inputWeights, inputBiases, featureCount, hiddenSize, featureSubsets, ensembleRawProb)
            : (1.0, 0.0);
        var (aSell, bSell) = sellSamples.Count >= 5
            ? FitPlattScaling(sellSamples, weights, biases, inputWeights, inputBiases, featureCount, hiddenSize, featureSubsets, ensembleRawProb)
            : (1.0, 0.0);

        return (aBuy, bBuy, aSell, bSell);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Isotonic calibration (PAVA)
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb)
    {
        if (calSet.Count < 5) return [];

        var pairs = new (double Pred, double Target)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            pairs[i] = (
                ensembleCalibProb(
                    calSet[i].Features, weights, biases, inputWeights, inputBiases,
                    plattA, plattB, featureCount, hiddenSize, featureSubsets, null),
                calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.Pred.CompareTo(b.Pred));

        int n = pairs.Length;
        var stackVal  = new double[n];
        var stackW    = new double[n];
        var stackPred = new double[n];
        int top = 0;

        for (int i = 0; i < n; i++)
        {
            stackVal[top]  = pairs[i].Target;
            stackW[top]    = 1.0;
            stackPred[top] = pairs[i].Pred;
            top++;

            while (top >= 2 && stackVal[top - 2] / stackW[top - 2] > stackVal[top - 1] / stackW[top - 1])
            {
                stackVal[top - 2]  += stackVal[top - 1];
                double mergedW      = stackW[top - 2] + stackW[top - 1];
                stackPred[top - 2]  = (stackPred[top - 2] * stackW[top - 2]
                                     + stackPred[top - 1] * stackW[top - 1]) / mergedW;
                stackW[top - 2]     = mergedW;
                top--;
            }
        }

        var bp = new double[top * 2];
        for (int i = 0; i < top; i++)
        {
            bp[i * 2]     = stackPred[i];
            bp[i * 2 + 1] = stackVal[i] / stackW[i];
        }
        return bp;
    }

    internal static double ApplyIsotonicCalibration(double p, double[] breakpoints)
    {
        if (breakpoints.Length < 2) return p;
        int n = breakpoints.Length / 2;
        if (p <= breakpoints[0]) return breakpoints[1];
        if (p >= breakpoints[(n - 1) * 2]) return breakpoints[(n - 1) * 2 + 1];

        for (int i = 0; i < n - 1; i++)
        {
            double x0 = breakpoints[i * 2], y0 = breakpoints[i * 2 + 1];
            double x1 = breakpoints[(i + 1) * 2], y1 = breakpoints[(i + 1) * 2 + 1];
            if (p >= x0 && p <= x1)
            {
                double t = (x1 - x0) > 1e-10 ? (p - x0) / (x1 - x0) : 0.5;
                return y0 + t * (y1 - y0);
            }
        }
        return p;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Conformal prediction
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB, double[] isotonicBp,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        double alpha,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb)
    {
        if (calSet.Count < 5) return 0.5;

        var residuals = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double calibP = ensembleCalibProb(
                calSet[i].Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null);
            if (isotonicBp.Length > 0) calibP = ApplyIsotonicCalibration(calibP, isotonicBp);
            double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals[i] = Math.Abs(y - calibP);
        }
        Array.Sort(residuals);

        int idx = (int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1;
        idx = Math.Clamp(idx, 0, calSet.Count - 1);
        return residuals[idx];
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Temperature scaling
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb)
    {
        if (calSet.Count < 5) return 1.0;

        double[] logits = new double[calSet.Count];
        double[] targets = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double raw = ensembleRawProb(
                calSet[i].Features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, null);
            logits[i] = Math.Log(Math.Max(raw, 1e-7) / Math.Max(1 - raw, 1e-7));
            targets[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double BceLoss(double T)
        {
            double loss = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double p = MLFeatureHelper.Sigmoid(logits[i] / T);
                loss -= targets[i] * Math.Log(Math.Max(p, 1e-10))
                      + (1 - targets[i]) * Math.Log(Math.Max(1 - p, 1e-10));
            }
            return loss;
        }

        const double phi = 1.6180339887498949;
        double a = 0.1, b = 5.0;
        double c = b - (b - a) / phi;
        double d = a + (b - a) / phi;
        double fc = BceLoss(c), fd = BceLoss(d);

        for (int iter = 0; iter < 50 && (b - a) > 0.001; iter++)
        {
            if (fc < fd)
            {
                b = d;
                d = c; fd = fc;
                c = b - (b - a) / phi;
                fc = BceLoss(c);
            }
            else
            {
                a = c;
                c = d; fc = fd;
                d = a + (b - a) / phi;
                fd = BceLoss(d);
            }
        }

        return (a + b) / 2.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Kelly fraction
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb)
    {
        if (calSet.Count == 0) return 0;

        double winMagSum = 0, lossMagSum = 0;
        int winCount = 0, lossCount = 0;
        foreach (var s in calSet)
        {
            double mag = Math.Max(1e-6, Math.Abs(s.Magnitude));
            if (s.Direction > 0) { winMagSum += mag; winCount++; }
            else { lossMagSum += mag; lossCount++; }
        }

        double avgWin  = winCount > 0 ? winMagSum / winCount : 1.0;
        double avgLoss = lossCount > 0 ? lossMagSum / lossCount : 1.0;
        double b = avgLoss > 1e-10 ? avgWin / avgLoss : 1.0;

        double sum = 0;
        foreach (var s in calSet)
        {
            double p = ensembleCalibProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null);
            double q = 1.0 - p;
            double kelly = (p * b - q) / b;
            sum += Math.Max(0, kelly);
        }
        return (sum / calSet.Count) * 0.5;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Decision boundary distance
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb)
    {
        if (calSet.Count < 2) return (0, 0);

        double sum = 0, sumSq = 0;
        foreach (var s in calSet)
        {
            double raw = ensembleRawProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, null);
            double logit = Math.Log(Math.Max(raw, 1e-7) / Math.Max(1 - raw, 1e-7));
            double dist = Math.Abs(logit);
            sum += dist;
            sumSq += dist * dist;
        }
        double mean = sum / calSet.Count;
        double std = Math.Sqrt(Math.Max(0, sumSq / calSet.Count - mean * mean));
        return (mean, std);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EV-optimal threshold
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeOptimalThreshold(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int searchMin, int searchMax,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb)
    {
        if (calSet.Count < 10) return 0.50;

        double bestThreshold = 0.50;
        double bestEV = double.MinValue;

        double[] probs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            probs[i] = ensembleCalibProb(
                calSet[i].Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null);
        }

        int lo = Math.Clamp(searchMin, 1, 99);
        int hi = Math.Clamp(searchMax, lo, 99);

        for (int pct = lo; pct <= hi; pct++)
        {
            double thr = pct / 100.0;
            int correct = 0, total = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double p = probs[i];
                if (p >= thr || p < (1.0 - thr))
                {
                    total++;
                    int pred = p >= 0.5 ? 1 : 0;
                    if (pred == calSet[i].Direction) correct++;
                }
            }
            if (total < 5) continue;
            double ev = (double)correct / total - 0.5;
            if (ev > bestEV) { bestEV = ev; bestThreshold = thr; }
        }

        return bestThreshold;
    }
}
