using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION
    // ═══════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  numRounds,
        int                  maxDepth,
        double               learningRate,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);
        int barsPerDay = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;

        if (foldSize < 50)
        {
            _logger.LogWarning("GBM walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];
        int[][]? interactionConstraints = null;
        if (!string.IsNullOrEmpty(hp.GbmInteractionConstraints))
        {
            try
            {
                interactionConstraints = JsonSerializer.Deserialize<int[][]>(hp.GbmInteractionConstraints);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "GBM CV: failed to parse interaction constraints, ignoring.");
            }
        }

        // Item 42: configurable CV parallelism; Item 46: deterministic = sequential
        int maxParallelism = hp.GbmDeterministic ? 1 : (hp.GbmCvMaxParallelism > 0 ? hp.GbmCvMaxParallelism : -1);
        var parallelOpts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxParallelism };

        Parallel.For(0, folds, parallelOpts, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples) return;

            var rawFoldTrain = samples[..trainEnd].ToList();

            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < rawFoldTrain.Count)
                    rawFoldTrain = rawFoldTrain[..purgeFrom];
            }

            var rawFoldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (rawFoldTest.Count < 20) return;

            int cvCalSize = Math.Max(10, rawFoldTrain.Count / 8);
            if (rawFoldTrain.Count - cvCalSize < 20) return;
            var rawFoldCal = rawFoldTrain[^cvCalSize..];
            rawFoldTrain = rawFoldTrain[..^cvCalSize];

            if (hp.GbmConceptDriftGate && rawFoldTrain.Count >= hp.MinSamples * 2)
                rawFoldTrain = ApplyConceptDriftGate(rawFoldTrain, featureCount, hp.MinSamples);

            if (rawFoldTrain.Count < 20) return;

            var (foldMeans, foldStds) = ComputeStandardizationFromSamples(rawFoldTrain);
            var foldTrain = StandardizeSamples(rawFoldTrain, foldMeans, foldStds);
            var foldCal = StandardizeSamples(rawFoldCal, foldMeans, foldStds);
            var foldTest = StandardizeSamples(rawFoldTest, foldMeans, foldStds);

            double[]? foldDensityWeights = null;
            if (hp.DensityRatioWindowDays > 0 && foldTrain.Count >= 50)
                foldDensityWeights = ComputeDensityRatioImportanceWeights(foldTrain, featureCount, hp.DensityRatioWindowDays, barsPerDay);

            int cvRounds = Math.Max(10, numRounds / 3);
            var (cvTrees, cvBLO, _, _, cvPerTreeLr) = FitGbmEnsemble(
                foldTrain, featureCount, cvRounds, maxDepth, learningRate, hp.LabelSmoothing,
                null, foldDensityWeights, hp, ct, interactionConstraints);
            var (cvA, cvB) = FitPlattScaling(foldCal, cvTrees, cvBLO, learningRate, featureCount, cvPerTreeLr);
            double cvTemp = hp.FitTemperatureScale && foldCal.Count >= 10
                ? FitTemperatureScaling(foldCal, cvTrees, cvBLO, learningRate, featureCount, cvPerTreeLr)
                : 0.0;
            var cvGlobalCalibrationSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
                GlobalPlattA: cvA,
                GlobalPlattB: cvB,
                TemperatureScale: cvTemp,
                PlattABuy: 0.0,
                PlattBBuy: 0.0,
                PlattASell: 0.0,
                PlattBSell: 0.0,
                ConditionalRoutingThreshold: 0.5,
                IsotonicBreakpoints: []));
            int cvRoutingFitCount = Math.Max(10, foldCal.Count * 2 / 3);
            double cvRoutingThreshold = DetermineConditionalRoutingThreshold(
                foldCal[..Math.Min(cvRoutingFitCount, foldCal.Count)],
                foldCal[Math.Min(cvRoutingFitCount, foldCal.Count)..],
                cvTrees,
                cvBLO,
                learningRate,
                featureCount,
                cvGlobalCalibrationSnapshot,
                cvPerTreeLr);
            var cvConditionalFit = FitClassConditionalPlatt(
                foldCal, cvTrees, cvBLO, learningRate, featureCount, cvPerTreeLr, cvRoutingThreshold, cvGlobalCalibrationSnapshot);
            double cvABuy = cvConditionalFit.Buy.A;
            double cvBBuy = cvConditionalFit.Buy.B;
            double cvASell = cvConditionalFit.Sell.A;
            double cvBSell = cvConditionalFit.Sell.B;
            var cvCalibrationState = new GbmCalibrationState(
                GlobalPlattA: cvA,
                GlobalPlattB: cvB,
                TemperatureScale: cvTemp,
                PlattABuy: cvABuy,
                PlattBBuy: cvBBuy,
                PlattASell: cvASell,
                PlattBSell: cvBSell,
                ConditionalRoutingThreshold: cvRoutingThreshold,
                IsotonicBreakpoints: []);
            var cvCalibrationSnapshot = CreateCalibrationSnapshot(cvCalibrationState);
            double[] cvIsotonic = FitIsotonicCalibration(foldCal, cvTrees, cvBLO, learningRate, featureCount, cvCalibrationSnapshot, cvPerTreeLr);
            cvCalibrationState = cvCalibrationState with { IsotonicBreakpoints = cvIsotonic };
            cvCalibrationSnapshot = CreateCalibrationSnapshot(cvCalibrationState);
            double cvThreshold = ComputeOptimalThreshold(
                foldCal, cvTrees, cvBLO, learningRate, featureCount, cvCalibrationSnapshot, cvPerTreeLr,
                hp.ThresholdSearchMin, hp.ThresholdSearchMax, hp.GbmEvThresholdSpreadCost, hp.ThresholdSearchStepBps);
            var m = EvaluateGbm(foldTest, cvTrees, cvBLO, learningRate, [], 0, featureCount, cvCalibrationSnapshot, cvPerTreeLr, cvThreshold);

            var foldImpF = ComputeGainWeightedImportance(cvTrees, featureCount); // Item 39
            var foldImp = Array.ConvertAll(foldImpF, f => (double)f);

            var predictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int i = 0; i < foldTest.Count; i++)
            {
                double p = GbmCalibProb(foldTest[i].Features, cvTrees, cvBLO, learningRate, featureCount, cvCalibrationSnapshot, cvPerTreeLr);
                predictions[i] = (p >= cvThreshold ? 1 : -1, foldTest[i].Direction > 0 ? 1 : -1);
            }
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(predictions);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBad);
        });

        // Aggregate
        var accList = new List<double>(folds);
        var f1List  = new List<double>(folds);
        var evList  = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc);
            f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV);
            sharpeList.Add(r.Value.Sharpe);
            foldImportances.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning("GBM equity-curve gate: {BadFolds}/{TotalFolds} folds failed", badFolds, accList.Count);

        double avgAcc = accList.Average();
        double stdAcc = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning("GBM Sharpe trend gate: slope={Slope:F3} < threshold", sharpeTrend);
            equityCurveGateFailed = true;
        }

        // Item 33: rank-dispersion feature stability across folds
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = ComputeRankStability(foldImportances, featureCount);
        }

        return (new WalkForwardResult(
            AvgAccuracy:            avgAcc,
            StdAccuracy:            stdAcc,
            AvgF1:                  f1List.Average(),
            AvgEV:                  evList.Average(),
            AvgSharpe:              sharpeList.Average(),
            FoldCount:              accList.Count,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CV-SPECIFIC HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0;
        int n = sharpePerFold.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++) { sumX += i; sumY += sharpePerFold[i]; sumXY += i * sharpePerFold[i]; sumXX += i * i; }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) > 1e-15 ? (n * sumXY - sumX * sumY) / denom : 0;
    }

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats((int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);
        var returns = new double[predictions.Length];
        double equity = 1.0, peak = 1.0, maxDD = 0;
        for (int i = 0; i < predictions.Length; i++)
        {
            double r = predictions[i].Predicted == predictions[i].Actual ? 0.01 : -0.01;
            returns[i] = r; equity += r;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0;
            if (dd > maxDD) maxDD = dd;
        }
        double mean = returns.Average();
        double varSum = 0;
        foreach (double r in returns) varSum += (r - mean) * (r - mean);
        double std = returns.Length > 1 ? Math.Sqrt(varSum / (returns.Length - 1)) : 0;
        return (maxDD, std > 1e-10 ? mean / std * Math.Sqrt(252) : 0);
    }

    /// <summary>Item 33: Rank-dispersion feature stability across fold importance rankings.</summary>
    private static double[] ComputeRankStability(List<double[]> foldImportances, int featureCount)
    {
        int k = foldImportances.Count;
        if (k < 2) return new double[featureCount];

        // Compute ranks for each fold
        var ranks = new double[k][];
        for (int fi = 0; fi < k; fi++)
        {
            var imp = foldImportances[fi];
            var indexed = imp.Select((v, idx) => (v, idx)).OrderByDescending(x => x.v).ToArray();
            ranks[fi] = new double[featureCount];
            for (int r = 0; r < indexed.Length && r < featureCount; r++)
                ranks[fi][indexed[r].idx] = r + 1;
        }

        // Compute per-feature rank stability: coefficient of concordance per feature
        var stability = new double[featureCount];
        for (int j = 0; j < featureCount; j++)
        {
            double sumRank = 0;
            for (int fi = 0; fi < k; fi++) sumRank += ranks[fi][j];
            double meanRank = sumRank / k;
            double variance = 0;
            for (int fi = 0; fi < k; fi++)
            {
                double d = ranks[fi][j] - meanRank;
                variance += d * d;
            }
            // Normalised stability: 0 = perfect agreement, 1 = maximum discord
            stability[j] = k > 1 ? Math.Sqrt(variance / (k - 1)) / featureCount : 0;
        }
        return stability;
    }
}
