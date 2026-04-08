using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    private readonly record struct ConditionalPlattBranchFit(
        int SampleCount,
        double BaselineLoss,
        double FittedLoss,
        double A,
        double B)
    {
        public bool Accepted => InferenceHelpers.HasMeaningfulConditionalCalibration(A, B);
    }

    private readonly record struct ClassConditionalPlattFit(
        ConditionalPlattBranchFit Buy,
        ConditionalPlattBranchFit Sell);

    private readonly record struct TabNetCalibrationFit(
        ModelSnapshot FinalSnapshot,
        ModelSnapshot PreIsotonicSnapshot,
        TabNetCalibrationArtifact Artifact);

    private readonly record struct GlobalCalibrationSelection(
        string Name,
        ModelSnapshot Snapshot,
        double EvalNll);

    // ═══════════════════════════════════════════════════════════════════════
    //  PLATT / TEMPERATURE / CONDITIONAL / ISOTONIC STACK
    // ═══════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        int minCalibrationSamples,
        int calibrationEpochs,
        double calibrationLr)
    {
        if (calSet.Count < minCalibrationSamples) return (1.0, 0.0);
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        int nPos = 0;
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TabNetRawProb(calSet[i].Features, w), ProbClampMin, 1.0 - ProbClampMin);
            logits[i] = Logit(raw);
            if (calSet[i].Direction > 0) nPos++;
        }
        int nNeg = n - nPos;
        // Platt (2000) label smoothing: prevents overfitting on small calibration sets
        double targetPos = (nPos + 1.0) / (nPos + 2.0);
        double targetNeg = 1.0 / (nNeg + 2.0);
        for (int i = 0; i < n; i++)
            labels[i] = calSet[i].Direction > 0 ? targetPos : targetNeg;

        double plattA = 1.0, plattB = 0.0;
        double bestA = plattA, bestB = plattB;
        double bestLoss = double.PositiveInfinity;
        for (int ep = 0; ep < calibrationEpochs; ep++)
        {
            double dA = 0, dB = 0, loss = 0;
            double hAA = 0, hAB = 0, hBB = 0; // Hessian for Newton-Raphson
            for (int i = 0; i < n; i++)
            {
                double calibP = Sigmoid(plattA * logits[i] + plattB);
                double err = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
                loss -= labels[i] * Math.Log(Math.Max(calibP, ProbClampMin))
                      + (1.0 - labels[i]) * Math.Log(Math.Max(1.0 - calibP, ProbClampMin));
                double w2 = calibP * (1.0 - calibP) + Eps;
                hAA += w2 * logits[i] * logits[i];
                hAB += w2 * logits[i];
                hBB += w2;
            }
            loss /= n;
            if (double.IsFinite(loss) && loss < bestLoss)
            {
                bestLoss = loss;
                bestA = plattA;
                bestB = plattB;
            }
            // Newton-Raphson with Hessian; fall back to clipped gradient descent if singular
            double det = hAA * hBB - hAB * hAB;
            if (Math.Abs(det) > Eps)
            {
                plattA -= (hBB * dA - hAB * dB) / det;
                plattB -= (hAA * dB - hAB * dA) / det;
            }
            else
            {
                double gradNorm = Math.Sqrt(dA * dA + dB * dB) / n;
                double clipScale = gradNorm > 10.0 ? 10.0 / gradNorm : 1.0;
                plattA -= calibrationLr * clipScale * dA / n;
                plattB -= calibrationLr * clipScale * dB / n;
            }
        }
        return (double.IsFinite(bestA) ? bestA : 1.0, double.IsFinite(bestB) ? bestB : 0.0);
    }

    private static TabNetCalibrationFit FitTabNetCalibrationStack(
        IReadOnlyList<TrainingSample> fitSet,
        IReadOnlyList<TrainingSample>? diagnosticsSet,
        TabNetWeights w,
        bool fitTemperatureScale,
        int minCalibrationSamples,
        int calibrationEpochs,
        double calibrationLr)
    {
        var evalSet = diagnosticsSet is { Count: > 0 }
            ? diagnosticsSet
            : fitSet;
        if (evalSet.Count == 0)
            evalSet = fitSet;

        var (plattA, plattB) = FitPlattScaling(fitSet, w, minCalibrationSamples, calibrationEpochs, calibrationLr);
        if (!double.IsFinite(plattA)) plattA = 1.0;
        if (!double.IsFinite(plattB)) plattB = 0.0;

        var plattSnapshot = BuildCalibrationSnapshot(plattA, plattB);
        double globalPlattNll = ComputeCalibrationNll(evalSet, w, plattSnapshot);

        double temperatureScale = 0.0;
        double temperatureNll = globalPlattNll;
        string selectedGlobalCalibration = "PLATT";
        var selectedGlobalSnapshot = plattSnapshot;
        if (fitTemperatureScale && fitSet.Count >= minCalibrationSamples)
        {
            double candidateTemperature = FitTemperatureScaling(fitSet, w, minCalibrationSamples, calibrationEpochs, calibrationLr);
            if (double.IsFinite(candidateTemperature) && candidateTemperature > 0.0)
            {
                var temperatureSnapshot = BuildCalibrationSnapshot(plattA, plattB, candidateTemperature);
                temperatureNll = ComputeCalibrationNll(evalSet, w, temperatureSnapshot);
                if (temperatureNll + 1e-6 < globalPlattNll)
                {
                    temperatureScale = candidateTemperature;
                    selectedGlobalCalibration = "TEMPERATURE";
                    selectedGlobalSnapshot = temperatureSnapshot;
                }
            }
        }

        double routingThreshold = DetermineConditionalRoutingThreshold(
            fitSet,
            evalSet,
            w,
            selectedGlobalSnapshot,
            selectedGlobalCalibration,
            minCalibrationSamples);
        var conditionalFit = FitClassConditionalPlatt(
            fitSet, w, plattA, plattB, temperatureScale, routingThreshold, minCalibrationSamples, calibrationEpochs, calibrationLr);
        var preIsotonicSnapshot = BuildCalibrationSnapshot(
            plattA,
            plattB,
            temperatureScale,
            conditionalFit.Buy.A,
            conditionalFit.Buy.B,
            conditionalFit.Sell.A,
            conditionalFit.Sell.B,
            conditionalRoutingThreshold: routingThreshold);
        double preIsotonicNll = ComputeCalibrationNll(evalSet, w, preIsotonicSnapshot);

        double[] isotonicBp = FitIsotonicCalibration(fitSet, w, preIsotonicSnapshot, minCalibrationSamples);
        bool isotonicAccepted = false;
        double postIsotonicNll = preIsotonicNll;
        var finalSnapshot = preIsotonicSnapshot;
        if (isotonicBp.Length >= 4)
        {
            var isotonicSnapshot = BuildCalibrationSnapshot(
                plattA,
                plattB,
                temperatureScale,
                conditionalFit.Buy.A,
                conditionalFit.Buy.B,
                conditionalFit.Sell.A,
                conditionalFit.Sell.B,
                isotonicBp,
                routingThreshold);
            postIsotonicNll = ComputeCalibrationNll(evalSet, w, isotonicSnapshot);
            if (postIsotonicNll + 1e-6 < preIsotonicNll)
            {
                finalSnapshot = isotonicSnapshot;
                isotonicAccepted = true;
            }
            else
            {
                postIsotonicNll = preIsotonicNll;
            }
        }

        var artifact = new TabNetCalibrationArtifact
        {
            SelectedGlobalCalibration = selectedGlobalCalibration,
            CalibrationSelectionStrategy = diagnosticsSet is { Count: > 0 }
                ? "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS"
                : "FIT_AND_EVAL_ON_FIT",
            GlobalPlattNll = globalPlattNll,
            TemperatureNll = temperatureNll,
            TemperatureSelected = string.Equals(selectedGlobalCalibration, "TEMPERATURE", StringComparison.Ordinal),
            FitSampleCount = fitSet.Count,
            DiagnosticsSampleCount = evalSet.Count,
            DiagnosticsSelectedGlobalNll = string.Equals(selectedGlobalCalibration, "TEMPERATURE", StringComparison.Ordinal)
                ? temperatureNll
                : globalPlattNll,
            DiagnosticsSelectedStackNll = isotonicAccepted ? postIsotonicNll : preIsotonicNll,
            ConditionalRoutingThreshold = routingThreshold,
            BuyBranchSampleCount = conditionalFit.Buy.SampleCount,
            BuyBranchBaselineNll = conditionalFit.Buy.BaselineLoss,
            BuyBranchFittedNll = conditionalFit.Buy.FittedLoss,
            BuyBranchAccepted = conditionalFit.Buy.Accepted,
            SellBranchSampleCount = conditionalFit.Sell.SampleCount,
            SellBranchBaselineNll = conditionalFit.Sell.BaselineLoss,
            SellBranchFittedNll = conditionalFit.Sell.FittedLoss,
            SellBranchAccepted = conditionalFit.Sell.Accepted,
            IsotonicSampleCount = fitSet.Count,
            IsotonicBreakpointCount = isotonicAccepted ? finalSnapshot.IsotonicBreakpoints.Length / 2 : 0,
            PreIsotonicNll = preIsotonicNll,
            PostIsotonicNll = postIsotonicNll,
            IsotonicAccepted = isotonicAccepted,
        };

        return new TabNetCalibrationFit(finalSnapshot, preIsotonicSnapshot, artifact);
    }

    private static ClassConditionalPlattFit FitClassConditionalPlatt(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB, double temperatureScale,
        double routingThreshold,
        int minCalibrationSamples, int calibrationEpochs, double calibrationLr)
    {
        var buyPairs = new List<(double Logit, double BaseProb, double Y)>(calSet.Count);
        var sellPairs = new List<(double Logit, double BaseProb, double Y)>(calSet.Count);

        foreach (var sample in calSet)
        {
            double raw = Math.Clamp(TabNetRawProb(sample.Features, w), ProbClampMin, 1.0 - ProbClampMin);
            double rawLogit = Logit(raw);
            double globalCalibP = temperatureScale > 0.0
                ? Sigmoid(rawLogit / temperatureScale)
                : Sigmoid(plattA * rawLogit + plattB);
            double y = sample.Direction > 0 ? 1.0 : 0.0;

            if (globalCalibP >= routingThreshold)
                buyPairs.Add((rawLogit, globalCalibP, y));
            else
                sellPairs.Add((rawLogit, globalCalibP, y));
        }

        return new ClassConditionalPlattFit(
            FitConditionalPlattBranch(buyPairs, minCalibrationSamples, calibrationEpochs, calibrationLr),
            FitConditionalPlattBranch(sellPairs, minCalibrationSamples, calibrationEpochs, calibrationLr));
    }

    private static double DetermineConditionalRoutingThreshold(
        IReadOnlyList<TrainingSample> fitSet,
        IReadOnlyList<TrainingSample> evalSet,
        TabNetWeights w,
        ModelSnapshot globalCalibrationSnapshot,
        string selectedGlobalCalibration,
        int minCalibrationSamples)
    {
        if (fitSet.Count < minCalibrationSamples * 2 || evalSet.Count < Math.Max(8, minCalibrationSamples / 2))
            return 0.5;

        var fitProbs = new double[fitSet.Count];
        for (int i = 0; i < fitSet.Count; i++)
            fitProbs[i] = TabNetCalibProb(fitSet[i].Features, w, globalCalibrationSnapshot);

        var candidates = new SortedSet<double>
        {
            0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65
        };
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.33), 0.35, 0.65));
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.50), 0.35, 0.65));
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.67), 0.35, 0.65));

        double bestThreshold = 0.5;
        double bestEvalNll = ComputeCalibrationNll(evalSet, w, globalCalibrationSnapshot);
        foreach (double threshold in candidates)
        {
            var conditionalFit = FitClassConditionalPlatt(
                fitSet,
                w,
                globalCalibrationSnapshot.PlattA,
                globalCalibrationSnapshot.PlattB,
                string.Equals(selectedGlobalCalibration, "TEMPERATURE", StringComparison.Ordinal)
                    ? globalCalibrationSnapshot.TemperatureScale
                    : 0.0,
                threshold,
                minCalibrationSamples,
                Math.Max(8, minCalibrationSamples),
                0.01);
            var candidateSnapshot = BuildCalibrationSnapshot(
                globalCalibrationSnapshot.PlattA,
                globalCalibrationSnapshot.PlattB,
                globalCalibrationSnapshot.TemperatureScale,
                conditionalFit.Buy.A,
                conditionalFit.Buy.B,
                conditionalFit.Sell.A,
                conditionalFit.Sell.B,
                conditionalRoutingThreshold: threshold);
            double evalNll = ComputeCalibrationNll(evalSet, w, candidateSnapshot);
            if (evalNll + 1e-6 < bestEvalNll)
            {
                bestEvalNll = evalNll;
                bestThreshold = threshold;
            }
        }

        return bestThreshold;
    }

    private static ConditionalPlattBranchFit FitConditionalPlattBranch(
        List<(double Logit, double BaseProb, double Y)> pairs,
        int minCalibrationSamples,
        int calibrationEpochs,
        double calibrationLr)
    {
        if (pairs.Count == 0)
            return new ConditionalPlattBranchFit(0, 0.0, 0.0, 0.0, 0.0);

        double baselineLoss = ComputeConditionalBranchNll(pairs);
        if (pairs.Count < minCalibrationSamples)
            return new ConditionalPlattBranchFit(pairs.Count, baselineLoss, baselineLoss, 0.0, 0.0);

        bool hasPositive = false, hasNegative = false;
        foreach (var (_, _, y) in pairs)
        {
            hasPositive |= y > 0.5;
            hasNegative |= y < 0.5;
            if (hasPositive && hasNegative)
                break;
        }

        if (!hasPositive || !hasNegative)
            return new ConditionalPlattBranchFit(pairs.Count, baselineLoss, baselineLoss, 0.0, 0.0);

        double a = 1.0, b = 0.0;
        double bestA = a, bestB = b, bestLoss = baselineLoss;

        for (int ep = 0; ep < calibrationEpochs; ep++)
        {
            double dA = 0.0, dB = 0.0;
            for (int i = 0; i < pairs.Count; i++)
            {
                double calibP = Sigmoid(a * pairs[i].Logit + b);
                double err = calibP - pairs[i].Y;
                dA += err * pairs[i].Logit;
                dB += err;
            }

            a -= calibrationLr * dA / pairs.Count;
            b -= calibrationLr * dB / pairs.Count;

            double loss = ComputeConditionalBranchNll(pairs, a, b);
            if (!double.IsFinite(loss))
                return new ConditionalPlattBranchFit(pairs.Count, baselineLoss, baselineLoss, 0.0, 0.0);

            if (loss < bestLoss)
            {
                bestLoss = loss;
                bestA = a;
                bestB = b;
            }
        }

        bool accepted = bestLoss + 1e-6 < baselineLoss;
        return new ConditionalPlattBranchFit(
            pairs.Count,
            baselineLoss,
            bestLoss,
            accepted ? bestA : 0.0,
            accepted ? bestB : 0.0);
    }

    private static double ComputeConditionalBranchNll(
        IReadOnlyList<(double Logit, double BaseProb, double Y)> pairs,
        double? plattA = null,
        double? plattB = null)
    {
        if (pairs.Count == 0)
            return 0.0;

        double loss = 0.0;
        for (int i = 0; i < pairs.Count; i++)
        {
            double p = plattA.HasValue && plattB.HasValue
                ? Sigmoid(plattA.Value * pairs[i].Logit + plattB.Value)
                : Math.Clamp(pairs[i].BaseProb, ProbClampMin, 1.0 - ProbClampMin);
            loss -= pairs[i].Y * Math.Log(Math.Max(p, ProbClampMin))
                  + (1.0 - pairs[i].Y) * Math.Log(Math.Max(1.0 - p, ProbClampMin));
        }

        return loss / pairs.Count;
    }

    private static double ComputeCalibrationNll(
        IReadOnlyList<TrainingSample> samples,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot)
    {
        if (samples.Count == 0)
            return 0.0;

        double loss = 0.0;
        for (int i = 0; i < samples.Count; i++)
        {
            double p = TabNetCalibProb(samples[i].Features, w, calibrationSnapshot);
            double y = samples[i].Direction > 0 ? 1.0 : 0.0;
            loss -= y * Math.Log(Math.Max(p, ProbClampMin))
                  + (1.0 - y) * Math.Log(Math.Max(1.0 - p, ProbClampMin));
        }

        return loss / samples.Count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISOTONIC CALIBRATION (PAVA)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        int minCalibrationSamples)
    {
        if (calSet.Count < minCalibrationSamples) return [];
        var pairs = new (double X, double Y)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            pairs[i] = (TabNetCalibProb(calSet[i].Features, w, calibrationSnapshot),
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

        // Post-PAVA: merge blocks with fewer than MinBlockSize samples into adjacent blocks
        const int MinIsotonicBlockSize = 5;
        for (int i = blocks.Count - 1; i >= 1; i--)
        {
            if (blocks[i].Count < MinIsotonicBlockSize)
            {
                blocks[i - 1] = (blocks[i - 1].SumY + blocks[i].SumY,
                    blocks[i - 1].Count + blocks[i].Count,
                    blocks[i - 1].XMin, blocks[i].XMax);
                blocks.RemoveAt(i);
            }
        }
        // Handle first block being too small
        if (blocks.Count >= 2 && blocks[0].Count < MinIsotonicBlockSize)
        {
            blocks[1] = (blocks[0].SumY + blocks[1].SumY,
                blocks[0].Count + blocks[1].Count,
                blocks[0].XMin, blocks[1].XMax);
            blocks.RemoveAt(0);
        }

        var bp = new List<double>();
        foreach (var block in blocks)
        {
            bp.Add((block.XMin + block.XMax) / 2.0);
            bp.Add(block.SumY / block.Count);
        }
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
                double frac = Math.Clamp((p - bp[i]) / (bp[i + 2] - bp[i] + Eps), 0.0, 1.0);
                return bp[i + 1] + frac * (bp[i + 3] - bp[i + 1]);
            }
        }
        return bp[^1];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TEMPERATURE SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        int minCalibrationSamples,
        int calibrationEpochs,
        double calibrationLr)
    {
        if (calSet.Count < minCalibrationSamples) return 1.0;
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TabNetRawProb(calSet[i].Features, w), ProbClampMin, 1.0 - ProbClampMin);
            logits[i] = Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double temperature = 1.0;
        double bestTemperature = 1.0;
        double bestLoss = double.PositiveInfinity;
        for (int ep = 0; ep < calibrationEpochs; ep++)
        {
            double dT = 0, loss = 0;
            for (int i = 0; i < n; i++)
            {
                double p = Sigmoid(logits[i] / temperature);
                dT += (p - labels[i]) * (-logits[i] / (temperature * temperature));
                loss -= labels[i] * Math.Log(Math.Max(p, ProbClampMin))
                      + (1.0 - labels[i]) * Math.Log(Math.Max(1.0 - p, ProbClampMin));
            }
            loss /= n;
            if (double.IsFinite(loss) && loss < bestLoss)
            {
                bestLoss = loss;
                bestTemperature = temperature;
            }
            // Gradient clipping
            double gradMag = Math.Abs(dT / n);
            double clipScale = gradMag > 10.0 ? 10.0 / gradMag : 1.0;
            temperature -= calibrationLr * clipScale * dT / n;
            temperature = Math.Max(0.01, temperature);
        }
        return bestTemperature;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ECE, THRESHOLD, KELLY, BSS
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeEce(
        IReadOnlyList<TrainingSample> testSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        int bins = 10)
    {
        if (testSet.Count < bins) return 1.0;
        var binCorrect = new double[bins];
        var binConf = new double[bins];
        var binCount = new int[bins];
        foreach (var sample in testSet)
        {
            double p = TabNetCalibProb(sample.Features, w, calibrationSnapshot);
            int bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin] += p;
            binCorrect[bin] += sample.Direction > 0 ? 1 : 0;
            binCount[bin]++;
        }

        double ece = 0;
        int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            ece += Math.Abs(binCorrect[b] / binCount[b] - binConf[b] / binCount[b]) * binCount[b] / n;
        }
        return ece;
    }

    /// <summary>
    /// Adaptive (equal-count) ECE: partitions samples into equal-size bins by predicted probability.
    /// More robust than equal-width ECE when predictions cluster in a narrow range.
    /// </summary>
    private static double ComputeAdaptiveEce(
        IReadOnlyList<TrainingSample> testSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        int bins = 10)
    {
        int n = testSet.Count;
        if (n < bins) return 1.0;
        var pairs = new (double P, int Y)[n];
        for (int i = 0; i < n; i++)
        {
            double p = TabNetCalibProb(testSet[i].Features, w, calibrationSnapshot);
            pairs[i] = (p, testSet[i].Direction > 0 ? 1 : 0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        double ece = 0;
        int binSize = n / bins;
        for (int b = 0; b < bins; b++)
        {
            int start = b * binSize;
            int end = b == bins - 1 ? n : start + binSize;
            int count = end - start;
            if (count == 0) continue;
            double sumConf = 0, sumCorrect = 0;
            for (int i = start; i < end; i++) { sumConf += pairs[i].P; sumCorrect += pairs[i].Y; }
            ece += Math.Abs(sumCorrect / count - sumConf / count) * count / n;
        }
        return ece;
    }

    private static double ComputeOptimalThreshold(
        IReadOnlyList<TrainingSample> dataSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        int searchMin = 30,
        int searchMax = 75)
    {
        if (dataSet.Count < 30) return 0.5;
        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = TabNetCalibProb(dataSet[i].Features, w, calibrationSnapshot);

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

    private static double ComputeAvgKellyFraction(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        int minCalibrationSamples)
    {
        if (calSet.Count < minCalibrationSamples) return 0;
        double kellySum = 0;
        foreach (var sample in calSet)
            kellySum += Math.Max(0, (2 * TabNetCalibProb(sample.Features, w, calibrationSnapshot) - 1) * 0.5);
        return kellySum / calSet.Count;
    }

    private static double ComputeBrierSkillScore(
        IReadOnlyList<TrainingSample> testSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        int minCalibrationSamples)
    {
        if (testSet.Count < minCalibrationSamples) return 0;
        int n = testSet.Count;
        double baseRate = testSet.Count(s => s.Direction > 0) / (double)n;
        double brierNaive = baseRate * (1 - baseRate), brierModel = 0;
        foreach (var sample in testSet)
        {
            double p = TabNetCalibProb(sample.Features, w, calibrationSnapshot);
            int y = sample.Direction > 0 ? 1 : 0;
            brierModel += (p - y) * (p - y);
        }
        brierModel /= n;
        return brierNaive > 1e-10 ? 1.0 - brierModel / brierNaive : 0;
    }

    private static (double Mean, double Std, double Threshold) ComputeCalibrationResidualStats(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        int minCalibrationSamples)
    {
        if (calSet.Count < minCalibrationSamples)
            return (0.0, 0.0, 0.0);

        var residuals = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = TabNetCalibProb(calSet[i].Features, w, calibrationSnapshot);
            double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals[i] = Math.Abs(p - y);
        }

        double mean = residuals.Average();
        double std = residuals.Length > 1 ? StdDev(residuals, mean) : 0.0;
        double threshold = Quantile(residuals, 0.90);
        return (mean, std, threshold);
    }
}
