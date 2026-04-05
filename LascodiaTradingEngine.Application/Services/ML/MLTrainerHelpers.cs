using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Shared pure-math helpers used by multiple <see cref="IMLModelTrainer"/> implementations.
/// All methods are stateless, taking pre-computed probability arrays and sample lists.
/// Trainers compute model-specific predictions, then delegate to these helpers for
/// calibration, evaluation, importance weighting, and diagnostics.
/// </summary>
internal static class MLTrainerHelpers
{
    // ═════════════════════════════════════════════════════════════════════════
    // MATH PRIMITIVES
    // ═════════════════════════════════════════════════════════════════════════

    internal static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -500, 500)));
    internal static double Logit(double p)   => Math.Log(p / (1.0 - p));

    // ═════════════════════════════════════════════════════════════════════════
    // CALIBRATION STACK
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Platt scaling via Adam-optimised logistic regression with L2 and early stopping.</summary>
    internal static (double A, double B) FitPlatt(float[] rawProbs, List<TrainingSample> samples)
    {
        if (samples.Count < 5) return (1.0, 0.0);

        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;

        double[] logits = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            logits[i] = Logit(Math.Clamp(rawProbs[i], 1e-7, 1.0 - 1e-7));

        double a = 1.0, b = 0.0, bestA = 1.0, bestB = 0.0, bestLoss = double.MaxValue;
        double mA = 0, mB = 0, vA = 0, vB = 0;
        const double lr = 0.01, l2 = 1e-4;
        const int maxEpochs = 300, patience = 30;
        int noImprove = 0;

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            double gradA = 0, gradB = 0, loss = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                double p   = Sigmoid(a * logits[i] + b);
                double y   = samples[i].Direction > 0 ? 1.0 : 0.0;
                double err = p - y;
                gradA += err * logits[i];
                gradB += err;
                loss  -= y * Math.Log(Math.Max(p, 1e-10))
                       + (1 - y) * Math.Log(Math.Max(1 - p, 1e-10));
            }
            int t = epoch + 1;
            gradA = gradA / samples.Count + 2.0 * l2 * a;
            gradB = gradB / samples.Count + 2.0 * l2 * b;
            loss  = loss / samples.Count + l2 * (a * a + b * b);

            mA = beta1 * mA + (1 - beta1) * gradA;
            mB = beta1 * mB + (1 - beta1) * gradB;
            vA = beta2 * vA + (1 - beta2) * gradA * gradA;
            vB = beta2 * vB + (1 - beta2) * gradB * gradB;
            a -= lr * (mA / (1 - Math.Pow(beta1, t))) / (Math.Sqrt(vA / (1 - Math.Pow(beta2, t))) + eps);
            b -= lr * (mB / (1 - Math.Pow(beta1, t))) / (Math.Sqrt(vB / (1 - Math.Pow(beta2, t))) + eps);

            if (loss < bestLoss - 1e-7) { bestLoss = loss; bestA = a; bestB = b; noImprove = 0; }
            else if (++noImprove >= patience) break;
        }
        return (bestA, bestB);
    }

    /// <summary>Separate Platt parameters for Buy (p≥0.5) and Sell (p&lt;0.5) samples.</summary>
    internal static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        float[] rawProbs, List<TrainingSample> calSet)
    {
        const double lr     = 0.01;
        const int    epochs = 200;

        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();

        for (int i = 0; i < calSet.Count; i++)
        {
            double rawP  = Math.Clamp(rawProbs[i], 1e-7, 1.0 - 1e-7);
            double logit = Logit(rawP);
            double y     = calSet[i].Direction > 0 ? 1.0 : 0.0;
            if (rawP >= 0.5) buySamples.Add((logit, y));
            else             sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs)
        {
            if (pairs.Count < 5) return (1.0, 0.0);
            double a = 1.0, b = 0.0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0, dB = 0;
                foreach (var (logit, y) in pairs)
                {
                    double p   = Sigmoid(a * logit + b);
                    double err = p - y;
                    dA += err * logit;
                    dB += err;
                }
                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;
            }
            return (a, b);
        }

        var (aBuy,  bBuy)  = FitSgd(buySamples);
        var (aSell, bSell) = FitSgd(sellSamples);
        return (aBuy, bBuy, aSell, bSell);
    }

    /// <summary>Apply Platt scaling: logit → affine → sigmoid.</summary>
    internal static float[] CalibratePlatt(float[] rawProbs, double plattA, double plattB)
    {
        var result = new float[rawProbs.Length];
        for (int i = 0; i < rawProbs.Length; i++)
        {
            double logit = Logit(Math.Clamp(rawProbs[i], 1e-7, 1.0 - 1e-7));
            result[i] = (float)Sigmoid(plattA * logit + plattB);
        }
        return result;
    }

    /// <summary>Isotonic calibration via pool-adjacent-violators (PAVA). Returns flat breakpoint array [x0,y0,x1,y1,...].</summary>
    internal static double[] FitIsotonicCalibration(float[] calibProbs, List<TrainingSample> calSet)
    {
        if (calSet.Count < 10) return [];

        int cn = calSet.Count;
        var pairs = new (double P, double Y)[cn];
        for (int i = 0; i < cn; i++)
            pairs[i] = (Math.Clamp(calibProbs[i], 1e-7, 1.0 - 1e-7), calSet[i].Direction > 0 ? 1.0 : 0.0);
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        var stack = new List<(double SumY, double SumP, int Count)>(cn);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var last = stack[^1]; var prev = stack[^2];
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY, prev.SumP + last.SumP, prev.Count + last.Count);
                }
                else break;
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

    /// <summary>Piecewise-linear interpolation through isotonic breakpoints.</summary>
    internal static double ApplyIsotonicCalibration(double p, double[] breakpoints)
    {
        if (breakpoints.Length < 4) return p;
        int nPoints = breakpoints.Length / 2;
        if (p <= breakpoints[0])                 return breakpoints[1];
        if (p >= breakpoints[(nPoints - 1) * 2]) return breakpoints[(nPoints - 1) * 2 + 1];
        int lo = 0, hi = nPoints - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (breakpoints[(mid + 1) * 2] <= p) lo = mid + 1;
            else hi = mid;
        }
        double x0 = breakpoints[lo * 2],       y0 = breakpoints[lo * 2 + 1];
        double x1 = breakpoints[(lo + 1) * 2], y1 = breakpoints[(lo + 1) * 2 + 1];
        return Math.Abs(x1 - x0) < 1e-15 ? (y0 + y1) / 2.0 : y0 + (p - x0) * (y1 - y0) / (x1 - x0);
    }

    /// <summary>Batch isotonic calibration.</summary>
    internal static float[] ApplyIsotonicArray(float[] probs, double[] breakpoints)
    {
        if (breakpoints.Length < 4) return probs;
        var result = new float[probs.Length];
        for (int i = 0; i < probs.Length; i++)
            result[i] = (float)ApplyIsotonicCalibration(probs[i], breakpoints);
        return result;
    }

    /// <summary>Grid-search temperature scaling to minimise NLL on calibration set.</summary>
    internal static double FitTemperatureScaling(float[] isoCalProbs, List<TrainingSample> calSet)
    {
        if (calSet.Count < 10) return 1.0;
        var logits = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            logits[i] = Logit(Math.Clamp(isoCalProbs[i], 1e-7, 1.0 - 1e-7));

        double Nll(double T)
        {
            double loss = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double p = Sigmoid(logits[i] / T);
                double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
                loss -= y * Math.Log(Math.Max(p, 1e-10)) + (1.0 - y) * Math.Log(Math.Max(1.0 - p, 1e-10));
            }
            return loss / calSet.Count;
        }

        double bestT = 1.0, bestNll = Nll(1.0);
        for (int i = 1; i <= 100; i++)
        {
            double T   = 0.1 + i * 0.099;
            double nll = Nll(T);
            if (nll < bestNll) { bestNll = nll; bestT = T; }
        }
        return Math.Round(bestT, 4);
    }

    /// <summary>Full calibration pipeline: Platt → Isotonic → Temperature.</summary>
    internal static float[] ApplyFullCalibration(
        float[] rawProbs, double plattA, double plattB,
        double[] isotonicBp, double tempScale)
    {
        var result = new float[rawProbs.Length];
        for (int i = 0; i < rawProbs.Length; i++)
        {
            double p = Sigmoid(plattA * Logit(Math.Clamp(rawProbs[i], 1e-7, 1.0 - 1e-7)) + plattB);
            if (isotonicBp.Length >= 4)
                p = ApplyIsotonicCalibration(p, isotonicBp);
            if (Math.Abs(tempScale - 1.0) > 1e-6 && tempScale > 0.0)
                p = Sigmoid(Logit(Math.Clamp(p, 1e-7, 1.0 - 1e-7)) / tempScale);
            result[i] = (float)Math.Clamp(p, 1e-7, 1.0 - 1e-7);
        }
        return result;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // EVALUATION METRICS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Half-Kelly fraction averaged over the calibration set.</summary>
    internal static double ComputeAvgKellyFraction(float[] calibProbs, List<TrainingSample> calSet)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0.0;
        for (int i = 0; i < calSet.Count; i++)
            sum += Math.Max(0.0, 2.0 * calibProbs[i] - 1.0);
        return sum / calSet.Count * 0.5;
    }

    /// <summary>Expected Calibration Error (equal-width bins).</summary>
    internal static double ComputeEce(float[] calibProbs, List<TrainingSample> samples, int bins = 10)
    {
        if (samples.Count == 0) return 0;
        int[] cnt = new int[bins];
        double[] sumAcc = new double[bins], sumConf = new double[bins];
        for (int i = 0; i < samples.Count; i++)
        {
            double p = calibProbs[i];
            int    b = Math.Clamp((int)(p * bins), 0, bins - 1);
            cnt[b]++;
            sumAcc[b]  += samples[i].Direction > 0 ? 1.0 : 0.0;
            sumConf[b] += p;
        }
        double ece = 0;
        for (int b = 0; b < bins; b++)
        {
            if (cnt[b] == 0) continue;
            ece += Math.Abs(sumAcc[b] / cnt[b] - sumConf[b] / cnt[b]) * cnt[b];
        }
        return ece / samples.Count;
    }

    /// <summary>Brier Skill Score relative to naive base-rate.</summary>
    internal static double ComputeBss(float[] calibProbs, List<TrainingSample> samples)
    {
        if (samples.Count == 0) return 0;
        int pos = samples.Count(s => s.Direction > 0);
        double naiveP = (double)pos / samples.Count;
        double brierModel = 0, brierNaive = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double p = calibProbs[i], y = samples[i].Direction > 0 ? 1.0 : 0.0;
            brierModel += (p - y) * (p - y);
            brierNaive += (naiveP - y) * (naiveP - y);
        }
        return brierNaive > 1e-10 ? 1.0 - brierModel / brierNaive : 0.0;
    }

    /// <summary>EV-optimal threshold via grid search over calibration set.</summary>
    internal static double ComputeOptimalThreshold(
        float[] calibProbs, List<TrainingSample> samples, int minPct, int maxPct)
    {
        if (samples.Count == 0) return 0.5;
        double bestEV = double.NegativeInfinity, bestThr = 0.5;
        for (int tPct = minPct; tPct <= maxPct; tPct += 2)
        {
            double thr = tPct / 100.0;
            double evWin = 0, evLoss = 0;
            int traded = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                double p = calibProbs[i];
                if (p < thr && p > 1.0 - thr) continue;
                traded++;
                double mag = Math.Max(0.001, Math.Abs(samples[i].Magnitude));
                if ((p >= 0.5 ? 1 : 0) == samples[i].Direction) evWin += mag;
                else evLoss += mag;
            }
            if (traded == 0) continue;
            double ev = (evWin - evLoss) / traded;
            if (ev > bestEV) { bestEV = ev; bestThr = thr; }
        }
        return bestThr;
    }

    /// <summary>Full evaluation metrics: accuracy, precision, recall, F1, EV, Brier, Sharpe.</summary>
    internal static EvalMetrics ComputeFullMetrics(
        float[] calibProbs, List<TrainingSample> samples, double threshold, double sharpeAnnFactor)
    {
        if (samples.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, evWin = 0, evLoss = 0, magSse = 0;
        var returns = new double[samples.Count];

        for (int i = 0; i < samples.Count; i++)
        {
            double p      = calibProbs[i];
            double y      = samples[i].Direction > 0 ? 1.0 : 0.0;
            int    pred   = p >= threshold ? 1 : 0;
            double absMag = Math.Max(0.001, Math.Abs(samples[i].Magnitude));

            if (pred == samples[i].Direction) correct++;
            if (pred == 1 && samples[i].Direction == 1) tp++;
            if (pred == 1 && samples[i].Direction == 0) fp++;
            if (pred == 0 && samples[i].Direction == 1) fn++;
            if (pred == 0 && samples[i].Direction == 0) tn++;

            brierSum += (p - y) * (p - y);
            if (pred == samples[i].Direction) evWin += absMag; else evLoss += absMag;
            magSse  += samples[i].Magnitude * samples[i].Magnitude;
            returns[i] = (pred == 1 ? 1 : -1) * (samples[i].Direction > 0 ? 1 : -1) * absMag;
        }

        int ne = samples.Count;
        double acc  = (double)correct / ne;
        double prec = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double rec  = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1   = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
        double ev   = (evWin - evLoss) / ne;
        double sharpe = ComputeSharpe(returns, sharpeAnnFactor);
        return new EvalMetrics(acc, prec, rec, f1, Math.Sqrt(magSse / ne), ev, brierSum / ne, acc, sharpe, tp, fp, fn, tn);
    }

    /// <summary>Annualised Sharpe ratio.</summary>
    internal static double ComputeSharpe(double[] returns, double annFactor)
    {
        if (returns.Length < 2) return 0;
        double mean = returns.Average();
        double std  = Math.Sqrt(returns.Average(r => (r - mean) * (r - mean)));
        return std > 1e-10 ? mean / std * Math.Sqrt(annFactor) : 0;
    }

    /// <summary>Maximum drawdown of a signal-following equity curve.</summary>
    internal static double ComputeMaxDrawdown(float[] probs, List<TrainingSample> samples)
    {
        double peak = 0, equity = 0, maxDD = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double ret = (probs[i] >= 0.5 ? 1 : -1)
                       * (samples[i].Direction > 0 ? 1 : -1)
                       * Math.Max(0.001, Math.Abs(samples[i].Magnitude));
            equity += ret;
            if (equity > peak) peak = equity;
            if (peak > 0) { double dd = (peak - equity) / peak; if (dd > maxDD) maxDD = dd; }
        }
        return maxDD;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // META-LABEL & ABSTENTION
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logistic meta-label predicting whether the primary model's prediction is correct.</summary>
    internal static (double[] Weights, double Bias) FitMetaLabelModel(
        float[] calibProbs, float[] gpVars, List<TrainingSample> calSet, int F, int metaDim)
    {
        const int    Epochs = 50;
        const double Lr     = 0.01;
        const double L2     = 0.001;

        if (calSet.Count < 10) return (new double[metaDim], 0.0);

        var    mw = new double[metaDim];
        double mb = 0.0;

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            var    dW = new double[metaDim];
            double dB = 0;

            for (int i = 0; i < calSet.Count; i++)
            {
                var    s      = calSet[i];
                double calibP = Math.Clamp(calibProbs[i], 1e-7, 1.0 - 1e-7);
                double gpStd  = Math.Sqrt(Math.Max(gpVars[i], 0.0));

                var feat = new double[metaDim];
                feat[0] = calibP;
                feat[1] = gpStd;
                int topF = Math.Min(F, metaDim - 2);
                for (int j = 0; j < topF; j++) feat[2 + j] = s.Features[j];

                double z    = mb;
                for (int j = 0; j < metaDim; j++) z += mw[j] * feat[j];
                double pred = Sigmoid(z);
                double lbl  = (calibP >= 0.5) == (s.Direction == 1) ? 1.0 : 0.0;
                double err  = pred - lbl;

                for (int j = 0; j < metaDim; j++) dW[j] += err * feat[j];
                dB += err;
            }

            int n = calSet.Count;
            for (int j = 0; j < metaDim; j++)
                mw[j] -= Lr * (dW[j] / n + L2 * mw[j]);
            mb -= Lr * dB / n;
        }

        return (mw, mb);
    }

    /// <summary>3-feature logistic abstention gate: [calibP, gpStd, metaScore].</summary>
    internal static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        float[]              calibProbs,
        float[]              gpVars,
        List<TrainingSample> calSet,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        int                  F,
        int                  metaDim)
    {
        const int    Dim    = 3;
        const int    Epochs = 50;
        const double Lr     = 0.01;
        const double L2     = 0.001;

        if (calSet.Count < 10)
            return (new double[Dim], 0.0, 0.5);

        var    aw = new double[Dim];
        double ab = 0.0;

        var dW = new double[Dim];
        var mf = new double[metaDim];
        var af = new double[Dim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, Dim);

            for (int i = 0; i < calSet.Count; i++)
            {
                var    s      = calSet[i];
                double calibP = Math.Clamp(calibProbs[i], 1e-7, 1.0 - 1e-7);
                double gpStd  = Math.Sqrt(Math.Max(gpVars[i], 0.0));

                mf[0] = calibP; mf[1] = gpStd;
                int topF = Math.Min(F, metaDim - 2);
                for (int j = 0; j < topF; j++) mf[2 + j] = s.Features[j];
                double mz = metaLabelBias;
                for (int j = 0; j < metaDim && j < metaLabelWeights.Length; j++)
                    mz += metaLabelWeights[j] * mf[j];
                double metaScore = Sigmoid(mz);

                af[0] = calibP; af[1] = gpStd; af[2] = metaScore;
                double lbl = (calibP >= 0.5) == (s.Direction == 1) ? 1.0 : 0.0;

                double z    = ab;
                for (int j = 0; j < Dim; j++) z += aw[j] * af[j];
                double pred = Sigmoid(z);
                double err  = pred - lbl;

                for (int j = 0; j < Dim; j++) dW[j] += err * af[j];
                dB += err;
            }

            int n = calSet.Count;
            for (int j = 0; j < Dim; j++)
                aw[j] -= Lr * (dW[j] / n + L2 * aw[j]);
            ab -= Lr * dB / n;
        }

        return (aw, ab, 0.5);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // MAGNITUDE REGRESSORS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Huber-loss linear regressor with Adam, cosine LR, and early stopping.</summary>
    internal static (double[] Weights, double Bias) FitMagnitudeRegressor(
        List<TrainingSample> trainSet, int F)
    {
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;

        if (trainSet.Count < F + 2) return (new double[F], 0.0);

        bool   canEarlyStop = trainSet.Count >= 30;
        int    valSize      = canEarlyStop ? Math.Max(5, trainSet.Count / 10) : 0;
        var    valSet       = canEarlyStop ? trainSet[^valSize..] : trainSet;
        var    train        = canEarlyStop ? trainSet[..^valSize] : trainSet;

        if (train.Count == 0) return (new double[F], 0.0);

        double[] w = new double[F], mW = new double[F], vW = new double[F];
        double   b = 0.0, mB = 0, vB = 0, beta1t = 1.0, beta2t = 1.0;
        int      t = 0;
        double bestValLoss = double.MaxValue;
        var    bestW       = new double[F];
        double bestB       = 0.0;
        int    patience    = 0;

        const int    MaxEpochs = 150;
        const double BaseLr    = 0.001;
        const double L2        = 0.01;
        const int    Patience  = 20;

        for (int epoch = 0; epoch < MaxEpochs; epoch++)
        {
            double alpha = BaseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / MaxEpochs));
            foreach (var s in train)
            {
                t++;
                beta1t *= beta1; beta2t *= beta2;
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                double hGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                double bc1   = 1.0 - beta1t, bc2 = 1.0 - beta2t;
                double alpAt = alpha * Math.Sqrt(bc2) / bc1;
                mB  = beta1 * mB  + (1.0 - beta1) * hGrad;
                vB  = beta2 * vB  + (1.0 - beta2) * hGrad * hGrad;
                b  -= alpAt * mB / (Math.Sqrt(vB) + eps);
                for (int j = 0; j < F; j++)
                {
                    double g = hGrad * s.Features[j] + L2 * w[j];
                    mW[j]  = beta1 * mW[j] + (1.0 - beta1) * g;
                    vW[j]  = beta2 * vW[j] + (1.0 - beta2) * g * g;
                    w[j]  -= alpAt * mW[j] / (Math.Sqrt(vW[j]) + eps);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0.0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5;
                valN++;
            }
            if (valN > 0) valLoss /= valN; else valLoss = double.MaxValue;
            if (valLoss < bestValLoss - 1e-6) { bestValLoss = valLoss; Array.Copy(w, bestW, F); bestB = b; patience = 0; }
            else if (++patience >= Patience) break;
        }
        if (canEarlyStop) { Array.Copy(bestW, w, F); b = bestB; }
        return (w, b);
    }

    /// <summary>Linear quantile regressor via SGD (pinball loss).</summary>
    internal static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> trainSet, int F, double tau)
    {
        var    w = new double[F];
        double b = 0.0;
        const double lr = 0.005, l2 = 1e-4;
        const int    MaxEpochs = 100;
        for (int epoch = 0; epoch < MaxEpochs; epoch++)
        {
            foreach (var s in trainSet)
            {
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double r = s.Magnitude - pred;
                double g = r >= 0 ? -tau : (1.0 - tau);
                b -= lr * g;
                for (int j = 0; j < F; j++)
                    w[j] -= lr * (g * s.Features[j] + l2 * w[j]);
            }
        }
        return (w, b);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // IMPORTANCE WEIGHTS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Density-ratio weights via logistic classifier (recent vs historical).</summary>
    internal static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int F, int windowDays)
    {
        int n = trainSet.Count;
        if (n < 50) { var u = new double[n]; Array.Fill(u, 1.0 / n); return u; }

        int recentCount = Math.Max(10, Math.Min(n / 5, windowDays * 24));
        recentCount = Math.Min(recentCount, n - 10);
        int histCount = n - recentCount;

        var    dw = new double[F];
        double db = 0.0;
        const double lr = 0.01, l2 = 0.01;

        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < n; i++)
            {
                double y   = i >= histCount ? 1.0 : 0.0;
                double z   = db;
                for (int j = 0; j < F; j++) z += dw[j] * trainSet[i].Features[j];
                double p   = Sigmoid(z);
                double err = p - y;
                for (int j = 0; j < F; j++)
                    dw[j] -= lr * (err * trainSet[i].Features[j] + l2 * dw[j]);
                db -= lr * err;
            }
        }

        var    weights = new double[n];
        double sum     = 0.0;
        for (int i = 0; i < n; i++)
        {
            double z = db;
            for (int j = 0; j < F; j++) z += dw[j] * trainSet[i].Features[j];
            double p     = Sigmoid(z);
            double ratio = Math.Clamp(p / Math.Max(1.0 - p, 1e-6), 0.01, 10.0);
            weights[i] = ratio;
            sum        += ratio;
        }
        for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    /// <summary>Covariate-shift weights based on parent-model quantile breakpoints.</summary>
    internal static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> samples, double[][] parentQbp, int F)
    {
        int n = samples.Count;
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            float[] feat = samples[i].Features;
            int outsideCount = 0, checkedCount = 0;
            for (int j = 0; j < F; j++)
            {
                if (j >= parentQbp.Length) continue;
                var bp = parentQbp[j];
                if (bp.Length < 2) continue;
                double q10 = bp[0], q90 = bp[^1];
                if ((double)feat[j] < q10 || (double)feat[j] > q90) outsideCount++;
                checkedCount++;
            }
            weights[i] = 1.0 + (checkedCount > 0 ? (double)outsideCount / checkedCount : 0.0);
        }
        double mean = weights.Average();
        if (mean > 1e-10) for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }

    /// <summary>Element-wise product of two weight vectors, re-normalised.</summary>
    internal static double[] BlendImportanceWeights(double[] w1, double[] w2, int n)
    {
        var    blended = new double[n];
        double sum     = 0.0;
        for (int i = 0; i < n; i++) { blended[i] = w1[i] * w2[i]; sum += blended[i]; }
        if (sum > 1e-15) for (int i = 0; i < n; i++) blended[i] /= sum;
        else             Array.Fill(blended, 1.0 / n);
        return blended;
    }

    /// <summary>Uniform 1/n weight vector.</summary>
    internal static double[] UniformWeights(int n)
    {
        var w = new double[n];
        Array.Fill(w, 1.0 / Math.Max(1, n));
        return w;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // MONITORING DIAGNOSTICS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Per-feature decile breakpoints for PSI monitoring.</summary>
    internal static double[][] ComputeQuantileBreakpoints(List<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 10) return [];
        var bp = new double[F][];
        for (int j = 0; j < F; j++)
        {
            var sorted = trainSet.Select(s => (double)s.Features[j]).OrderBy(v => v).ToArray();
            int n = sorted.Length;
            bp[j] = new double[9];
            for (int q = 1; q <= 9; q++)
            {
                int idx = Math.Clamp((int)Math.Round((double)q / 10.0 * n), 0, n - 1);
                bp[j][q - 1] = sorted[idx];
            }
        }
        return bp;
    }

    /// <summary>Durbin-Watson statistic on linear-regression magnitude residuals.</summary>
    internal static double ComputeDurbinWatson(
        List<TrainingSample> trainSet, double[] magW, double magBias, int F)
    {
        if (trainSet.Count < 3) return 2.0;
        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < F && j < magW.Length; j++) pred += magW[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }
        double numerator = 0.0, denominator = 0.0;
        for (int i = 1; i < residuals.Length; i++) { double d = residuals[i] - residuals[i - 1]; numerator += d * d; }
        for (int i = 0; i < residuals.Length; i++) denominator += residuals[i] * residuals[i];
        return denominator > 1e-15 ? numerator / denominator : 2.0;
    }

    /// <summary>Feature pairs with mutual information above threshold (binned estimator).</summary>
    internal static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 20 || F < 2) return [];

        const int nBins      = 10;
        double    threshNats = threshold * Math.Log(2.0);
        int       n          = trainSet.Count;

        var bins = new int[n, F];
        for (int j = 0; j < F; j++)
        {
            var vals  = trainSet.Select(s => (double)s.Features[j]).OrderBy(v => v).ToArray();
            var edges = new double[nBins - 1];
            for (int b = 1; b < nBins; b++)
            {
                int idx = Math.Clamp((int)Math.Round((double)b / nBins * n), 0, n - 1);
                edges[b - 1] = vals[idx];
            }
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[j];
                int    b = 0;
                for (int eb = 0; eb < edges.Length; eb++) if (v > edges[eb]) b = eb + 1;
                bins[i, j] = b;
            }
        }

        var result = new List<string>();
        for (int j1 = 0; j1 < F; j1++)
        for (int j2 = j1 + 1; j2 < F; j2++)
        {
            var    joint = new double[nBins, nBins];
            var    margX = new double[nBins];
            var    margY = new double[nBins];
            for (int i = 0; i < n; i++)
            {
                int b1 = bins[i, j1], b2 = bins[i, j2];
                joint[b1, b2] += 1.0; margX[b1] += 1.0; margY[b2] += 1.0;
            }
            double mi = 0.0;
            for (int b1 = 0; b1 < nBins; b1++)
            for (int b2 = 0; b2 < nBins; b2++)
            {
                double pXY = joint[b1, b2] / n;
                if (pXY < 1e-10) continue;
                double pX = margX[b1] / n, pY = margY[b2] / n;
                if (pX < 1e-10 || pY < 1e-10) continue;
                mi += pXY * Math.Log(pXY / (pX * pY));
            }
            if (mi > threshNats) result.Add($"F{j1}-F{j2}");
        }
        return result.ToArray();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // K-MEANS++ HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>K-means++ initialisation of cluster centres.</summary>
    internal static double[][] KMeansInit(List<TrainingSample> samples, int k, int F, Random rng)
    {
        var centres = new double[k][];
        centres[0] = Array.ConvertAll(samples[rng.Next(samples.Count)].Features, x => (double)x);
        for (int ci = 1; ci < k; ci++)
        {
            double[] dists = samples.Select(s =>
            {
                double best = double.MaxValue;
                for (int j = 0; j < ci; j++)
                {
                    double d = 0;
                    for (int fi = 0; fi < F; fi++) { double diff = s.Features[fi] - centres[j][fi]; d += diff * diff; }
                    if (d < best) best = d;
                }
                return best;
            }).ToArray();
            double total = dists.Sum(), pick = rng.NextDouble() * total, cum = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                cum += dists[i];
                if (cum >= pick) { centres[ci] = Array.ConvertAll(samples[i].Features, x => (double)x); break; }
            }
            centres[ci] ??= Array.ConvertAll(samples[rng.Next(samples.Count)].Features, x => (double)x);
        }
        return centres;
    }

    /// <summary>Lloyd's K-means refinement iterations.</summary>
    internal static double[][] KMeansRefine(List<TrainingSample> samples, double[][] centres, int F, int iters)
    {
        int k = centres.Length;
        for (int it = 0; it < iters; it++)
        {
            int[] assign = new int[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                double best = double.MaxValue; int bestC = 0;
                for (int c = 0; c < k; c++)
                {
                    double d = 0;
                    for (int fi = 0; fi < F; fi++) { double diff = samples[i].Features[fi] - centres[c][fi]; d += diff * diff; }
                    if (d < best) { best = d; bestC = c; }
                }
                assign[i] = bestC;
            }
            double[][] newC = Enumerable.Range(0, k).Select(_ => new double[F]).ToArray();
            int[]      cnt  = new int[k];
            for (int i = 0; i < samples.Count; i++)
            {
                for (int fi = 0; fi < F; fi++) newC[assign[i]][fi] += samples[i].Features[fi];
                cnt[assign[i]]++;
            }
            for (int c = 0; c < k; c++)
                if (cnt[c] > 0) for (int fi = 0; fi < F; fi++) newC[c][fi] /= cnt[c];
                else            newC[c] = centres[c];
            centres = newC;
        }
        return centres;
    }
}
