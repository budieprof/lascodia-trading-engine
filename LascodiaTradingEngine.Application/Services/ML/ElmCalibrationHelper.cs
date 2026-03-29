using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Extracted calibration routines for the ELM trainer: Platt scaling, isotonic (PAVA),
/// conformal prediction, temperature scaling, Kelly fraction, and decision boundary stats.
/// All methods are stateless and thread-safe.
/// </summary>
internal static class ElmCalibrationHelper
{
    private static int ToBinaryLabel(int direction) => direction > 0 ? 1 : 0;

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

        bool hasPositive = false, hasNegative = false;
        foreach (var sample in calSet)
        {
            if (sample.Direction > 0) hasPositive = true;
            else hasNegative = true;

            if (hasPositive && hasNegative)
                break;
        }

        if (!hasPositive || !hasNegative)
            return (1.0, 0.0);

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
            rawLogits[i] = MLFeatureHelper.Logit(ClampLogitProbability(raw));
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
                    double y = ToBinaryLabel(calSet[i].Direction);
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
                double y = ToBinaryLabel(calSet[i].Direction);
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

    internal static double ApplyGlobalCalibration(
        double rawProb,
        double plattA,
        double plattB,
        double temperatureScale)
    {
        double rawLogit = MLFeatureHelper.Logit(ClampLogitProbability(rawProb));
        return temperatureScale > 0.0 && temperatureScale < 10.0
            ? MLFeatureHelper.Sigmoid(rawLogit / temperatureScale)
            : MLFeatureHelper.Sigmoid(plattA * rawLogit + plattB);
    }

