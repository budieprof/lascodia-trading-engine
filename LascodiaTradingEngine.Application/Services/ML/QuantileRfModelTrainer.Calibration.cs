using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class QuantileRfModelTrainer
{

    // ── Platt scaling (SGD, 200 epochs) ───────────────────────────────────────

    /// <summary>
    /// #4: Platt scaling with early convergence termination. SGD stops when the
    /// absolute change in both A and B falls below <see cref="PlattConvergenceDelta"/>.
    /// </summary>
    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        if (calSet.Count < MinCalSamples) return (1.0, 0.0);

        int n      = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];

        for (int i = 0; i < n; i++)
        {
            double raw = PredictRawProb(calSet[i].Features, allTrees, trainSet);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i]  = MLFeatureHelper.Logit(raw);
            labels[i]  = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;

        for (int epoch = 0; epoch < PlattMaxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double p   = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err = p - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            double stepA = PlattLearningRate * dA / n;
            double stepB = PlattLearningRate * dB / n;
            plattA -= stepA;
            plattB -= stepB;

            // #4: Early convergence — stop when updates are negligible
            if (Math.Abs(stepA) < PlattConvergenceDelta && Math.Abs(stepB) < PlattConvergenceDelta)
                break;
        }

        return (double.IsFinite(plattA) ? plattA : 1.0,
                double.IsFinite(plattB) ? plattB : 0.0);
    }

    // ── Class-conditional Platt (full calset, class-weighted labels) ─────────
    //
    // Buy calibrator:  labels buy=1, sell=0, buy samples weighted 3:1 vs sell.
    // Sell calibrator:  labels sell=1, buy=0, sell samples weighted 3:1 vs buy.
    // Both calibrators see the full calibration set so both classes constrain the
    // sigmoid — matching the approach used by GbmModelTrainer.

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        if (calSet.Count < MinCalSamplesPlatt) return (0.0, 0.0, 0.0, 0.0);

        int n      = calSet.Count;
        var logits = new double[n];
        var isBuy  = new bool[n];
        int buyCount = 0;
        for (int i = 0; i < n; i++)
        {
            double raw = PredictRawProb(calSet[i].Features, allTrees, trainSet);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i]  = MLFeatureHelper.Logit(raw);
            isBuy[i]   = calSet[i].Direction > 0;
            if (isBuy[i]) buyCount++;
        }
        int sellCount = n - buyCount;

        // #5: Adaptive class weights proportional to inverse class frequency
        // (replaces hardcoded 3:1). Minority class gets higher weight so that
        // both classes contribute equally to the gradient regardless of imbalance.
        double buyWeightForBuyCal  = sellCount > 0 ? (double)sellCount / buyCount  : 1.0;
        double sellWeightForBuyCal = 1.0;
        double sellWeightForSellCal = buyCount > 0 ? (double)buyCount / sellCount : 1.0;
        double buyWeightForSellCal  = 1.0;

        // Clamp weights to avoid extreme ratios
        buyWeightForBuyCal   = Math.Clamp(buyWeightForBuyCal,   1.0, 10.0);
        sellWeightForSellCal = Math.Clamp(sellWeightForSellCal, 1.0, 10.0);

        // Buy calibrator: standard labels (buy=1, sell=0), upweight buy samples
        double aBuy = 1.0, bBuy = 0.0;
        for (int epoch = 0; epoch < PlattMaxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(aBuy * logits[i] + bBuy);
                double label  = isBuy[i] ? 1.0 : 0.0;
                double w      = isBuy[i] ? buyWeightForBuyCal : sellWeightForBuyCal;
                double err    = (calibP - label) * w;
                dA += err * logits[i];
                dB += err;
            }
            double stepA = PlattLearningRate * dA / n;
            double stepB = PlattLearningRate * dB / n;
            aBuy -= stepA;
            bBuy -= stepB;
            if (Math.Abs(stepA) < PlattConvergenceDelta && Math.Abs(stepB) < PlattConvergenceDelta)
                break;
        }

        // Sell calibrator: inverted labels (sell=1, buy=0), upweight sell samples
        double aSell = 1.0, bSell = 0.0;
        for (int epoch = 0; epoch < PlattMaxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(aSell * logits[i] + bSell);
                double label  = isBuy[i] ? 0.0 : 1.0;
                double w      = isBuy[i] ? buyWeightForSellCal : sellWeightForSellCal;
                double err    = (calibP - label) * w;
                dA += err * logits[i];
                dB += err;
            }
            double stepA = PlattLearningRate * dA / n;
            double stepB = PlattLearningRate * dB / n;
            aSell -= stepA;
            bSell -= stepB;
            if (Math.Abs(stepA) < PlattConvergenceDelta && Math.Abs(stepB) < PlattConvergenceDelta)
                break;
        }

        return (double.IsFinite(aBuy)  ? aBuy  : 0.0,
                double.IsFinite(bBuy)  ? bBuy  : 0.0,
                double.IsFinite(aSell) ? aSell : 0.0,
                double.IsFinite(bSell) ? bSell : 0.0);
    }

    // ── Isotonic calibration (PAVA) ───────────────────────────────────────────

    /// <summary>
    /// #6: Isotonic calibration (PAVA) with minimum block size regularisation.
    /// Blocks smaller than <see cref="IsotonicMinBlockSize"/> are merged with their
    /// neighbour to prevent overfitting on small calibration sets.
    /// </summary>
    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB)
    {
        if (calSet.Count < MinCalSamples) return [];

        int n     = calSet.Count;
        var pairs = new (double P, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            double raw = PredictRawProb(calSet[i].Features, allTrees, trainSet);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double p   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            pairs[i]   = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        // Standard PAVA
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Length);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var (lastSumY, lastSumP, lastCount) = stack[^1];
                var (prevSumY, prevSumP, prevCount) = stack[^2];
                if (prevSumY / prevCount > lastSumY / lastCount)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prevSumY + lastSumY, prevSumP + lastSumP, prevCount + lastCount);
                }
                else break;
            }
        }

        // #6: Merge blocks smaller than IsotonicMinBlockSize with their right neighbour
        // to regularise against overfitting on small cal sets.
        for (int i = stack.Count - 2; i >= 0; i--)
        {
            if (stack[i].Count < IsotonicMinBlockSize && i + 1 < stack.Count)
            {
                var (sy, sp, sc) = stack[i];
                var (ny, np, nc) = stack[i + 1];
                stack[i + 1] = (sy + ny, sp + np, sc + nc);
                stack.RemoveAt(i);
            }
        }

        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
    }

    private static double ApplyIsotonicCalibration(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        int nPoints = bp.Length / 2;
        if (p <= bp[0])                 return bp[1];
        if (p >= bp[(nPoints - 1) * 2]) return bp[(nPoints - 1) * 2 + 1];

        int lo = 0, hi = nPoints - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (bp[(mid + 1) * 2] <= p) lo = mid + 1;
            else hi = mid;
        }
        double x0 = bp[lo * 2],       y0 = bp[lo * 2 + 1];
        double x1 = bp[(lo + 1) * 2], y1 = bp[(lo + 1) * 2 + 1];
        return Math.Abs(x1 - x0) < 1e-15
            ? (y0 + y1) * 0.5
            : y0 + (p - x0) * (y1 - y0) / (x1 - x0);
    }

    // ── ECE (10 equal-width bins) ─────────────────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  bins = 10)
    {
        if (testSet.Count < bins) return 1.0;

        var binAcc  = new double[bins];
        var binConf = new double[bins];
        var binCnt  = new int[bins];

        foreach (var s in testSet)
        {
            double p    = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            int    binI = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[binI] += p;
            if (s.Direction > 0) binAcc[binI]++; // positive-class frequency, not classification accuracy
            binCnt[binI]++;
        }

        double ece = 0;
        int    n   = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCnt[b] == 0) continue;
            ece += (double)binCnt[b] / n * Math.Abs(binAcc[b] / binCnt[b] - binConf[b] / binCnt[b]);
        }
        return ece;
    }

    // ── EV-optimal threshold sweep ────────────────────────────────────────────

    /// <summary>
    /// #3: Uses <c>hp.ThresholdSearchStepBps</c> for finer-grained search.
    /// Default 50 bps = 0.5 % steps (vs. legacy 100 bps = 1 % steps).
    /// </summary>
    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  searchMin = 30,
        int                  searchMax = 75,
        int                  stepBps   = 50)
    {
        if (calSet.Count < 30) return 0.5;

        var probs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            probs[i] = PredictProb(calSet[i].Features, allTrees, trainSet, plattA, plattB, isotonicBp);

        double bestEv = double.MinValue, bestThreshold = 0.5;
        int step = Math.Max(1, stepBps);
        for (int bps = searchMin * 100; bps <= searchMax * 100; bps += step)
        {
            double t = bps / 10000.0, ev = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                bool correct = (probs[i] >= t) == (calSet[i].Direction > 0);
                ev += (correct ? 1 : -1) * (double)calSet[i].Magnitude;
            }
            ev /= calSet.Count;
            if (ev > bestEv) { bestEv = ev; bestThreshold = t; }
        }
        return bestThreshold;
    }

    // ── Kelly fraction (half-Kelly or magnitude-adjusted) ────────────────────

    /// <summary>
    /// #44: When <c>useAdjusted</c> is true, uses the magnitude-aware Kelly:
    /// f = p − (1−p) × avgLoss/avgWin. Otherwise uses simplified 2p−1.
    /// Always applies half-Kelly (÷2) for conservatism.
    /// </summary>
    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        bool                 useAdjusted = false)
    {
        if (calSet.Count == 0) return 0;

        if (useAdjusted)
        {
            // Compute average win/loss magnitudes on the calibration set
            double winSum = 0, lossSum = 0;
            int    winN = 0,   lossN = 0;
            foreach (var s in calSet)
            {
                double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
                bool correct = (p >= 0.5) == (s.Direction > 0);
                if (correct) { winSum  += (double)s.Magnitude; winN++; }
                else         { lossSum += (double)s.Magnitude; lossN++; }
            }
            double avgWin  = winN  > 0 ? winSum  / winN  : 1.0;
            double avgLoss = lossN > 0 ? lossSum / lossN : 1.0;
            double ratio   = avgWin > Eps ? avgLoss / avgWin : 1.0;

            double sum = 0;
            foreach (var s in calSet)
            {
                double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
                double kelly = p - (1.0 - p) * ratio;
                sum += Math.Max(0, kelly);
            }
            return sum / calSet.Count * 0.5;
        }

        // Simplified Kelly: 2p − 1 (assumes symmetric payoff)
        double simpleSum = 0;
        foreach (var s in calSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            simpleSum += Math.Max(0, 2 * p - 1);
        }
        return simpleSum / calSet.Count * 0.5;
    }

    // ── Temperature scaling (grid search T ∈ [0.5, 5.0], 0.01 steps) ────────

    /// <summary>
    /// #7: Finer grid search (0.01 steps vs. legacy 0.1 steps) for optimal NLL.
    /// </summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        double bestT = 1.0, bestLoss = double.MaxValue;

        for (int ti = 50; ti <= 500; ti++)
        {
            double T = ti / 100.0, loss = 0;
            foreach (var s in calSet)
            {
                double raw = PredictRawProb(s.Features, allTrees, trainSet);
                raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
                double p = MLFeatureHelper.Sigmoid(MLFeatureHelper.Logit(raw) / T);
                int    y = s.Direction > 0 ? 1 : 0;
                loss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
            }
            loss /= calSet.Count;
            if (loss < bestLoss) { bestLoss = loss; bestT = T; }
        }
        return bestT;
    }

    // ── Magnitude linear regressor (Adam + Huber + cosine LR + early stop) ────

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train,
        int                  F,
        TrainingHyperparams  hp,
        CancellationToken    ct = default)
    {
        var    w = new double[F];
        double b = 0.0;

        bool canEarlyStop = train.Count >= 30;
        int  valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var  valSlice     = canEarlyStop ? train[^valSize..] : train;
        var  trainSlice   = canEarlyStop ? train[..^valSize] : train;

        if (trainSlice.Count == 0) return (w, b);

        var    mW     = new double[F];
        var    vW     = new double[F];
        double mB     = 0.0, vB = 0.0;
        double beta1t = 1.0, beta2t = 1.0;
        int    t      = 0;

        double bestValLoss = double.MaxValue;
        var    bestW       = new double[F];
        double bestB       = 0.0;
        int    patience    = 0;

        int    epochs     = hp.MaxEpochs > 0 ? hp.MaxEpochs : 100;
        double baseLr     = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        double l2         = hp.L2Lambda;
        int    esPatience = Math.Max(5, hp.EarlyStoppingPatience / 2);

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lrCosine = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            foreach (var s in trainSlice)
            {
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;

                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;

                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                double bc1       = 1.0 - beta1t;
                double bc2       = 1.0 - beta2t;
                double alphAt    = lrCosine * Math.Sqrt(bc2) / bc1;

                mB  = AdamBeta1 * mB  + (1.0 - AdamBeta1) * huberGrad;
                vB  = AdamBeta2 * vB  + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b  -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                for (int j = 0; j < F; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j]   = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j]   = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j]   -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }

            if (!canEarlyStop) continue;

            double valLoss = 0.0;
            foreach (var s in valSlice)
            {
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5;
            }
            valLoss /= valSlice.Count;

            if (valLoss < bestValLoss)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, F);
                bestB    = b;
                patience = 0;
            }
            else if (++patience >= esPatience)
                break;
        }

        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ── 2-layer MLP magnitude regressor (ReLU hidden, Adam + Huber, cosine LR) ─

    /// <summary>
    /// Trains a 2-layer MLP magnitude regressor: input → ReLU(W1·x + b1) → W2·h + b2.
    /// Uses Adam with cosine-annealing LR, Huber loss, L2 regularisation, and early stopping.
    /// He initialisation for ReLU hidden weights. Trained on the same train/val split as the
    /// linear regressor for fair early-stopping comparison.
    /// Returns (W1[H×F], b1[H], W2[H], b2) where H = hiddenDim.
    /// </summary>
    private static (double[] W1, double[] B1, double[] W2, double B2) FitMlpMagnitudeRegressor(
        List<TrainingSample> train,
        int                  F,
        int                  H,
        TrainingHyperparams  hp,
        CancellationToken    ct = default)
    {
        var zeroW1 = new double[H * F];
        var zeroB1 = new double[H];
        var zeroW2 = new double[H];
        if (train.Count == 0) return (zeroW1, zeroB1, zeroW2, 0.0);

        // He initialisation for ReLU hidden layer
        var rng    = hp.QrfSeed != 0 ? new Random(hp.QrfSeed + 997) : new Random();
        double sw1 = Math.Sqrt(2.0 / F);
        double sw2 = Math.Sqrt(2.0 / H);

        var    W1 = new double[H * F];
        var    b1 = new double[H];
        var    W2 = new double[H];
        double b2 = 0.0;

        for (int i = 0; i < W1.Length; i++) W1[i] = (rng.NextDouble() * 2 - 1) * sw1;
        for (int h = 0; h < H; h++)         W2[h] = (rng.NextDouble() * 2 - 1) * sw2;

        // Adam moment buffers
        var    mW1 = new double[H * F]; var vW1 = new double[H * F];
        var    mB1 = new double[H];     var vB1 = new double[H];
        var    mW2 = new double[H];     var vW2 = new double[H];
        double mB2 = 0, vB2 = 0;

        bool canEarlyStop = train.Count >= 30;
        int  valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var  valSlice     = canEarlyStop ? train[^valSize..] : train;
        var  trainSlice   = canEarlyStop ? train[..^valSize] : train;
        if (trainSlice.Count == 0) return (zeroW1, zeroB1, zeroW2, 0.0);

        int    epochs    = hp.MaxEpochs > 0 ? hp.MaxEpochs : 100;
        double baseLr    = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        double l2        = hp.L2Lambda;
        int    esPatience = Math.Max(5, hp.EarlyStoppingPatience / 2);

        double bestVal = double.MaxValue;
        var    bW1 = (double[])W1.Clone(); var bB1 = (double[])b1.Clone();
        var    bW2 = (double[])W2.Clone(); double bB2 = b2;
        int    patience = 0, t = 0;
        double beta1t = 1.0, beta2t = 1.0;

        var hidden = new double[H];
        var dW1    = new double[H * F];
        var dB1    = new double[H];
        var dW2    = new double[H];

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lrCos = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            foreach (var s in trainSlice)
            {
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;
                double bc1    = 1.0 - beta1t;
                double bc2    = 1.0 - beta2t;
                double alphAt = lrCos * Math.Sqrt(bc2) / bc1;

                // Forward
                for (int h = 0; h < H; h++)
                {
                    double z = b1[h];
                    for (int j = 0; j < F && j < s.Features.Length; j++)
                        z += W1[h * F + j] * s.Features[j];
                    hidden[h] = Math.Max(0.0, z); // ReLU
                }
                double pred = b2;
                for (int h = 0; h < H; h++) pred += W2[h] * hidden[h];

                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                double hGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);

                // Backward — output layer
                Array.Clear(dW1, 0, dW1.Length);
                Array.Clear(dB1, 0, H);
                for (int h = 0; h < H; h++) dW2[h] = hGrad * hidden[h];
                double dB2local = hGrad;

                // Backward — hidden layer (ReLU derivative)
                for (int h = 0; h < H; h++)
                {
                    double dZ = hGrad * W2[h] * (hidden[h] > 0 ? 1.0 : 0.0);
                    dB1[h] = dZ;
                    for (int j = 0; j < F && j < s.Features.Length; j++)
                        dW1[h * F + j] = dZ * s.Features[j];
                }

                // Adam updates
                for (int i = 0; i < H * F; i++)
                {
                    double g = dW1[i] + l2 * W1[i];
                    mW1[i] = AdamBeta1 * mW1[i] + (1 - AdamBeta1) * g;
                    vW1[i] = AdamBeta2 * vW1[i] + (1 - AdamBeta2) * g * g;
                    W1[i] -= alphAt * mW1[i] / (Math.Sqrt(vW1[i]) + AdamEpsilon);
                }
                for (int h = 0; h < H; h++)
                {
                    mB1[h] = AdamBeta1 * mB1[h] + (1 - AdamBeta1) * dB1[h];
                    vB1[h] = AdamBeta2 * vB1[h] + (1 - AdamBeta2) * dB1[h] * dB1[h];
                    b1[h] -= alphAt * mB1[h] / (Math.Sqrt(vB1[h]) + AdamEpsilon);
                }
                for (int h = 0; h < H; h++)
                {
                    double g = dW2[h] + l2 * W2[h];
                    mW2[h] = AdamBeta1 * mW2[h] + (1 - AdamBeta1) * g;
                    vW2[h] = AdamBeta2 * vW2[h] + (1 - AdamBeta2) * g * g;
                    W2[h] -= alphAt * mW2[h] / (Math.Sqrt(vW2[h]) + AdamEpsilon);
                }
                mB2 = AdamBeta1 * mB2 + (1 - AdamBeta1) * dB2local;
                vB2 = AdamBeta2 * vB2 + (1 - AdamBeta2) * dB2local * dB2local;
                b2 -= alphAt * mB2 / (Math.Sqrt(vB2) + AdamEpsilon);
            }

            if (!canEarlyStop) continue;

            // #42: Reuse a single hidden-activation buffer across validation samples
            double valLoss = 0.0;
            var valHidden = new double[H];
            foreach (var s in valSlice)
            {
                for (int h = 0; h < H; h++)
                {
                    double z = b1[h];
                    for (int j = 0; j < F && j < s.Features.Length; j++)
                        z += W1[h * F + j] * s.Features[j];
                    valHidden[h] = Math.Max(0.0, z);
                }
                double p2 = b2;
                for (int h = 0; h < H; h++) p2 += W2[h] * valHidden[h];
                double e = p2 - s.Magnitude;
                valLoss += Math.Abs(e) <= 1.0 ? 0.5 * e * e : Math.Abs(e) - 0.5;
            }
            valLoss /= valSlice.Count;

            if (valLoss < bestVal)
            {
                bestVal = valLoss;
                Array.Copy(W1, bW1, W1.Length);
                Array.Copy(b1, bB1, H);
                Array.Copy(W2, bW2, H);
                bB2     = b2;
                patience = 0;
            }
            else if (++patience >= esPatience)
                break;
        }

        if (canEarlyStop) { W1 = bW1; b1 = bB1; W2 = bW2; b2 = bB2; }
        return (W1, b1, W2, b2);
    }

    // ── Quantile magnitude regressor (pinball loss, SGD) ─────────────────────

    /// <summary>
    /// #40: Quantile regressor with early stopping on a validation split.
    /// #41: L2 regularisation via <paramref name="l2"/>.
    /// </summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train, int F, double tau,
        double l2 = 0.0, int earlyStopPatience = 0)
    {
        var    w  = new double[F];
        double b  = 0;
        const double sgdLr = 0.001;
        const int    epochs = 100;

        bool canEarlyStop = earlyStopPatience > 0 && train.Count >= 30;
        int  valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var  valSlice     = canEarlyStop ? train[^valSize..] : train;
        var  trainSlice   = canEarlyStop ? train[..^valSize] : train;

        var    bestW = new double[F];
        double bestB = 0, bestVal = double.MaxValue;
        int    patience = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            foreach (var s in trainSlice)
            {
                double pred = b;
                for (int j = 0; j < F && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err  = s.Magnitude - pred;
                double grad = err >= 0 ? tau : -(1 - tau);
                b += sgdLr * grad;
                for (int j = 0; j < F && j < s.Features.Length; j++)
                    w[j] += sgdLr * (grad * s.Features[j] - l2 * w[j]);
            }

            if (!canEarlyStop) continue;

            double valLoss = 0;
            foreach (var s in valSlice)
            {
                double pred = b;
                for (int j = 0; j < F && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = s.Magnitude - pred;
                valLoss += err >= 0 ? tau * err : (tau - 1) * err;
            }
            valLoss /= valSlice.Count;

            if (valLoss < bestVal)
            {
                bestVal = valLoss;
                Array.Copy(w, bestW, F);
                bestB    = b;
                patience = 0;
            }
            else if (++patience >= earlyStopPatience)
                break;
        }

        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ── Calibration confidence interval (#8) ──────────────────────────────────

    /// <summary>
    /// #8: Bootstrap the calibration set to estimate uncertainty of Platt parameters.
    /// Returns (stdA, stdB) — the standard deviations of A and B across resamples.
    /// </summary>
    private static (double StdA, double StdB) ComputeCalibrationCI(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  bootstrapRounds = 50,
        int                  seed = 42)
    {
        if (calSet.Count < MinCalSamplesPlatt) return (0, 0);

        var aValues = new double[bootstrapRounds];
        var bValues = new double[bootstrapRounds];
        var rng     = new Random(seed);

        for (int r = 0; r < bootstrapRounds; r++)
        {
            var resample = new List<TrainingSample>(calSet.Count);
            for (int i = 0; i < calSet.Count; i++)
                resample.Add(calSet[rng.Next(calSet.Count)]);
            var (a, b) = FitPlattScaling(resample, allTrees, trainSet);
            aValues[r] = a;
            bValues[r] = b;
        }

        return (StdDevArr(aValues), StdDevArr(bValues));

        static double StdDevArr(double[] values)
        {
            double mean = values.Average();
            double varSum = 0;
            foreach (var v in values) varSum += (v - mean) * (v - mean);
            return values.Length > 1 ? Math.Sqrt(varSum / (values.Length - 1)) : 0;
        }
    }
}
