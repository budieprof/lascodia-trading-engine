using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        int                  K,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds > 0 ? hp.WalkForwardFolds : 3;
        int embargo = hp.EmbargoBarCount;
        int cvK     = Math.Max(5, K / 2); // fewer rounds per fold for speed

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning(
                "AdaBoost CV: fold size too small ({Size} < 50), skipping CV.", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds        = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;

            // Purged CV: lookback-window embargo prevents feature-lookback leakage
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug(
                    "AdaBoost CV fold {Fold} skipped (trainEnd={N} < minSamples)", fold, trainEnd);
                continue;
            }

            var rawFoldTrain = samples[..trainEnd].ToList();

            // Time-series purging: remove trailing train samples whose label horizon
            // overlaps the test fold start (PurgeHorizonBars bars ahead)
            if (hp.PurgeHorizonBars > 0 && rawFoldTrain.Count > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < rawFoldTrain.Count)
                {
                    int purgeCount = rawFoldTrain.Count - purgeFrom;
                    rawFoldTrain = rawFoldTrain[..purgeFrom];
                    if (purgeCount > 0)
                        _logger.LogDebug(
                            "Purging: removed {N} train samples overlapping test fold.", purgeCount);
                }
            }

            var rawFoldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (rawFoldTest.Count < 20 || rawFoldTrain.Count < hp.MinSamples) continue;

            // ── Per-fold Z-score standardisation (no look-ahead) ──────────────
            // Fit scaler only on the fold's purged training window, then apply it
            // to both train and test slices.  This removes the subtle bias that
            // occurs when a global scaler trained on the full 70 % boundary leaks
            // the distribution of folds that appear later in the expanding window.
            var foldRawFeats = new List<float[]>(rawFoldTrain.Count);
            foreach (var s in rawFoldTrain) foldRawFeats.Add(s.Features);
            var (foldMeans, foldStds) = MLFeatureHelper.ComputeStandardization(foldRawFeats);

            var foldTrain = new List<TrainingSample>(rawFoldTrain.Count);
            foreach (var s in rawFoldTrain)
                foldTrain.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, foldMeans, foldStds) });

            var foldTest = new List<TrainingSample>(rawFoldTest.Count);
            foreach (var s in rawFoldTest)
                foldTest.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, foldMeans, foldStds) });

            int      foldM       = foldTrain.Count;
            var      foldLb      = new int[foldM];
            for (int i = 0; i < foldM; i++) foldLb[i] = foldTrain[i].Direction > 0 ? 1 : -1;

            // Mirror the main training loop: use soft labels in SAMME weight updates so
            // CV Sharpe/EV metrics reflect the same algorithm as the final model.
            var foldSoftLabels = new double[foldM];
            if (hp.UseAdaptiveLabelSmoothing)
            {
                double foldMaxMag = 0.0;
                foreach (var s in foldTrain) { double mag = Math.Abs((double)s.Magnitude); if (mag > foldMaxMag) foldMaxMag = mag; }
                for (int i = 0; i < foldM; i++)
                {
                    double eps_i = foldMaxMag > 1e-9
                        ? Math.Clamp(1.0 - Math.Abs((double)foldTrain[i].Magnitude) / foldMaxMag, 0.0, 0.20)
                        : 0.0;
                    foldSoftLabels[i] = foldLb[i] * (1.0 - eps_i);
                }
            }
            else
            {
                for (int i = 0; i < foldM; i++) foldSoftLabels[i] = foldLb[i];
            }

            double[] foldW       = InitialiseBoostWeights(foldTrain, hp.TemporalDecayLambda);
            var      foldStumps  = new List<GbmTree>(cvK);
            var      foldAlphas  = new List<double>(cvK);
            var      foldKeys    = new double[foldM];
            var      foldIndices = new int[foldM];

            double cvShrinkage  = hp.AdaBoostAlphaShrinkage > 0.0 ? hp.AdaBoostAlphaShrinkage : 1.0;
            bool   cvSammeR     = hp.UseSammeR;
            int    cvDepth      = hp.AdaBoostMaxTreeDepth >= 2 ? 2 : 1;
            bool   cvJointD2    = cvDepth == 2 && hp.UseJointDepth2Search;

            for (int r = 0; r < cvK && !ct.IsCancellationRequested; r++)
            {
                var (fi, thresh, parity, err) =
                    FindBestStump(foldTrain, foldLb, foldW, F, foldKeys, foldIndices);

                if (!double.IsFinite(err) || err >= 0.5 - Eps) break;

                GbmTree cvTree;
                double  alpha;

                if (cvSammeR)
                {
                    cvTree = cvDepth == 2
                        ? (cvJointD2
                            ? BuildJointDepth2Tree(foldTrain, foldLb, foldW, F, foldKeys, foldIndices, true, null)
                            : BuildDepth2Tree(fi, thresh, foldTrain, foldLb, foldW, F, foldKeys, foldIndices, true))
                        : BuildSammeRStump(fi, thresh, foldTrain, foldLb, foldW, foldM);
                    alpha = 1.0;
                    foldAlphas.Add(alpha);
                    foldStumps.Add(cvTree);
                    double wSum = 0;
                    for (int i = 0; i < foldM; i++)
                    {
                        double hR = PredictStump(cvTree, foldTrain[i].Features);
                        foldW[i] *= Math.Exp(-foldSoftLabels[i] * hR);
                        wSum += foldW[i];
                    }
                    if (wSum > 0) for (int i = 0; i < foldM; i++) foldW[i] /= wSum;
                }
                else
                {
                    double cErr = Math.Max(Eps, Math.Min(1 - Eps, err));
                    alpha  = cvShrinkage * 0.5 * Math.Log((1 - cErr) / cErr);
                    cvTree = cvDepth == 2
                        ? (cvJointD2
                            ? BuildJointDepth2Tree(foldTrain, foldLb, foldW, F, foldKeys, foldIndices, false, null)
                            : BuildDepth2Tree(fi, thresh, foldTrain, foldLb, foldW, F, foldKeys, foldIndices, false))
                        : BuildStump(fi, thresh, parity);
                    foldAlphas.Add(alpha);
                    foldStumps.Add(cvTree);
                    double wSum = 0;
                    for (int i = 0; i < foldM; i++)
                    {
                        double pred = PredictStump(cvTree, foldTrain[i].Features);
                        foldW[i] *= Math.Exp(-alpha * foldSoftLabels[i] * pred);
                        wSum += foldW[i];
                    }
                    if (wSum > 0) for (int i = 0; i < foldM; i++) foldW[i] /= wSum;
                }
            }

            // Fold feature importance: alpha-weighted stump selection frequency
            var foldImp  = new double[F];
            double sumAl = 0;
            foreach (var a in foldAlphas) sumAl += a;
            if (sumAl > 0)
            {
                for (int k = 0; k < foldStumps.Count && k < foldAlphas.Count; k++)
                {
                    var root = foldStumps[k].Nodes?[0];
                    if (root is { IsLeaf: false, SplitFeature: >= 0 } && root.SplitFeature < F)
                        foldImp[root.SplitFeature] += foldAlphas[k] / sumAl;
                }
            }
            foldImportances.Add(foldImp);

            // Evaluate fold (no Platt for speed)
            int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
            double brierSum = 0, evSum = 0;
            var    foldReturns = new double[foldTest.Count];

            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                var    s     = foldTest[pi];
                double score = PredictScore(s.Features, foldStumps, foldAlphas);
                double p     = MLFeatureHelper.Sigmoid(2 * score);
                int    yHat  = score >= 0 ? 1 : 0;
                int    y     = s.Direction > 0 ? 1 : 0;
                if (yHat == y) correct++;
                if (yHat == 1 && y == 1) tp++;
                if (yHat == 1 && y == 0) fp++;
                if (yHat == 0 && y == 1) fn++;
                if (yHat == 0 && y == 0) tn++;
                brierSum += (p - y) * (p - y);
                double realizedReturn = (yHat == y ? 1 : -1) * (double)s.Magnitude;
                evSum += realizedReturn;
                foldReturns[pi] = realizedReturn;
            }

            int    nFold = foldTest.Count;
            double acc   = (double)correct / nFold;
            double prec  = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
            double rec   = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
            double f1    = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
            double brier = brierSum / nFold;
            double ev    = evSum / nFold;

            // ── Equity-curve gate — proper return-series Sharpe ───────────────
            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldReturns);
            double sharpe = foldCurveSharpe;

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                isBadFold = true;
            if (isBadFold) badFolds++;

            accList.Add(acc);
            f1List.Add(f1);
            evList.Add(ev);
            sharpeList.Add(sharpe);

            _logger.LogDebug(
                "AdaBoost CV fold {Fold}: acc={Acc:P1}, f1={F1:F3}, ev={EV:F4}, sharpe={Sharpe:F2}, maxDD={DD:P2}, bad={Bad}",
                fold, acc, f1, ev, sharpe, foldMaxDD, isBadFold);
        }

        if (accList.Count == 0) return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        // ── Equity-curve gate: bad-fold fraction threshold ─────────────────────
        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "Equity-curve gate: {BadFolds}/{Total} folds failed (maxDD or Sharpe).",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        // ── Sharpe trend gate ─────────────────────────────────────────────────
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model flagged.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // ── Cross-fold variance gate ──────────────────────────────────────────
        if (hp.MaxWalkForwardStdDev > 0.0 && stdAcc > hp.MaxWalkForwardStdDev)
            _logger.LogWarning(
                "CV high variance: stdAcc={Std:P1} > threshold {Max:P1}.",
                stdAcc, hp.MaxWalkForwardStdDev);

        // ── Feature stability: CV = σ/μ of alpha-weighted selection freq ──────
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int foldCount = foldImportances.Count;
            for (int j = 0; j < F; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImportances[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = foldImportances[fi][j] - meanImp;
                    varImp += d * d;
                }
                double stdImp = foldCount > 1 ? Math.Sqrt(varImp / (foldCount - 1)) : 0.0;
                featureStabilityScores[j] = meanImp > 1e-10 ? stdImp / meanImp : 0.0;
            }
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

    // ── Walk-forward Sharpe trend (OLS slope through per-fold Sharpe series) ──

    /// <summary>
    /// Fits a least-squares linear regression slope through the per-fold Sharpe series.
    /// Returns 0.0 when fewer than 3 folds are available.
    /// A negative slope indicates degrading out-of-sample performance over time.
    /// </summary>
    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        int n = sharpePerFold.Count;
        if (n < 3) return 0.0;

        // OLS: slope = (n·Σxy − Σx·Σy) / (n·Σx² − (Σx)²)
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            double x  = i;
            double y  = sharpePerFold[i];
            sumX  += x;
            sumY  += y;
            sumXY += x * y;
            sumXX += x * x;
        }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) < 1e-12 ? 0.0 : (n * sumXY - sumX * sumY) / denom;
    }

    // ── Standard deviation helper ─────────────────────────────────────────────

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sumSq = 0;
        foreach (double v in values) sumSq += (v - mean) * (v - mean);
        return Math.Sqrt(sumSq / (values.Count - 1));
    }
}
