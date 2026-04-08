using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateTabNet(
        IReadOnlyList<TrainingSample> evalSet, TabNetWeights w,
        double plattA, double plattB, double[] magWeights, double magBias, int origF)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;
        double weightSum = 0, correctWeighted = 0;
        int n = evalSet.Count;
        double evSum = 0;
        var returns = new List<double>(n);

        for (int idx = 0; idx < n; idx++)
        {
            var s   = evalSet[idx];
            double p = TabNetCalibProb(s.Features, w, plattA, plattB);
            int yHat = p >= 0.5 ? 1 : 0;
            int y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);

            double sign = (yHat == y) ? 1.0 : -1.0;
            double ret  = sign * Math.Abs(s.Magnitude);
            evSum += ret;
            returns.Add(ret);

            if (magWeights.Length > 0)
            {
                double pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    pred += magWeights[j] * s.Features[j];
                magSse += (pred - s.Magnitude) * (pred - s.Magnitude);
            }

            double wt = 1.0 + (double)idx / n;
            weightSum += wt;
            if (yHat == y) correctWeighted += wt;
        }

        double accuracy  = n > 0 ? (double)correct / n : 0;
        double brier     = n > 0 ? brierSum / n : 1;
        double magRmse   = n > 0 && magSse > 0 ? Math.Sqrt(magSse / n) : 0;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = n > 0 ? evSum / n : 0;
        double wAcc      = weightSum > 0 ? correctWeighted / weightSum : accuracy;

        double avgRet = returns.Count > 0 ? returns.Average() : 0;
        double stdRet = returns.Count > 1 ? StdDev(returns, avgRet) : 0;
        double sharpe = stdRet > 1e-10 ? avgRet / stdRet * Math.Sqrt(252) : 0;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: wAcc, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PERMUTATION FEATURE IMPORTANCE
    // ═══════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        IReadOnlyList<TrainingSample> testSet, TabNetWeights w, double plattA, double plattB, CancellationToken ct)
    {
        int n = testSet.Count, F = w.F;
        double baseline = 0;
        foreach (var s in testSet)
            if ((TabNetCalibProb(s.Features, w, plattA, plattB) >= 0.5) == (s.Direction > 0)) baseline++;
        baseline /= n;
        var importance = new float[F];
        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(j * 13 + 42);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = testSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                var scratch = (float[])testSet[idx].Features.Clone();
                scratch[j] = vals[idx];
                if ((TabNetCalibProb(scratch, w, plattA, plattB) >= 0.5) == (testSet[idx].Direction > 0)) correct++;
            }
            importance[j] = (float)Math.Max(0, baseline - (double)correct / n);
        });
        float total = importance.Sum();
        if (total > 1e-6f) for (int j = 0; j < F; j++) importance[j] /= total;
        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, CancellationToken ct)
    {
        int n = calSet.Count, F = w.F;
        double baseAcc = 0;
        foreach (var s in calSet)
            if ((TabNetRawProb(s.Features, w) >= 0.5) == (s.Direction > 0)) baseAcc++;
        baseAcc /= n;
        var importance = new double[F];
        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(j * 17 + 7);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                var scratch = (float[])calSet[idx].Features.Clone();
                scratch[j] = vals[idx];
                if ((TabNetRawProb(scratch, w) >= 0.5) == (calSet[idx].Direction > 0)) correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FUSED ATTENTION ANALYSIS (single forward-pass loop)
    //  Computes mean attention, per-step attention, and attention entropy
    //  in one pass — eliminates 2× redundant forward passes.
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] MeanAttn, double[][] PerStepAttn, double[] Entropy) ComputeAttentionStats(
        IReadOnlyList<TrainingSample> samples, TabNetWeights w)
    {
        int F = w.F, nSteps = w.NSteps;
        int count = Math.Min(samples.Count, MeanAttentionSampleCap);

        var meanAttn = new double[F];
        var perStep  = new double[nSteps][];
        for (int s = 0; s < nSteps; s++) perStep[s] = new double[F];
        var entropy = new double[nSteps];

        var priorBuf = new double[F];
        var attnBuf  = new double[F];

        for (int i = 0; i < count; i++)
        {
            var fwd = ForwardPass(samples[i].Features, w, priorBuf, attnBuf, false, 0, null);

            for (int s = 0; s < nSteps; s++)
            {
                double h = 0;
                for (int j = 0; j < F && j < fwd.StepAttn[s].Length; j++)
                {
                    double a = fwd.StepAttn[s][j];
                    meanAttn[j]    += a / (nSteps * count);
                    perStep[s][j]  += a / count;
                    if (a > Eps) h -= a * Math.Log(a);
                }
                entropy[s] += h / count;
            }
        }

        return (meanAttn, perStep, entropy);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CONFORMAL / JACKKNIFE / META-LABEL / ABSTENTION / QUANTILE /
    //  BOUNDARY / DURBIN-WATSON / MI / MAGNITUDE REGRESSOR
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB,
        double[] isotonicBp, double alpha)
    {
        if (calSet.Count < MinCalibrationSamples) return 0.5;
        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = TabNetCalibProb(calSet[i].Features, w, plattA, plattB);
            if (isotonicBp.Length >= 2) p = ApplyIsotonic(p, isotonicBp);
            int y = calSet[i].Direction > 0 ? 1 : 0;
            scores[i] = 1.0 - (y == 1 ? p : 1.0 - p);
        }
        Array.Sort(scores);
        int qIdx = Math.Clamp((int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1, 0, calSet.Count - 1);
        return scores[qIdx];
    }

    /// <summary>
    /// Jackknife+ residuals using the infinitesimal jackknife approximation.
    /// For each training sample i, estimates the LOO prediction p_{-i} via:
    ///   p_{-i} ≈ p_full + h_ii / (1 - h_ii) × (p_full - y_i)
    /// where h_ii is approximated by the squared gradient norm of sample i
    /// divided by the mean squared gradient norm (a leverage proxy).
    /// This avoids n full retraining runs while producing proper LOO residuals.
    /// </summary>
    private double[] ComputeJackknifeResiduals(IReadOnlyList<TrainingSample> trainSet, TabNetWeights w)
    {
        int n = trainSet.Count;
        if (n == 0) return [];

        var priorBuf = new double[w.F];
        var attnBuf  = new double[w.F];

        // Pass 1: compute full-model predictions and per-sample gradient norms (leverage proxy)
        var probs     = new double[n];
        var gradNorms = new double[n];

        for (int i = 0; i < n; i++)
        {
            var fwd = ForwardPass(trainSet[i].Features, w, priorBuf, attnBuf, false, 0, null);
            probs[i] = fwd.Prob;

            // Approximate leverage h_ii via ||∂loss/∂output||² for this sample
            // The output-layer gradient norm captures how influential this sample is
            double y = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            double errCE = fwd.Prob - y;
            double sqNorm = 0;
            for (int j = 0; j < w.HiddenDim && j < w.OutputW.Length; j++)
            {
                double g = errCE * fwd.AggregatedH[j];
                sqNorm += g * g;
            }
            sqNorm += errCE * errCE; // bias gradient
            gradNorms[i] = sqNorm;
        }

        // Mean gradient norm for normalization
        double meanGradNorm = 0;
        for (int i = 0; i < n; i++) meanGradNorm += gradNorms[i];
        meanGradNorm /= n;

        // Pass 2: compute LOO-adjusted residuals
        var residuals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double y = trainSet[i].Direction > 0 ? 1.0 : 0.0;

            // h_ii ≈ gradNorm_i / (n × meanGradNorm), clamped to [0, 0.9]
            double hii = meanGradNorm > Eps
                ? Math.Min(gradNorms[i] / (n * meanGradNorm), 0.9)
                : 0.0;

            // LOO prediction: p_{-i} ≈ p + h/(1-h) × (p - y)
            double looCorrection = hii / (1.0 - hii) * (probs[i] - y);
            double pLoo = Math.Clamp(probs[i] + looCorrection, ProbClampMin, 1.0 - ProbClampMin);

            residuals[i] = Math.Abs(pLoo - y);
        }

        return residuals;
    }

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        if (calSet.Count < MinCalibrationSamples) return ([0.0], 0.0);
        int n = calSet.Count;
        double metaW = 0.0, metaB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0; int t = 0;
        for (int ep = 0; ep < CalibrationEpochs; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double p = TabNetRawProb(calSet[i].Features, w);
                int correct = ((p >= 0.5) == (calSet[i].Direction > 0)) ? 1 : 0;
                double metaP = Sigmoid(metaW * p + metaB);
                dW += (metaP - correct) * p; dB += metaP - correct;
            }
            t++; double bc1 = 1.0 - Math.Pow(AdamBeta1, t), bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n; vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n; vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            metaW -= CalibrationLr * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon);
            metaB -= CalibrationLr * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }
        return ([metaW], metaB);
    }

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB)
    {
        if (calSet.Count < MinCalibrationSamples) return ([0.0], 0.0, 0.5);
        int n = calSet.Count;
        var probs = new double[n];
        for (int i = 0; i < n; i++) probs[i] = TabNetCalibProb(calSet[i].Features, w, plattA, plattB);
        double absW = 0.0, absB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0; int t = 0;
        for (int ep = 0; ep < CalibrationEpochs; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double feat = Math.Abs(probs[i] - 0.5);
                int correct = ((probs[i] >= 0.5) == (calSet[i].Direction > 0)) ? 1 : 0;
                double abstP = Sigmoid(absW * feat + absB);
                dW += (abstP - correct) * feat; dB += abstP - correct;
            }
            t++; double bc1 = 1.0 - Math.Pow(AdamBeta1, t), bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n; vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n; vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            absW -= CalibrationLr * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon);
            absB -= CalibrationLr * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }
        double bestPrec = 0, bestThresh = 0.1;
        for (int ti = 1; ti <= 40; ti++)
        {
            double thresh = ti / 100.0; int tpA = 0, fpA = 0;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(probs[i] - 0.5) < thresh) continue;
                if ((probs[i] >= 0.5) == (calSet[i].Direction > 0)) tpA++; else fpA++;
            }
            double prec = (tpA + fpA) > 0 ? (double)tpA / (tpA + fpA) : 0;
            if (prec > bestPrec) { bestPrec = prec; bestThresh = thresh; }
        }
        return ([absW], absB, bestThresh);
    }

    private static (double[] Weights, double Bias) FitQuantileRegressor(IReadOnlyList<TrainingSample> trainSet, int F, double tau)
    {
        if (trainSet.Count < MinCalibrationSamples) return (new double[F], 0.0);
        int n = trainSet.Count; var qw = new double[F]; double b = 0.0;
        for (int ep = 0; ep < 100; ep++)
            for (int i = 0; i < n; i++)
            {
                double pred = b;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) pred += qw[j] * trainSet[i].Features[j];
                double grad = (trainSet[i].Magnitude - pred) >= 0 ? -tau : (1.0 - tau);
                b -= 0.001 * grad;
                b = Math.Clamp(b, -MaxWeightVal, MaxWeightVal);
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                {
                    qw[j] -= 0.001 * grad * trainSet[i].Features[j];
                    qw[j] = Math.Clamp(qw[j], -MaxWeightVal, MaxWeightVal);
                }
            }
        return (qw, b);
    }

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        var distances = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++) distances[i] = Math.Abs(TabNetRawProb(calSet[i].Features, w) - 0.5);
        double mean = distances.Average();
        return (mean, StdDev(distances.ToList(), mean));
    }

    private static double ComputeDurbinWatson(IReadOnlyList<TrainingSample> trainSet, double[] magWeights, double magBias, int F)
    {
        if (trainSet.Count < MinCalibrationSamples || magWeights.Length == 0) return 2.0;
        int n = trainSet.Count; var residuals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(F, magWeights.Length) && j < trainSet[i].Features.Length; j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) den += residuals[i] * residuals[i];
        for (int i = 1; i < n; i++) { double d = residuals[i] - residuals[i - 1]; num += d * d; }
        return den > Eps ? num / den : 2.0;
    }

    private static string[] ComputeRedundantFeaturePairs(IReadOnlyList<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 30 || F < 2) return [];
        int n = Math.Min(trainSet.Count, MeanAttentionSampleCap), numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));
        var redundant = new List<string>();
        for (int a = 0; a < F; a++)
            for (int b = a + 1; b < F; b++)
            {
                var vA = new double[n]; var vB = new double[n];
                for (int i = 0; i < n; i++) { vA[i] = trainSet[i].Features[a]; vB[i] = trainSet[i].Features[b]; }
                double mi = ComputeMI(vA, vB, numBins), hA = ComputeEntropy(vA, numBins), hB = ComputeEntropy(vB, numBins);
                double norm = Math.Max(hA, hB);
                if (norm > 1e-10 && mi / norm > threshold)
                {
                    string nA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nB = b < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[b] : $"F{b}";
                    redundant.Add($"{nA}\u2194{nB}:{mi / norm:F2}");
                }
            }
        return redundant.ToArray();
    }

    private static (double[] Weights, double Bias) FitLinearRegressor(List<TrainingSample> train, int featureCount, TrainingHyperparams hp)
    {
        var lw = new double[featureCount]; double b = 0.0;
        bool canEarlyStop = train.Count >= 30;
        int valSize = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var valSet = canEarlyStop ? train[^valSize..] : train;
        var trainSet = canEarlyStop ? train[..^valSize] : train;
        if (trainSet.Count == 0) return (lw, b);
        var mW = new double[featureCount]; var vW = new double[featureCount];
        double mB = 0.0, vB = 0.0, beta1t = 1.0, beta2t = 1.0; int t = 0;
        double bestValLoss = double.MaxValue; var bestW = new double[featureCount]; double bestB = 0.0; int patience = 0;
        int epochs = hp.MaxEpochs; double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.1, l2 = hp.L2Lambda;
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));
            foreach (var s in trainSet)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += lw[j] * s.Features[j];
                double err = pred - s.Magnitude; if (!double.IsFinite(err)) continue;
                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                double bc1 = 1.0 - beta1t, bc2 = 1.0 - beta2t, alphat = alpha * Math.Sqrt(bc2) / bc1;
                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * huberGrad; vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * lw[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g; vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    lw[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0.0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += lw[j] * s.Features[j];
                double err = pred - s.Magnitude; if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5; valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;
            if (valLoss < bestValLoss - EarlyStopMinDelta) { bestValLoss = valLoss; Array.Copy(lw, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= hp.EarlyStoppingPatience) break;
        }
        if (canEarlyStop) { lw = bestW; b = bestB; }
        return (lw, b);
    }
}