    internal static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        double plattA, double plattB, double temperatureScale,
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb)
    {
        static bool HasBothClasses(List<TrainingSample> samples)
        {
            bool hasBuy = false, hasSell = false;
            foreach (var sample in samples)
            {
                if (sample.Direction > 0) hasBuy = true;
                else hasSell = true;
                if (hasBuy && hasSell) return true;
            }
            return false;
        }

        var buySamples = new List<TrainingSample>();
        var sellSamples = new List<TrainingSample>();
        foreach (var sample in calSet)
        {
            double rawProb = ensembleRawProb(
                sample.Features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, null);
            double globalCalibProb = ApplyGlobalCalibration(rawProb, plattA, plattB, temperatureScale);
            if (globalCalibProb >= 0.5) buySamples.Add(sample);
            else sellSamples.Add(sample);
        }

        var (aBuy, bBuy) = buySamples.Count >= 10 && HasBothClasses(buySamples)
            ? FitPlattScaling(buySamples, weights, biases, inputWeights, inputBiases, featureCount, hiddenSize, featureSubsets, ensembleRawProb)
            : (0.0, 0.0);
        var (aSell, bSell) = sellSamples.Count >= 10 && HasBothClasses(sellSamples)
            ? FitPlattScaling(sellSamples, weights, biases, inputWeights, inputBiases, featureCount, hiddenSize, featureSubsets, ensembleRawProb)
            : (0.0, 0.0);

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
                ClampProbability(ensembleCalibProb(
                    calSet[i].Features, weights, biases, inputWeights, inputBiases,
                    plattA, plattB, featureCount, hiddenSize, featureSubsets, null)),
                ToBinaryLabel(calSet[i].Direction));
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
        double clampedP = ClampProbability(p);
        if (breakpoints.Length < 2)
            return clampedP;

        var clean = new List<(double X, double Y)>(breakpoints.Length / 2);
        for (int i = 0; i + 1 < breakpoints.Length; i += 2)
        {
            double rawX = breakpoints[i];
            double rawY = breakpoints[i + 1];
            if (!double.IsFinite(rawX) || !double.IsFinite(rawY))
                continue;

            double x = ClampProbability(rawX);
            double y = ClampProbability(rawY);
            if (clean.Count > 0)
            {
                var last = clean[^1];
                if (x < last.X)
                    continue;

                if (Math.Abs(x - last.X) <= 1e-12)
                {
                    clean[^1] = (x, y);
                    continue;
                }
            }

            clean.Add((x, y));
        }

        if (clean.Count == 0)
            return clampedP;
        if (clean.Count == 1)
            return clean[0].Y;
        if (clampedP <= clean[0].X)
            return clean[0].Y;

        for (int i = 0; i < clean.Count - 1; i++)
        {
            var (x0, y0) = clean[i];
            var (x1, y1) = clean[i + 1];
            if (clampedP > x1)
                continue;

            double t = (x1 - x0) > 1e-10 ? (clampedP - x0) / (x1 - x0) : 0.5;
            return ClampProbability(y0 + t * (y1 - y0));
        }

        return clean[^1].Y;
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
            calibP = ClampProbability(calibP);
            if (isotonicBp.Length > 0) calibP = ApplyIsotonicCalibration(calibP, isotonicBp);
            double y = ToBinaryLabel(calSet[i].Direction);
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
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb,
        double plattA = 1.0, double plattB = 0.0,
        double plattABuy = 0.0, double plattBBuy = 0.0,
        double plattASell = 0.0, double plattBSell = 0.0,
        double[]? isotonicBreakpoints = null,
        double ageDecayLambda = 0.0,
        DateTime trainedAtUtc = default)
    {
        if (calSet.Count < 5) return 1.0;

        double[] rawProbs = new double[calSet.Count];
        double[] targets = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double raw = ensembleRawProb(
                calSet[i].Features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, null);
            rawProbs[i] = ClampLogitProbability(raw);
            targets[i] = ToBinaryLabel(calSet[i].Direction);
        }

        double BceLoss(double T)
        {
            double loss = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double p = ApplyTemperatureAwareCalibration(
                    rawProbs[i],
                    T,
                    plattA,
                    plattB,
                    plattABuy,
                    plattBBuy,
                    plattASell,
                    plattBSell,
                    isotonicBreakpoints,
                    ageDecayLambda,
                    trainedAtUtc);
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

    private static double ApplyTemperatureAwareCalibration(
        double rawP,
        double temperatureScale,
        double plattA,
        double plattB,
        double plattABuy,
        double plattBBuy,
        double plattASell,
        double plattBSell,
        double[]? isotonicBreakpoints,
        double ageDecayLambda,
        DateTime trainedAtUtc)
    {
        double clampedRaw = ClampLogitProbability(rawP);
        double rawLogit = MLFeatureHelper.Logit(clampedRaw);
        double safeTemperature = double.IsFinite(temperatureScale) ? temperatureScale : 0.0;
        double safePlattA = double.IsFinite(plattA) ? plattA : 1.0;
        double safePlattB = double.IsFinite(plattB) ? plattB : 0.0;
        double safePlattABuy = double.IsFinite(plattABuy) ? plattABuy : 0.0;
        double safePlattBBuy = double.IsFinite(plattBBuy) ? plattBBuy : 0.0;
        double safePlattASell = double.IsFinite(plattASell) ? plattASell : 0.0;
        double safePlattBSell = double.IsFinite(plattBSell) ? plattBSell : 0.0;
        double globalCalibP = safeTemperature > 0.0 && safeTemperature < 10.0
            ? MLFeatureHelper.Sigmoid(rawLogit / safeTemperature)
            : MLFeatureHelper.Sigmoid(safePlattA * rawLogit + safePlattB);

        double calibP;
        if (globalCalibP >= 0.5 && safePlattABuy != 0.0)
            calibP = MLFeatureHelper.Sigmoid(safePlattABuy * rawLogit + safePlattBBuy);
        else if (globalCalibP < 0.5 && safePlattASell != 0.0)
            calibP = MLFeatureHelper.Sigmoid(safePlattASell * rawLogit + safePlattBSell);
        else
            calibP = globalCalibP;

        if (isotonicBreakpoints is { Length: >= 4 })
            calibP = ApplyIsotonicCalibration(calibP, isotonicBreakpoints);

        double safeAgeDecayLambda = double.IsFinite(ageDecayLambda) && ageDecayLambda > 0.0
            ? ageDecayLambda
            : 0.0;
        if (safeAgeDecayLambda > 0.0 && trainedAtUtc != default)
        {
            double daysSinceTrain = (DateTime.UtcNow - trainedAtUtc).TotalDays;
            double decayFactor = Math.Exp(-safeAgeDecayLambda * Math.Max(0.0, daysSinceTrain));
            calibP = 0.5 + (calibP - 0.5) * decayFactor;
        }

        return ClampProbability(calibP);
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

        double sum = 0;
        foreach (var s in calSet)
        {
            double p = ClampProbability(ensembleCalibProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null));

            // Kelly fraction for roughly symmetric payoff trades simplifies to max(0, 2p - 1).
            sum += Math.Max(0.0, 2.0 * p - 1.0);
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
            double logit = MLFeatureHelper.Logit(ClampLogitProbability(raw));
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
            probs[i] = ClampProbability(ensembleCalibProb(
                calSet[i].Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null));
        }

        int lo = Math.Clamp(searchMin, 1, 99);
        int hi = Math.Clamp(searchMax, lo, 99);

        for (int pct = lo; pct <= hi; pct++)
        {
            double thr = pct / 100.0;
            double ev = 0.0;
            for (int i = 0; i < calSet.Count; i++)
            {
                bool predictedBuy = probs[i] >= thr;
                bool actualBuy = calSet[i].Direction > 0;
                bool correct = predictedBuy == actualBuy;
                double absMagnitude = Math.Abs(double.IsFinite(calSet[i].Magnitude) ? calSet[i].Magnitude : 0.0);
                ev += (correct ? 1.0 : -1.0) * Math.Max(0.001, absMagnitude);
            }

            ev /= calSet.Count;
            if (ev > bestEV) { bestEV = ev; bestThreshold = thr; }
        }

        return bestThreshold;
    }

    private static double ClampProbability(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 0.0, 1.0);
    }

    private static double ClampLogitProbability(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 1e-7, 1.0 - 1e-7);
    }
}
