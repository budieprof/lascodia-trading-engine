using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    // ── Platt scaling ─────────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        int n      = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];

        for (int i = 0; i < n; i++)
        {
            double score = PredictScore(calSet[i].Features, stumps, alphas);
            double raw   = MLFeatureHelper.Sigmoid(2 * score);
            raw          = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i]    = MLFeatureHelper.Logit(raw);
            labels[i]    = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double lr      = 0.01;
        const int    epochs  = 200;
        const double tol     = 1e-6;
        int          noImpro = 0;
        double       prevLoss = double.MaxValue;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0, dB = 0, loss = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA   += err * logits[i];
                dB   += err;
                loss -= labels[i] * Math.Log(calibP + Eps) + (1 - labels[i]) * Math.Log(1 - calibP + Eps);
            }
            plattA -= lr * dA / n;
            plattB -= lr * dB / n;

            double curLoss = loss / n;
            if (prevLoss - curLoss < tol) { if (++noImpro >= 5) break; }
            else                          noImpro = 0;
            prevLoss = curLoss;
        }

        return (plattA, plattB);
    }

    // ── Isotonic calibration (PAVA) ───────────────────────────────────────────

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (calSet.Count < 30) return [];

        int cn    = calSet.Count;
        var pairs = new (double P, double Y)[cn];
        for (int i = 0; i < cn; i++)
        {
            double rawProb = PredictRawProb(calSet[i].Features, stumps, alphas);
            double p = PredictPreIsotonicProbFromRaw(
                rawProb, plattA, plattB, temperatureScale,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            pairs[i]     = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        // Stack-based Pool Adjacent Violators Algorithm (PAVA)
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
                    stack[^1] = (prevSumY + lastSumY,
                                 prevSumP + lastSumP,
                                 prevCount + lastCount);
                }
                else break;
            }
        }

        // Merge pools with fewer than MinBlockSize samples into their smaller neighbour
        // to prevent overfitting on tiny PAVA segments.
        const int MinBlockSize = 5;
        bool merged = true;
        while (merged && stack.Count > 2)
        {
            merged = false;
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i].Count >= MinBlockSize) continue;
                int neighbour = (i == 0) ? 1
                              : (i == stack.Count - 1) ? i - 1
                              : (stack[i - 1].Count <= stack[i + 1].Count ? i - 1 : i + 1);
                int lo = Math.Min(i, neighbour);
                int hi = Math.Max(i, neighbour);
                var (lSumY, lSumP, lCount) = stack[lo];
                var (hSumY, hSumP, hCount) = stack[hi];
                stack[lo] = (lSumY + hSumY, lSumP + hSumP, lCount + hCount);
                stack.RemoveAt(hi);
                merged = true;
                break;
            }
        }

        // Interleaved [x₀,y₀,x₁,y₁,...] breakpoints — one per PAVA block
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
        if (p <= bp[0])                  return bp[1];
        if (p >= bp[(nPoints - 1) * 2])  return bp[(nPoints - 1) * 2 + 1];

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

    // ── Class-conditional Platt scaling (Buy / Sell separate scalers) ─────────

    /// <summary>
    /// Fits separate Platt scalers for Buy (raw prob ≥ 0.5) and Sell (raw prob &lt; 0.5) subsets
    /// of the calibration set to correct directional calibration bias.
    /// Returns (ABuy, BBuy, ASell, BSell); returns (1,0,1,0) when a class subset has &lt; 5 samples.
    /// </summary>
    private static (double ABuy, double BBuy, double ASell, double BSell)
        FitClassConditionalPlatt(
            List<TrainingSample> calSet,
            List<GbmTree>        stumps,
            List<double>         alphas,
            double               plattA,
            double               plattB,
            double               temperatureScale,
            double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        const double lr     = 0.01;
        const int    epochs = 200;

        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();

        foreach (var s in calSet)
        {
            double rawP  = PredictRawProb(s.Features, stumps, alphas);
            double logit = MLFeatureHelper.Logit(rawP);
            double y     = s.Direction > 0 ? 1.0 : 0.0;
            double globalCalibP = PredictPreIsotonicProbFromRaw(rawP, plattA, plattB, temperatureScale,
                                                                routingThreshold: routingThreshold);
            if (globalCalibP >= routingThreshold) buySamples.Add((logit, y));
            else                                  sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs)
        {
            if (pairs.Count < 5) return (1.0, 0.0);
            double a = 1.0, b = 0.0;
            double prevL   = double.MaxValue;
            int    noImpro = 0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0, dB = 0, loss = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err    = calibP - y;
                    dA   += err * logit;
                    dB   += err;
                    loss -= y * Math.Log(calibP + 1e-10) + (1 - y) * Math.Log(1 - calibP + 1e-10);
                }
                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;

                double curL = loss / n;
                if (prevL - curL < 1e-6) { if (++noImpro >= 5) break; }
                else                     noImpro = 0;
                prevL = curL;
            }
            return (a, b);
        }

        var (aBuy,  bBuy)  = FitSgd(buySamples);
        var (aSell, bSell) = FitSgd(sellSamples);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ── Average Kelly fraction ─────────────────────────────────────────────────

    /// <summary>
    /// Computes the half-Kelly fraction averaged over the calibration set:
    ///   mean( max(0, 2·calibP − 1) ) × 0.5
    /// where calibP uses the already-fitted global Platt (A, B).
    /// Returns 0.0 if the calibration set is empty.
    /// </summary>
    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0.0;
        foreach (var s in calSet)
        {
            double calibP = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            sum += Math.Max(0.0, 2.0 * calibP - 1.0);
        }
        return sum / calSet.Count * 0.5;
    }

    // ── Temperature scaling ────────────────────────────────────────────────────

    /// <summary>
    /// Fits a single temperature scalar T on the calibration set via SGD, minimising
    /// binary cross-entropy: calibP = σ(logit(rawP) / T).
    /// Initialised at T=1.0 (no-op); uses the same early-stopping pattern as Platt scaling.
    /// Returns 1.0 when the cal set is too small.
    /// </summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        CancellationToken    ct = default)
    {
        if (calSet.Count < 10) return 1.0;

        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double score = PredictScore(calSet[i].Features, stumps, alphas);
            double rawP  = Math.Clamp(MLFeatureHelper.Sigmoid(2 * score), 1e-7, 1.0 - 1e-7);
            logits[i]    = MLFeatureHelper.Logit(rawP);
            labels[i]    = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        // SGD on log(T) so T stays positive without clamping; T = exp(logT).
        double logT     = 0.0;  // T=1.0 initially
        const double lr = 0.01;
        double prevLoss = double.MaxValue;
        int    noImpro  = 0;

        for (int epoch = 0; epoch < 300; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double T    = Math.Exp(logT);
            double invT = 1.0 / Math.Max(T, 1e-8);
            double dLogT = 0.0, loss = 0.0;

            for (int i = 0; i < n; i++)
            {
                double z      = logits[i] * invT;
                double calibP = MLFeatureHelper.Sigmoid(z);
                double y      = labels[i];
                loss -= y * Math.Log(calibP + Eps) + (1 - y) * Math.Log(1 - calibP + Eps);
                // ∂L/∂logT = ∂L/∂z · ∂z/∂logT = (calibP − y) · (−logits[i] · invT)
                // because z = logits[i]/T = logits[i]·exp(−logT), so ∂z/∂logT = −z
                dLogT += (calibP - y) * (-z);
            }
            logT -= lr * dLogT / n;

            // Clamp T to [0.05, 5.0] to prevent degenerate solutions
            logT = Math.Clamp(logT, Math.Log(0.05), Math.Log(5.0));

            double curLoss = loss / n;
            if (prevLoss - curLoss < 1e-7) { if (++noImpro >= 10) break; }
            else                           noImpro = 0;
            prevLoss = curLoss;
        }

        return Math.Exp(logT);
    }

    private static double ComputeCalibrationNll(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale = 0.0)
    {
        if (calSet.Count == 0)
            return double.PositiveInfinity;

        double loss = 0.0;
        foreach (var sample in calSet)
        {
            double rawProb = PredictRawProb(sample.Features, stumps, alphas);
            double calibP = PredictPreIsotonicProbFromRaw(rawProb, plattA, plattB, temperatureScale);
            double y = sample.Direction > 0 ? 1.0 : 0.0;
            loss -= y * Math.Log(calibP + Eps) + (1.0 - y) * Math.Log(1.0 - calibP + Eps);
        }

        return loss / calSet.Count;
    }

    private static double ComputeCalibrationStackNll(
        List<TrainingSample> evalSet,
        List<GbmTree> stumps,
        List<double> alphas,
        double plattA,
        double plattB,
        double temperatureScale,
        double[]? isotonicBp,
        double plattABuy  = double.NaN,
        double plattBBuy  = double.NaN,
        double plattASell = double.NaN,
        double plattBSell = double.NaN,
        double routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (evalSet.Count == 0)
            return double.PositiveInfinity;

        double loss = 0.0;
        foreach (var sample in evalSet)
        {
            double p = PredictProb(
                sample.Features,
                stumps,
                alphas,
                plattA,
                plattB,
                temperatureScale,
                isotonicBp ?? [],
                0.5,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                routingThreshold);
            loss += BinaryNll(p, sample.Direction > 0 ? 1.0 : 0.0);
        }

        return loss / evalSet.Count;
    }

    private static (int SampleCount, double BaselineNll, double FittedNll) ComputeConditionalBranchStats(
        List<TrainingSample> evalSet,
        List<GbmTree> stumps,
        List<double> alphas,
        double plattA,
        double plattB,
        double temperatureScale,
        int label,
        double plattABuy  = double.NaN,
        double plattBBuy  = double.NaN,
        double plattASell = double.NaN,
        double plattBSell = double.NaN,
        double routingThreshold = DefaultConditionalRoutingThreshold)
    {
        double baselineLoss = 0.0;
        double fittedLoss = 0.0;
        int count = 0;

        foreach (var sample in evalSet)
        {
            double y = sample.Direction > 0 ? 1.0 : 0.0;
            if ((int)y != label)
                continue;

            double rawProb = PredictRawProb(sample.Features, stumps, alphas);
            double rawLogit = MLFeatureHelper.Logit(Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7));
            double globalP = PredictGlobalCalibrationFromRaw(rawProb, plattA, plattB, temperatureScale);
            bool buyBranch = globalP >= routingThreshold;
            if ((label == 1 && !buyBranch) || (label == 0 && buyBranch))
                continue;

            double fittedP = InferenceHelpers.ApplyConditionalCalibration(
                rawLogit,
                globalP,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                routingThreshold);
            baselineLoss += BinaryNll(globalP, y);
            fittedLoss += BinaryNll(fittedP, y);
            count++;
        }

        if (count == 0)
            return (0, 0.0, 0.0);

        return (count, baselineLoss / count, fittedLoss / count);
    }

    private static double PredictGlobalCalibrationFromRaw(
        double rawProb,
        double plattA,
        double plattB,
        double temperatureScale)
    {
        double rawLogit = MLFeatureHelper.Logit(Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7));
        return temperatureScale > 0.0
            ? MLFeatureHelper.Sigmoid(rawLogit / temperatureScale)
            : MLFeatureHelper.Sigmoid(plattA * rawLogit + plattB);
    }

    private static double BinaryNll(double probability, double label)
    {
        double p = Math.Clamp(probability, 1e-7, 1.0 - 1e-7);
        return -label * Math.Log(p) - (1.0 - label) * Math.Log(1.0 - p);
    }
}
