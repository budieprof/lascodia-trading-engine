using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class DannModelTrainer
{

    // ═══════════════════════════════════════════════════════════════════════════
    //  CALIBRATION
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet, DannModel model, int F)
    {
        if (calSet.Count < 4) return (1.0, 0.0);

        double[] probs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            probs[i] = Math.Clamp(ForwardCls(model, calSet[i].Features), 1e-7, 1.0 - 1e-7);

        // Fit A, B via L-BFGS-style gradient descent on NLL
        double A = 1.0, B = 0.0;
        for (int iter = 0; iter < 200; iter++)
        {
            double dA = 0.0, dB = 0.0;
            foreach (var (s, p) in calSet.Zip(probs))
            {
                double logit = A * Math.Log(p / (1.0 - p)) + B;
                double q     = Sigmoid(logit);
                double err   = q - s.Direction;
                dA += err * Math.Log(p / (1.0 - p));
                dB += err;
            }
            double lr2 = 0.01 / calSet.Count;
            A -= lr2 * dA;
            B -= lr2 * dB;
        }
        return (A, B);
    }

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet, DannModel model, int F)
    {
        var buySamples  = calSet.Where(s => s.Direction == 1).ToList();
        var sellSamples = calSet.Where(s => s.Direction == 0).ToList();

        var (ab, bb) = buySamples.Count  >= 4 ? FitPlattScaling(buySamples,  model, F) : (1.0, 0.0);
        var (as2, bs) = sellSamples.Count >= 4 ? FitPlattScaling(sellSamples, model, F) : (1.0, 0.0);
        return (ab, bb, as2, bs);
    }

    private static double ApplyPlatt(double rawP, double A, double B)
    {
        double logit = A * Math.Log(Math.Clamp(rawP, 1e-7, 1.0 - 1e-7) /
                                    (1.0 - Math.Clamp(rawP, 1e-7, 1.0 - 1e-7))) + B;
        return Sigmoid(logit);
    }

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        if (calSet.Count < 4) return [];

        var pairs = calSet
            .Select(s => (Score: ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB),
                          Label: (double)s.Direction))
            .OrderBy(p => p.Score)
            .ToList();

        // PAVA (pool adjacent violators)
        var blocks = new List<(double MeanScore, double MeanLabel, int Count)>();
        foreach (var (score, label, _) in pairs.Select((p, _) => (p.Score, p.Label, 1)))
        {
            blocks.Add((score, label, 1));
            while (blocks.Count > 1 &&
                   blocks[^1].MeanLabel < blocks[^2].MeanLabel)
            {
                var b1 = blocks[^2]; var b2 = blocks[^1];
                int  tc = b1.Count + b2.Count;
                blocks.RemoveAt(blocks.Count - 1);
                blocks.RemoveAt(blocks.Count - 1);
                blocks.Add((
                    (b1.MeanScore * b1.Count + b2.MeanScore * b2.Count) / tc,
                    (b1.MeanLabel * b1.Count + b2.MeanLabel * b2.Count) / tc,
                    tc));
            }
        }

        // Flatten to [x0, y0, x1, y1, ...]
        var result = new double[blocks.Count * 2];
        for (int i = 0; i < blocks.Count; i++)
        {
            result[i * 2]     = blocks[i].MeanScore;
            result[i * 2 + 1] = blocks[i].MeanLabel;
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TEMPERATURE SCALING
    // ═══════════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        double T = 1.0;
        for (int iter = 0; iter < 100; iter++)
        {
            double dT = 0.0;
            foreach (var s in calSet)
            {
                double rawP   = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                double logit  = Math.Log(Math.Clamp(rawP, 1e-7, 1.0 - 1e-7) /
                                         (1.0 - Math.Clamp(rawP, 1e-7, 1.0 - 1e-7))) / T;
                double q      = Sigmoid(logit);
                dT           += (q - s.Direction) * (-logit / T);
            }
            T = Math.Max(0.1, T - 0.01 * dT / calSet.Count);
        }
        return T;
    }

    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet, DannModel model,
        double plattA, double plattB, int F,
        double searchMin, double searchMax)
    {
        if (calSet.Count == 0) return 0.5;
        double min = searchMin > 0 ? searchMin : 0.30;
        double max = searchMax > 0 ? searchMax : 0.70;
        double bestThr = 0.5, bestEV = double.MinValue;
        for (double thr = min; thr <= max; thr += 0.01)
        {
            double ev = 0.0;
            foreach (var s in calSet)
            {
                double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                if (p2 >= thr) ev += (2.0 * p2 - 1.0) * Math.Sign(s.Magnitude);
            }
            if (ev > bestEV) { bestEV = ev; bestThr = thr; }
        }
        return bestThr;
    }

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet, DannModel model,
        double plattA, double plattB, int F)
    {
        if (calSet.Count == 0) return 0.0;
        double kelly = 0.0;
        foreach (var s in calSet)
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            kelly += Math.Max(0.0, 2.0 * p2 - 1.0);
        }
        return kelly * 0.5 / calSet.Count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  QUANTILE MAGNITUDE REGRESSOR (pinball loss)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> trainSet, int F, double tau, double overrideLr = 0.0)
    {
        var w  = new double[F];
        double b = 0.0;
        double lr2 = overrideLr > 0.0 ? overrideLr : 0.001;

        // Adam moment buffers
        var mw = new double[F]; var vw = new double[F];
        double mb = 0.0, vb = 0.0;
        int step = 0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[F]; double db = 0.0;
            foreach (var s in trainSet)
            {
                double pred = b;
                for (int fi = 0; fi < F; fi++) pred += w[fi] * s.Features[fi];
                double resid = s.Magnitude - pred;
                double grad  = resid >= 0 ? -tau : (1.0 - tau);
                db += grad;
                for (int fi = 0; fi < F; fi++) dw[fi] += grad * s.Features[fi];
            }
            double n = trainSet.Count;
            for (int fi = 0; fi < F; fi++) dw[fi] /= n;
            db /= n;
            step++;
            double bc1 = 1.0 - Math.Pow(AdamBeta1, step);
            double bc2 = 1.0 - Math.Pow(AdamBeta2, step);
            b = AdamScalar(b, db, ref mb, ref vb, lr2, bc1, bc2);
            for (int fi = 0; fi < F; fi++)
            {
                mw[fi] = AdamBeta1 * mw[fi] + (1.0 - AdamBeta1) * dw[fi];
                vw[fi] = AdamBeta2 * vw[fi] + (1.0 - AdamBeta2) * dw[fi] * dw[fi];
                w[fi] -= lr2 * (mw[fi] / bc1) / (Math.Sqrt(vw[fi] / bc2) + AdamEpsilon);
            }
        }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MAGNITUDE REGRESSOR (Adam + Huber)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> trainSet, int F, TrainingHyperparams hp)
    {
        if (trainSet.Count == 0) return (new double[F], 0.0);

        var w  = new double[F]; double b = 0.0;
        var mw = new double[F]; var vw = new double[F];
        double mb = 0.0, vb = 0.0;
        double lr2 = 0.001;
        double delta = 1.0; // Huber delta
        int step = 0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[F]; double db = 0.0;
            foreach (var s in trainSet)
            {
                double pred = b;
                for (int fi = 0; fi < F; fi++) pred += w[fi] * s.Features[fi];
                double resid = pred - s.Magnitude;
                double grad  = Math.Abs(resid) <= delta ? resid : delta * Math.Sign(resid);
                db += grad;
                for (int fi = 0; fi < F; fi++) dw[fi] += grad * s.Features[fi];
            }
            double n = trainSet.Count;
            for (int fi = 0; fi < F; fi++) dw[fi] /= n;
            db /= n;
            step++;
            double bc1 = 1.0 - Math.Pow(AdamBeta1, step);
            double bc2 = 1.0 - Math.Pow(AdamBeta2, step);
            b  = AdamScalar(b,  db,  ref mb, ref vb, lr2, bc1, bc2);
            for (int fi = 0; fi < F; fi++)
            {
                mw[fi] = AdamBeta1 * mw[fi] + (1.0 - AdamBeta1) * dw[fi];
                vw[fi] = AdamBeta2 * vw[fi] + (1.0 - AdamBeta2) * dw[fi] * dw[fi];
                double mHat = mw[fi] / bc1;
                double vHat = vw[fi] / bc2;
                w[fi] -= lr2 * mHat / (Math.Sqrt(vHat) + AdamEpsilon);
            }
        }
        return (w, b);
    }
}
