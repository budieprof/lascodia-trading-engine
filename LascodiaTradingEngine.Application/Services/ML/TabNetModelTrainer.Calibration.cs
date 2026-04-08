using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  PLATT SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        if (calSet.Count < MinCalibrationSamples) return (1.0, 0.0);
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TabNetRawProb(calSet[i].Features, w), ProbClampMin, 1.0 - ProbClampMin);
            logits[i] = Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        for (int ep = 0; ep < CalibrationEpochs; ep++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            plattA -= CalibrationLr * dA / n;
            plattB -= CalibrationLr * dB / n;
        }
        return (plattA, plattB);
    }

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        var buySamples  = calSet.Where(s => s.Direction > 0).ToList();
        var sellSamples = calSet.Where(s => s.Direction <= 0).ToList();
        var (aBuy,  bBuy)  = buySamples.Count  >= MinCalibrationSamples ? FitPlattScaling(buySamples,  w) : (1.0, 0.0);
        var (aSell, bSell) = sellSamples.Count >= MinCalibrationSamples ? FitPlattScaling(sellSamples, w) : (1.0, 0.0);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISOTONIC CALIBRATION (PAVA)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB)
    {
        if (calSet.Count < MinCalibrationSamples) return [];
        var pairs = new (double X, double Y)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            pairs[i] = (TabNetCalibProb(calSet[i].Features, w, plattA, plattB),
                         calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.X.CompareTo(b.X));

        var blocks = new List<(double SumY, int Count, double XMin, double XMax)>();
        foreach (var (x, y) in pairs)
        {
            blocks.Add((y, 1, x, x));
            while (blocks.Count >= 2)
            {
                var last = blocks[^1];
                var prev = blocks[^2];
                if (prev.SumY / prev.Count <= last.SumY / last.Count) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (prev.SumY + last.SumY, prev.Count + last.Count, prev.XMin, last.XMax);
            }
        }

        var bp = new List<double>();
        foreach (var b in blocks) { bp.Add((b.XMin + b.XMax) / 2.0); bp.Add(b.SumY / b.Count); }
        return bp.ToArray();
    }

    private static double ApplyIsotonic(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        for (int i = 0; i < bp.Length - 2; i += 2)
        {
            if (p <= bp[i]) return bp[i + 1];
            if (i + 2 < bp.Length && p <= bp[i + 2])
            {
                double frac = (p - bp[i]) / (bp[i + 2] - bp[i] + Eps);
                return bp[i + 1] + frac * (bp[i + 3] - bp[i + 1]);
            }
        }
        return bp[^1];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TEMPERATURE SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        if (calSet.Count < MinCalibrationSamples) return 1.0;
        int n = calSet.Count;
        var logits = new double[n]; var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TabNetRawProb(calSet[i].Features, w), ProbClampMin, 1.0 - ProbClampMin);
            logits[i] = Logit(raw); labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }
        double T = 1.0;
        for (int ep = 0; ep < 100; ep++)
        {
            double dT = 0;
            for (int i = 0; i < n; i++) dT += (Sigmoid(logits[i] / T) - labels[i]) * (-logits[i] / (T * T));
            T -= CalibrationLr * dT / n; T = Math.Max(0.01, T);
        }
        return T;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ECE, THRESHOLD, KELLY, BSS
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeEce(IReadOnlyList<TrainingSample> testSet, TabNetWeights w, double plattA, double plattB, int bins = 10)
    {
        if (testSet.Count < bins) return 1.0;
        var binCorrect = new double[bins]; var binConf = new double[bins]; var binCount = new int[bins];
        foreach (var s in testSet)
        {
            double p = TabNetCalibProb(s.Features, w, plattA, plattB);
            int bin  = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin] += p; binCorrect[bin] += s.Direction > 0 ? 1 : 0; binCount[bin]++;
        }
        double ece = 0; int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            ece += Math.Abs(binCorrect[b] / binCount[b] - binConf[b] / binCount[b]) * binCount[b] / n;
        }
        return ece;
    }

    private static double ComputeOptimalThreshold(
        IReadOnlyList<TrainingSample> dataSet, TabNetWeights w, double plattA, double plattB,
        int searchMin = 30, int searchMax = 75)
    {
        if (dataSet.Count < 30) return 0.5;
        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = TabNetCalibProb(dataSet[i].Features, w, plattA, plattB);
        double bestEv = double.MinValue, bestT = 0.5;
        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double threshold = ti / 100.0, ev = 0;
            for (int i = 0; i < dataSet.Count; i++)
            {
                bool correct = (probs[i] >= threshold) == (dataSet[i].Direction > 0);
                ev += (correct ? 1 : -1) * Math.Abs(dataSet[i].Magnitude);
            }
            ev /= dataSet.Count;
            if (ev > bestEv) { bestEv = ev; bestT = threshold; }
        }
        return bestT;
    }

    private static double ComputeAvgKellyFraction(IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB)
    {
        if (calSet.Count < MinCalibrationSamples) return 0;
        double kellySum = 0;
        foreach (var s in calSet) kellySum += Math.Max(0, (2 * TabNetCalibProb(s.Features, w, plattA, plattB) - 1) * 0.5);
        return kellySum / calSet.Count;
    }

    private static double ComputeBrierSkillScore(IReadOnlyList<TrainingSample> testSet, TabNetWeights w, double plattA, double plattB)
    {
        if (testSet.Count < MinCalibrationSamples) return 0;
        int n = testSet.Count; double baseRate = testSet.Count(s => s.Direction > 0) / (double)n;
        double brierNaive = baseRate * (1 - baseRate), brierModel = 0;
        foreach (var s in testSet)
        {
            double p = TabNetCalibProb(s.Features, w, plattA, plattB);
            int y = s.Direction > 0 ? 1 : 0; brierModel += (p - y) * (p - y);
        }
        brierModel /= n;
        return brierNaive > 1e-10 ? 1.0 - brierModel / brierNaive : 0;
    }
}
