using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    private readonly record struct GbmCalibrationState(
        double GlobalPlattA,
        double GlobalPlattB,
        double TemperatureScale,
        double PlattABuy,
        double PlattBBuy,
        double PlattASell,
        double PlattBSell,
        double ConditionalRoutingThreshold,
        double[] IsotonicBreakpoints)
    {
        internal static readonly GbmCalibrationState Default = new(
            GlobalPlattA: 1.0,
            GlobalPlattB: 0.0,
            TemperatureScale: 0.0,
            PlattABuy: 0.0,
            PlattBBuy: 0.0,
            PlattASell: 0.0,
            PlattBSell: 0.0,
            ConditionalRoutingThreshold: 0.5,
            IsotonicBreakpoints: []);
    }

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

    private readonly record struct GbmCalibrationPartition(
        List<TrainingSample> FitSet,
        List<TrainingSample> DiagnosticsSet,
        List<TrainingSample> ConformalSet,
        List<TrainingSample> MetaLabelSet,
        List<TrainingSample> AbstentionSet,
        int FitStartIndex,
        int DiagnosticsStartIndex,
        int ConformalStartIndex,
        int MetaLabelStartIndex,
        int AbstentionStartIndex,
        string AdaptiveHeadSplitMode);

    private static ModelSnapshot CreateCalibrationSnapshot(in GbmCalibrationState state)
    {
        return new ModelSnapshot
        {
            PlattA = state.GlobalPlattA,
            PlattB = state.GlobalPlattB,
            TemperatureScale = state.TemperatureScale,
            PlattABuy = state.PlattABuy,
            PlattBBuy = state.PlattBBuy,
            PlattASell = state.PlattASell,
            PlattBSell = state.PlattBSell,
            ConditionalCalibrationRoutingThreshold = state.ConditionalRoutingThreshold,
            IsotonicBreakpoints = state.IsotonicBreakpoints,
        };
    }

    private static GbmCalibrationArtifact BuildCalibrationArtifact(
        IReadOnlyList<TrainingSample> fitSet,
        IReadOnlyList<TrainingSample> diagnosticsSet,
        IReadOnlyList<TrainingSample> conformalSet,
        IReadOnlyList<TrainingSample> metaLabelSet,
        IReadOnlyList<TrainingSample> abstentionSet,
        string adaptiveHeadMode,
        int adaptiveHeadCrossFitFoldCount,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double learningRate,
        int featureCount,
        in GbmCalibrationState state,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        var evalSet = diagnosticsSet.Count > 0 ? diagnosticsSet : fitSet;
        var globalPlattSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
            GlobalPlattA: state.GlobalPlattA,
            GlobalPlattB: state.GlobalPlattB,
            TemperatureScale: 0.0,
            PlattABuy: 0.0,
            PlattBBuy: 0.0,
            PlattASell: 0.0,
            PlattBSell: 0.0,
            ConditionalRoutingThreshold: 0.5,
            IsotonicBreakpoints: []));
        var selectedGlobalSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
            GlobalPlattA: state.GlobalPlattA,
            GlobalPlattB: state.GlobalPlattB,
            TemperatureScale: state.TemperatureScale,
            PlattABuy: 0.0,
            PlattBBuy: 0.0,
            PlattASell: 0.0,
            PlattBSell: 0.0,
            ConditionalRoutingThreshold: state.ConditionalRoutingThreshold,
            IsotonicBreakpoints: []));
        var preIsotonicSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
            GlobalPlattA: state.GlobalPlattA,
            GlobalPlattB: state.GlobalPlattB,
            TemperatureScale: state.TemperatureScale,
            PlattABuy: state.PlattABuy,
            PlattBBuy: state.PlattBBuy,
            PlattASell: state.PlattASell,
            PlattBSell: state.PlattBSell,
            ConditionalRoutingThreshold: state.ConditionalRoutingThreshold,
            IsotonicBreakpoints: []));
        var finalSnapshot = CreateCalibrationSnapshot(state);

        double globalPlattNll = ComputeCalibrationNll(
            evalSet, trees, baseLogOdds, learningRate, featureCount, globalPlattSnapshot, perTreeLearningRates);
        double temperatureNll = state.TemperatureScale > 0.0
            ? ComputeCalibrationNll(evalSet, trees, baseLogOdds, learningRate, featureCount, selectedGlobalSnapshot, perTreeLearningRates)
            : globalPlattNll;

        var conditionalFit = FitClassConditionalPlatt(
            fitSet,
            trees,
            baseLogOdds,
            learningRate,
            featureCount,
            perTreeLearningRates,
            state.ConditionalRoutingThreshold,
            selectedGlobalSnapshot);

        double preIsotonicNll = ComputeCalibrationNll(
            evalSet, trees, baseLogOdds, learningRate, featureCount, preIsotonicSnapshot, perTreeLearningRates);
        double postIsotonicNll = ComputeCalibrationNll(
            evalSet, trees, baseLogOdds, learningRate, featureCount, finalSnapshot, perTreeLearningRates);

        return new GbmCalibrationArtifact
        {
            SelectedGlobalCalibration = state.TemperatureScale > 0.0 ? "TEMPERATURE" : "PLATT",
            CalibrationSelectionStrategy = diagnosticsSet.Count > 0
                ? "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS"
                : "FIT_AND_EVAL_ON_FIT",
            GlobalPlattNll = globalPlattNll,
            TemperatureNll = temperatureNll,
            TemperatureSelected = state.TemperatureScale > 0.0,
            FitSampleCount = fitSet.Count,
            DiagnosticsSampleCount = evalSet.Count,
            DiagnosticsSelectedGlobalNll = state.TemperatureScale > 0.0 ? temperatureNll : globalPlattNll,
            DiagnosticsSelectedStackNll = postIsotonicNll,
            ConformalSampleCount = conformalSet.Count,
            MetaLabelSampleCount = metaLabelSet.Count,
            AbstentionSampleCount = abstentionSet.Count,
            AdaptiveHeadMode = adaptiveHeadMode,
            AdaptiveHeadCrossFitFoldCount = adaptiveHeadCrossFitFoldCount,
            ConditionalRoutingThreshold = state.ConditionalRoutingThreshold,
            BuyBranchSampleCount = conditionalFit.Buy.SampleCount,
            BuyBranchBaselineNll = conditionalFit.Buy.BaselineLoss,
            BuyBranchFittedNll = conditionalFit.Buy.FittedLoss,
            BuyBranchAccepted = conditionalFit.Buy.Accepted,
            SellBranchSampleCount = conditionalFit.Sell.SampleCount,
            SellBranchBaselineNll = conditionalFit.Sell.BaselineLoss,
            SellBranchFittedNll = conditionalFit.Sell.FittedLoss,
            SellBranchAccepted = conditionalFit.Sell.Accepted,
            IsotonicSampleCount = fitSet.Count,
            IsotonicBreakpointCount = state.IsotonicBreakpoints.Length / 2,
            PreIsotonicNll = preIsotonicNll,
            PostIsotonicNll = postIsotonicNll,
            IsotonicAccepted = state.IsotonicBreakpoints.Length >= 4 && postIsotonicNll <= preIsotonicNll + 1e-6,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLATT SCALING (Item 10: convergence check)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
            raw       = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i] = Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double sgdLr = 0.01;
        const int maxEpochs = 200;

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            plattA -= sgdLr * dA / n;
            plattB -= sgdLr * dB / n;

            // Item 10: convergence check
            if (Math.Abs(dA / n) + Math.Abs(dB / n) < 1e-6) break;
        }

        return (plattA, plattB);
    }

    /// <summary>Item 12: Temperature scaling via Brent's method (golden section search).</summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        // Precompute logits
        var logits = new double[calSet.Count]; var labels = new int[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double rawP = Math.Clamp(GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates), 1e-7, 1.0 - 1e-7);
            logits[i] = Logit(rawP);
            labels[i] = calSet[i].Direction > 0 ? 1 : 0;
        }

        double TempLoss(double T)
        {
            double loss = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double p = Sigmoid(logits[i] / T);
                loss -= labels[i] * Math.Log(p + 1e-15) + (1 - labels[i]) * Math.Log(1 - p + 1e-15);
            }
            return loss / calSet.Count;
        }

        // Golden section search on [0.1, 10.0]
        double a = 0.1, bnd = 10.0;
        const double phi = 0.6180339887;
        double x1 = bnd - phi * (bnd - a), x2 = a + phi * (bnd - a);
        double f1 = TempLoss(x1), f2 = TempLoss(x2);

        for (int iter = 0; iter < 50; iter++)
        {
            if (f1 < f2) { bnd = x2; x2 = x1; f2 = f1; x1 = bnd - phi * (bnd - a); f1 = TempLoss(x1); }
            else { a = x1; x1 = x2; f1 = f2; x2 = a + phi * (bnd - a); f2 = TempLoss(x2); }
            if (Math.Abs(bnd - a) < 0.001) break;
        }
        return (a + bnd) / 2.0;
    }

    private static ClassConditionalPlattFit FitClassConditionalPlatt(
        IReadOnlyList<TrainingSample> calSet,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double lr,
        int featureCount,
        IReadOnlyList<double>? perTreeLearningRates = null,
        double routingThreshold = 0.5,
        ModelSnapshot? globalCalibrationSnapshot = null)
    {
        if (calSet.Count < 20)
            return new ClassConditionalPlattFit(
                new ConditionalPlattBranchFit(0, 0.0, 0.0, 0.0, 0.0),
                new ConditionalPlattBranchFit(0, 0.0, 0.0, 0.0, 0.0));

        var buyPairs = new List<(double Logit, double BaseProb, double Y)>(calSet.Count);
        var sellPairs = new List<(double Logit, double BaseProb, double Y)>(calSet.Count);
        double effectiveRoutingThreshold = Math.Clamp(
            double.IsFinite(routingThreshold) ? routingThreshold : 0.5,
            0.01,
            0.99);

        foreach (var sample in calSet)
        {
            double raw = Math.Clamp(GbmProb(sample.Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates), 1e-7, 1.0 - 1e-7);
            double rawLogit = Logit(raw);
            double baseProb = globalCalibrationSnapshot is null
                ? raw
                : InferenceHelpers.ApplyDeployedCalibration(raw, globalCalibrationSnapshot);
            double y = sample.Direction > 0 ? 1.0 : 0.0;

            if (baseProb >= effectiveRoutingThreshold)
                buyPairs.Add((rawLogit, baseProb, y));
            else
                sellPairs.Add((rawLogit, baseProb, y));
        }

        return new ClassConditionalPlattFit(
            FitConditionalPlattBranch(buyPairs),
            FitConditionalPlattBranch(sellPairs));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISOTONIC CALIBRATION (Item 11: boundary extrapolation)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count < 10) return [];

        var pairs = new (double X, double Y)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            pairs[i] = (GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates),
                calSet[i].Direction > 0 ? 1.0 : 0.0);
        Array.Sort(pairs, (a, b) => a.X.CompareTo(b.X));

        var blocks = new List<(double SumY, int Count, double XMin, double XMax)>();
        foreach (var (x, y) in pairs)
        {
            blocks.Add((y, 1, x, x));
            while (blocks.Count >= 2)
            {
                var last = blocks[^1]; var prev = blocks[^2];
                if ((double)prev.SumY / prev.Count <= (double)last.SumY / last.Count) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (prev.SumY + last.SumY, prev.Count + last.Count, prev.XMin, last.XMax);
            }
        }

        var bp = new List<double>();
        foreach (var b in blocks) { bp.Add((b.XMin + b.XMax) / 2.0); bp.Add(b.SumY / b.Count); }
        return bp.ToArray();
    }

    /// <summary>Item 11: Apply isotonic with linear extrapolation beyond boundaries.</summary>
    private static double ApplyIsotonic(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        // Below first breakpoint: linear extrapolation
        if (p <= bp[0])
        {
            if (bp.Length >= 4)
            {
                double slope = (bp[3] - bp[1]) / (bp[2] - bp[0] + 1e-15);
                return Math.Clamp(bp[1] + slope * (p - bp[0]), 0.0, 1.0);
            }
            return bp[1];
        }
        // Above last breakpoint: linear extrapolation
        if (p >= bp[^2])
        {
            if (bp.Length >= 4)
            {
                double slope = (bp[^1] - bp[^3]) / (bp[^2] - bp[^4] + 1e-15);
                return Math.Clamp(bp[^1] + slope * (p - bp[^2]), 0.0, 1.0);
            }
            return bp[^1];
        }
        // Interior: linear interpolation
        for (int i = 0; i < bp.Length - 2; i += 2)
        {
            if (i + 2 < bp.Length && p <= bp[i + 2])
            {
                double frac = (p - bp[i]) / (bp[i + 2] - bp[i] + 1e-15);
                return bp[i + 1] + frac * (bp[i + 3] - bp[i + 1]);
            }
        }
        return bp[^1];
    }

    private static double DetermineConditionalRoutingThreshold(
        IReadOnlyList<TrainingSample> fitSet,
        IReadOnlyList<TrainingSample> evalSet,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double lr,
        int featureCount,
        ModelSnapshot globalCalibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (fitSet.Count < 20 || evalSet.Count < 8)
            return 0.5;

        var fitProbs = new double[fitSet.Count];
        for (int i = 0; i < fitSet.Count; i++)
            fitProbs[i] = GbmCalibProb(fitSet[i].Features, trees, baseLogOdds, lr, featureCount, globalCalibrationSnapshot, perTreeLearningRates);

        var candidates = new SortedSet<double>
        {
            0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65
        };
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.33), 0.35, 0.65));
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.50), 0.35, 0.65));
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.67), 0.35, 0.65));

        double bestThreshold = 0.5;
        double bestEvalNll = ComputeCalibrationNll(evalSet, trees, baseLogOdds, lr, featureCount, globalCalibrationSnapshot, perTreeLearningRates);
        foreach (double threshold in candidates)
        {
            var conditionalFit = FitClassConditionalPlatt(
                fitSet,
                trees,
                baseLogOdds,
                lr,
                featureCount,
                perTreeLearningRates,
                threshold,
                globalCalibrationSnapshot);
            var candidateSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
                GlobalPlattA: globalCalibrationSnapshot.PlattA,
                GlobalPlattB: globalCalibrationSnapshot.PlattB,
                TemperatureScale: globalCalibrationSnapshot.TemperatureScale,
                PlattABuy: conditionalFit.Buy.A,
                PlattBBuy: conditionalFit.Buy.B,
                PlattASell: conditionalFit.Sell.A,
                PlattBSell: conditionalFit.Sell.B,
                ConditionalRoutingThreshold: threshold,
                IsotonicBreakpoints: []));
            double evalNll = ComputeCalibrationNll(evalSet, trees, baseLogOdds, lr, featureCount, candidateSnapshot, perTreeLearningRates);
            if (evalNll + 1e-6 < bestEvalNll)
            {
                bestEvalNll = evalNll;
                bestThreshold = threshold;
            }
        }

        return bestThreshold;
    }

    private static double ComputeCalibrationNll(
        IReadOnlyList<TrainingSample> samples,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double lr,
        int featureCount,
        ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (samples.Count == 0)
            return 0.0;

        double loss = 0.0;
        for (int i = 0; i < samples.Count; i++)
        {
            double p = GbmCalibProb(samples[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double y = samples[i].Direction > 0 ? 1.0 : 0.0;
            loss -= y * Math.Log(Math.Max(p, 1e-7))
                  + (1.0 - y) * Math.Log(Math.Max(1.0 - p, 1e-7));
        }

        return loss / samples.Count;
    }

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count == 0) return 0;
        double sum = 0;
        foreach (var s in calSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            sum += Math.Max(0, 2 * p - 1);
        }
        return sum / calSet.Count * 0.5;
    }

    /// <summary>Item 23: EV-optimal threshold with optional transaction cost subtraction.</summary>
    private static double ComputeOptimalThreshold(
        List<TrainingSample> dataSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null,
        int searchMin = 30, int searchMax = 75, double spreadCost = 0.0, int stepBps = 50)
    {
        if (dataSet.Count < 30) return 0.5;
        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = GbmCalibProb(dataSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);

        double bestEv = double.MinValue; double bestT = 0.5;
        int minBps = Math.Max(1, searchMin * 100);
        int maxBps = Math.Max(minBps, searchMax * 100);
        int effectiveStepBps = stepBps > 0 ? stepBps : 50;
        for (int thresholdBps = minBps; thresholdBps <= maxBps; thresholdBps += effectiveStepBps)
        {
            double t = thresholdBps / 10_000.0;
            double ev = 0;
            for (int i = 0; i < dataSet.Count; i++)
            {
                bool correct = (probs[i] >= t) == (dataSet[i].Direction > 0);
                double mag = Math.Abs(dataSet[i].Magnitude) - spreadCost;
                ev += (correct ? 1 : -1) * Math.Max(0, mag);
            }
            ev /= dataSet.Count;
            if (ev > bestEv) { bestEv = ev; bestT = t; }
        }
        return bestT;
    }

    private static GbmCalibrationPartition BuildCalibrationPartition(
        List<TrainingSample> calibrationSet,
        int calibrationStartIndex)
    {
        if (calibrationSet.Count == 0)
        {
            return new GbmCalibrationPartition(
                FitSet: [],
                DiagnosticsSet: [],
                ConformalSet: [],
                MetaLabelSet: [],
                AbstentionSet: [],
                FitStartIndex: calibrationStartIndex,
                DiagnosticsStartIndex: calibrationStartIndex,
                ConformalStartIndex: calibrationStartIndex,
                MetaLabelStartIndex: calibrationStartIndex,
                AbstentionStartIndex: calibrationStartIndex,
                AdaptiveHeadSplitMode: "SHARED_FALLBACK");
        }

        if (calibrationSet.Count < 40)
        {
            return new GbmCalibrationPartition(
                FitSet: calibrationSet,
                DiagnosticsSet: calibrationSet,
                ConformalSet: calibrationSet,
                MetaLabelSet: calibrationSet,
                AbstentionSet: calibrationSet,
                FitStartIndex: calibrationStartIndex,
                DiagnosticsStartIndex: calibrationStartIndex,
                ConformalStartIndex: calibrationStartIndex,
                MetaLabelStartIndex: calibrationStartIndex,
                AbstentionStartIndex: calibrationStartIndex,
                AdaptiveHeadSplitMode: "SHARED_FALLBACK");
        }

        int fitCount = calibrationSet.Count >= 20
            ? Math.Clamp(calibrationSet.Count / 2, 10, calibrationSet.Count - 10)
            : calibrationSet.Count;
        fitCount = Math.Clamp(fitCount, 1, calibrationSet.Count);
        int diagnosticsCount = calibrationSet.Count - fitCount;

        var fitSet = calibrationSet[..fitCount];
        var diagnosticsSet = diagnosticsCount > 0 ? calibrationSet[fitCount..] : calibrationSet;
        int diagnosticsStartIndex = diagnosticsCount > 0
            ? calibrationStartIndex + fitCount
            : calibrationStartIndex;
        var conformalSet = diagnosticsSet;
        var metaLabelSet = diagnosticsSet;
        var abstentionSet = diagnosticsSet;
        int conformalStartIndex = diagnosticsStartIndex;
        int metaLabelStartIndex = diagnosticsStartIndex;
        int abstentionStartIndex = diagnosticsStartIndex;
        string mode = "SHARED_FALLBACK";

        const int minConformalSamples = 10;
        const int minAdaptiveHeadSamples = 10;
        if (diagnosticsSet.Count >= minConformalSamples + minAdaptiveHeadSamples + minAdaptiveHeadSamples)
        {
            int conformalCount = Math.Max(minConformalSamples, diagnosticsSet.Count / 3);
            conformalCount = Math.Min(conformalCount, diagnosticsSet.Count - (minAdaptiveHeadSamples * 2));
            int remaining = diagnosticsSet.Count - conformalCount;
            int metaCount = remaining / 2;
            int abstentionCount = diagnosticsSet.Count - conformalCount - metaCount;
            if (metaCount >= minAdaptiveHeadSamples && abstentionCount >= minAdaptiveHeadSamples)
            {
                conformalSet = diagnosticsSet[..conformalCount];
                metaLabelSet = diagnosticsSet[conformalCount..(conformalCount + metaCount)];
                abstentionSet = diagnosticsSet[(conformalCount + metaCount)..];
                conformalStartIndex = diagnosticsStartIndex;
                metaLabelStartIndex = diagnosticsStartIndex + conformalCount;
                abstentionStartIndex = diagnosticsStartIndex + conformalCount + metaCount;
                mode = "DISJOINT";
            }
        }
        else if (diagnosticsSet.Count >= minConformalSamples + minAdaptiveHeadSamples)
        {
            conformalSet = diagnosticsSet[..minConformalSamples];
            metaLabelSet = diagnosticsSet[minConformalSamples..];
            abstentionSet = metaLabelSet;
            conformalStartIndex = diagnosticsStartIndex;
            metaLabelStartIndex = diagnosticsStartIndex + minConformalSamples;
            abstentionStartIndex = metaLabelStartIndex;
            mode = "CONFORMAL_DISJOINT_SHARED_ADAPTIVE";
        }

        return new GbmCalibrationPartition(
            FitSet: fitSet,
            DiagnosticsSet: diagnosticsSet,
            ConformalSet: conformalSet,
            MetaLabelSet: metaLabelSet,
            AbstentionSet: abstentionSet,
            FitStartIndex: calibrationStartIndex,
            DiagnosticsStartIndex: diagnosticsStartIndex,
            ConformalStartIndex: conformalStartIndex,
            MetaLabelStartIndex: metaLabelStartIndex,
            AbstentionStartIndex: abstentionStartIndex,
            AdaptiveHeadSplitMode: mode);
    }
}
