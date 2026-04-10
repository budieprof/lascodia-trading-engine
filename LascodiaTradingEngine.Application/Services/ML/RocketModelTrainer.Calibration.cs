using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class RocketModelTrainer
{

    // ═══════════════════════════════════════════════════════════════════════════
    //  Platt scaling
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim)
    {
        int n = calRocket.Count;
        if (n < 5) return (1.0, 0.0);

        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = RocketProb(calRocket[i], w, bias, dim);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double lr = 0.01;
        const int epochs = 200;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            plattA -= lr * dA / n;
            plattB -= lr * dB / n;
        }

        return (plattA, plattB);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Class-conditional Platt scaling
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim)
    {
        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();
        const int epochs = 200;

        for (int i = 0; i < calRocket.Count; i++)
        {
            double raw = RocketProb(calRocket[i], w, bias, dim);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double logit = MLFeatureHelper.Logit(raw);
            double y     = calSet[i].Direction > 0 ? 1.0 : 0.0;
            if (raw >= 0.5) buySamples.Add((logit, y));
            else            sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs, int ep)
        {
            if (pairs.Count < 5) return (1.0, 0.0); // identity on logit scale
            double a = 1.0, b = 0.0;
            for (int e = 0; e < ep; e++)
            {
                double dA = 0, dB = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err = calibP - y;
                    dA += err * logit;
                    dB += err;
                }
                a -= 0.01 * dA / pairs.Count;
                b -= 0.01 * dB / pairs.Count;
            }
            return (a, b);
        }

        var (aBuy, bBuy)   = FitSgd(buySamples, epochs);
        var (aSell, bSell) = FitSgd(sellSamples, epochs);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EV-optimal threshold
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeOptimalThreshold(
        List<double[]> rocketFeatures, List<TrainingSample> samples,
        double[] w, double bias, double plattA, double plattB, int dim,
        int searchMin, int searchMax)
    {
        int n = samples.Count;
        var probs = new double[n];
        for (int i = 0; i < n; i++)
            probs[i] = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);

        double bestEv = double.MinValue;
        double bestThreshold = 0.5;

        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double t  = ti / 100.0;
            double ev = 0;
            for (int i = 0; i < n; i++)
            {
                bool predictedUp = probs[i] >= t;
                bool actualUp    = samples[i].Direction == 1;
                bool correct     = predictedUp == actualUp;
                ev += (correct ? 1 : -1) * Math.Abs(samples[i].Magnitude);
            }
            ev /= n;
            if (ev > bestEv) { bestEv = ev; bestThreshold = t; }
        }

        return bestThreshold;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Isotonic calibration (PAVA)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        int n = calRocket.Count;
        if (n < 5) return [];

        var pairs = new (double P, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            double p = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
            pairs[i] = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        var stack = new List<(double SumY, double SumP, int Count)>(n);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var prev = stack[^2];
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY,
                                 prev.SumP + last.SumP,
                                 prev.Count + last.Count);
                }
                else break;
            }
        }

        var breakpoints = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            breakpoints[i * 2]     = stack[i].SumP / stack[i].Count;
            breakpoints[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }

        return breakpoints;
    }

    private static double ApplyIsotonicCalibration(double p, double[] bp)
    {
        int count = bp.Length / 2;
        if (count < 2) return p;

        if (p <= bp[0]) return bp[1];
        if (p >= bp[(count - 1) * 2]) return bp[(count - 1) * 2 + 1];

        for (int i = 0; i < count - 1; i++)
        {
            double x0 = bp[i * 2],     y0 = bp[i * 2 + 1];
            double x1 = bp[(i + 1) * 2], y1 = bp[(i + 1) * 2 + 1];
            if (p >= x0 && p <= x1)
            {
                double frac = (x1 - x0) > 1e-10 ? (p - x0) / (x1 - x0) : 0;
                return y0 + frac * (y1 - y0);
            }
        }

        return p;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Magnitude regressor (linear OLS)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> trainSet, int featureCount,
        CancellationToken ct = default)
    {
        int n = trainSet.Count;
        if (n < 5) return (new double[featureCount], 0.0);

        // 90/10 train/val split for early stopping
        int magTrainN = (int)(n * 0.90);
        int magValN   = n - magTrainN;
        if (magTrainN < 5) magTrainN = n;

        var w = new double[featureCount];
        double b = 0;

        // Adam state (#3: shared helper)
        var linAdam = AdamState.Create(featureCount);

        const double baseLr = 0.01;
        const int maxEpochs = 200;
        const int patience = 15;
        const double huberDelta = 1.0;
        int batchSize = Math.Min(DefaultBatchSize, magTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestW = new double[featureCount];
        double bestB = 0;

        var idx = new int[magTrainN];
        for (int i = 0; i < magTrainN; i++) idx[i] = i;
        var rng = new Random(magTrainN ^ featureCount);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training with Huber loss
            for (int bStart = 0; bStart < magTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, magTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[featureCount];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[si].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[si].Features[j];
                    double residual = pred - trainSet[si].Magnitude;

                    // Huber loss gradient
                    double grad;
                    if (Math.Abs(residual) <= huberDelta)
                        grad = residual;
                    else
                        grad = huberDelta * Math.Sign(residual);

                    for (int j = 0; j < fLen; j++)
                        gW[j] += grad * trainSet[si].Features[j];
                    gBatch += grad;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < featureCount; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref linAdam, gW, gBatch, w, ref b, lr, featureCount);
            }

            // Validation loss (Huber)
            if (magValN > 0)
            {
                double valLoss = 0;
                for (int i = magTrainN; i < n; i++)
                {
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[i].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[i].Features[j];
                    double residual = Math.Abs(pred - trainSet[i].Magnitude);
                    valLoss += residual <= huberDelta
                        ? 0.5 * residual * residual
                        : huberDelta * (residual - 0.5 * huberDelta);
                }
                valLoss /= magValN;

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    Array.Copy(w, bestW, featureCount);
                    bestB = b;
                    patienceCounter = 0;
                }
                else
                {
                    patienceCounter++;
                    if (patienceCounter >= patience) break;
                }
            }
        }

        if (magValN > 0 && bestValLoss < double.MaxValue)
            return (bestW, bestB);
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Quantile magnitude regressor (pinball loss)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> trainSet, int featureCount, double tau,
        CancellationToken ct = default)
    {
        int n = trainSet.Count;
        if (n < 5) return (new double[featureCount], 0.0);

        // 90/10 train/val split for early stopping
        int qTrainN = (int)(n * 0.90);
        int qValN   = n - qTrainN;
        if (qTrainN < 5) qTrainN = n;

        var w = new double[featureCount];
        double b = 0;

        // Adam state (#3: shared helper)
        var qAdam = AdamState.Create(featureCount);

        const double baseLr = 0.01;
        const int maxEpochs = 200;
        const int patience = 15;
        int batchSize = Math.Min(DefaultBatchSize, qTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestW = new double[featureCount];
        double bestB = 0;

        var idx = new int[qTrainN];
        for (int i = 0; i < qTrainN; i++) idx[i] = i;
        var rng = new Random(qTrainN ^ featureCount ^ (int)(tau * 1000));

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training with pinball loss
            for (int bStart = 0; bStart < qTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, qTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[featureCount];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[si].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[si].Features[j];
                    double err = trainSet[si].Magnitude - pred;
                    double grad = err >= 0 ? -tau : (1.0 - tau);

                    for (int j = 0; j < fLen; j++)
                        gW[j] += grad * trainSet[si].Features[j];
                    gBatch += grad;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < featureCount; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref qAdam, gW, gBatch, w, ref b, lr, featureCount);
            }

            // Validation loss (pinball)
            if (qValN > 0)
            {
                double valLoss = 0;
                for (int i = qTrainN; i < n; i++)
                {
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[i].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[i].Features[j];
                    double err = trainSet[i].Magnitude - pred;
                    valLoss += err >= 0 ? tau * err : -(1.0 - tau) * err;
                }
                valLoss /= qValN;

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    Array.Copy(w, bestW, featureCount);
                    bestB = b;
                    patienceCounter = 0;
                }
                else
                {
                    patienceCounter++;
                    if (patienceCounter >= patience) break;
                }
            }
        }

        if (qValN > 0 && bestValLoss < double.MaxValue)
            return (bestW, bestB);
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Temperature scaling
    // ═══════════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim)
    {
        int n = calRocket.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double rawP = RocketProb(calRocket[i], w, bias, dim);
            rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(rawP);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        // Gradient descent on T to minimise NLL
        double T = 1.0;
        const double lr = 0.01;
        const int steps = 100;

        for (int step = 0; step < steps; step++)
        {
            double gradT = 0;
            for (int i = 0; i < n; i++)
            {
                double scaled = logits[i] / T;
                double calibP = MLFeatureHelper.Sigmoid(scaled);
                // dNLL/dT = sum_i (calibP_i - y_i) * (-logit_i / T^2)
                gradT += (calibP - labels[i]) * (-logits[i] / (T * T));
            }
            gradT /= n;
            T -= lr * gradT;
            T = Math.Clamp(T, 0.1, 10.0);
        }

        return T;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Average Kelly fraction
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeAvgKellyFraction(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        double sum = 0;
        for (int i = 0; i < calRocket.Count; i++)
        {
            double calibP = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
            sum += Math.Max(0.0, 2.0 * calibP - 1.0);
        }
        return calRocket.Count > 0 ? sum / calRocket.Count * 0.5 : 0;
    }
}
