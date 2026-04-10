using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    private static EvalMetrics EvaluateGbm(
        List<TrainingSample> evalSet, List<GbmTree> trees, double baseLogOdds, double lr,
        double[] magWeights, double magBias, int featureCount,
        ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null,
        double decisionThreshold = 0.5)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;

        foreach (var s in evalSet)
        {
            double p    = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int    yHat = p >= decisionThreshold ? 1 : 0;
            int    y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);

            if (magWeights.Length > 0)
            {
                double pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    pred += magWeights[j] * s.Features[j];
                magSse += (pred - s.Magnitude) * (pred - s.Magnitude);
            }
            else
            {
                double score = GbmScore(s.Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
                magSse += (score - s.Magnitude) * (score - s.Magnitude);
            }
        }

        int evalN = evalSet.Count;
        double accuracy  = evalN > 0 ? (double)correct / evalN : 0;
        double brier     = evalN > 0 ? brierSum / evalN : 1;
        double magRmse   = evalN > 0 ? Math.Sqrt(magSse / evalN) : double.MaxValue;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = accuracy > 0.5 ? accuracy - 0.5 : 0;
        double sharpe    = ev / (brier + 0.01);

        double weightSum = 0, correctWeighted = 0;
        for (int i = 0; i < evalN; i++)
        {
            double wt = 1.0 + (double)i / evalN;
            double p  = GbmCalibProb(evalSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            weightSum += wt;
            if ((p >= decisionThreshold) == (evalSet[i].Direction > 0)) correctWeighted += wt;
        }
        double wAcc = weightSum > 0 ? correctWeighted / weightSum : accuracy;

        return new EvalMetrics(accuracy, precision, recall, f1, magRmse, ev, brier, wAcc, sharpe, tp, fp, fn, tn);
    }

    private static double ComputeEce(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null,
        int bins = 10)
    {
        if (testSet.Count < bins) return 1.0;
        var binPositive = new double[bins]; var binConf = new double[bins]; var binCount = new int[bins];

        foreach (var s in testSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin] += p; binPositive[bin] += s.Direction > 0 ? 1 : 0; binCount[bin]++;
        }

        double ece = 0; int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            ece += Math.Abs(binPositive[b] / binCount[b] - binConf[b] / binCount[b]) * binCount[b] / n;
        }
        return ece;
    }

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (testSet.Count < 10) return 0;
        double brier = 0; int posCount = 0;
        foreach (var s in testSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int y = s.Direction > 0 ? 1 : 0;
            brier += (p - y) * (p - y); posCount += y;
        }
        brier /= testSet.Count;
        double baseRate = (double)posCount / testSet.Count;
        double naiveBrier = baseRate * (1 - baseRate);
        return naiveBrier > 1e-10 ? 1.0 - brier / naiveBrier : 0;
    }

    /// <summary>Item 36: Murphy-style Brier decomposition into calibration + refinement.</summary>
    private static (double CalibrationLoss, double RefinementLoss) ComputeMurphyDecomposition(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null, int bins = 10)
    {
        if (testSet.Count < bins) return (0, 0);
        var binSumP = new double[bins]; var binSumY = new double[bins]; var binCount = new int[bins];
        int totalPos = 0;

        foreach (var s in testSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int y = s.Direction > 0 ? 1 : 0;
            int bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binSumP[bin] += p; binSumY[bin] += y; binCount[bin]++; totalPos += y;
        }

        double baseRate = (double)totalPos / testSet.Count;
        double calLoss = 0, refLoss = 0;
        int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgP = binSumP[b] / binCount[b];
            double avgY = binSumY[b] / binCount[b];
            calLoss += (avgP - avgY) * (avgP - avgY) * binCount[b] / n; // reliability
            refLoss += avgY * (1 - avgY) * binCount[b] / n; // within-bin variance (resolution proxy)
        }
        return (calLoss, refLoss);
    }

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        CancellationToken ct, int baseSeed = 0, double decisionThreshold = 0.5)
    {
        double baseline = ComputeAccuracy(testSet, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates, decisionThreshold);
        var importance = new float[featureCount];
        int tn = testSet.Count;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            int rngSeed = baseSeed != 0 ? baseSeed + (j * 13) + 42 : j * 13 + 42;
            var rng  = new Random(rngSeed);
            var vals = new float[tn];
            for (int i = 0; i < tn; i++) vals[i] = testSet[i].Features[j];
            for (int i = tn - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }

            var scratch = new float[testSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < tn; idx++)
            {
                Array.Copy(testSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double p = GbmCalibProb(scratch, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                if ((p >= decisionThreshold) == (testSet[idx].Direction > 0)) correct++;
            }
            importance[j] = (float)Math.Max(0, baseline - (double)correct / tn);
        });

        float total = importance.Sum();
        if (total > 1e-6f) for (int j = 0; j < featureCount; j++) importance[j] /= total;
        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double decisionThreshold, CancellationToken ct, int baseSeed = 0)
    {
        int n = calSet.Count;
        int baseCorrect = 0;
        foreach (var s in calSet)
            if ((GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates) >= decisionThreshold) == (s.Direction > 0))
                baseCorrect++;
        double baseAcc = (double)baseCorrect / n;

        var importance = new double[featureCount];
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            int rngSeed = baseSeed != 0 ? baseSeed + (j * 17) + 7 : j * 17 + 7;
            var rng = new Random(rngSeed);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }

            var scratch = new float[calSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if ((GbmCalibProb(scratch, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates) >= decisionThreshold) == (calSet[idx].Direction > 0))
                    correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        var norms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
            norms[i] = p * (1 - p);
        }
        double mean = norms.Average();
        double std = 0;
        foreach (double v in norms) std += (v - mean) * (v - mean);
        std = norms.Length > 1 ? Math.Sqrt(std / (norms.Length - 1)) : 0;
        return (mean, std);
    }

    /// <summary>Item 38: Average distance-to-decision-boundary on test set.</summary>
    private static double ComputePredictionStability(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        double sum = 0;
        foreach (var s in testSet)
        {
            double rawScore = GbmScore(s.Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
            sum += Math.Abs(rawScore); // distance from decision boundary (0 in log-odds)
        }
        return testSet.Count > 0 ? sum / testSet.Count : 0;
    }

    private static double ComputeOobAccuracy(
        List<TrainingSample> trainSet, List<GbmTree> trees, List<HashSet<int>> bagMasks,
        double baseLogOdds, double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null, double decisionThreshold = 0.5)
    {
        if (trainSet.Count < 10 || trees.Count < 2 || bagMasks.Count != trees.Count) return 0;
        int correct = 0, evaluated = 0;
        for (int i = 0; i < trainSet.Count; i++)
        {
            double oobScore = baseLogOdds;
            int oobTreeCount = 0;
            for (int t = 0; t < trees.Count; t++)
            {
                if (bagMasks[t].Contains(i)) continue;
                oobScore += GetTreeLearningRate(t, lr, perTreeLearningRates) * Predict(trees[t], trainSet[i].Features);
                oobTreeCount++;
            }
            if (oobTreeCount == 0) continue;

            // OOB estimates are computed in raw-probability space so they do not apply
            // calibration artifacts fitted on the full ensemble to subset-tree predictions.
            double oobProb = Math.Clamp(Sigmoid(oobScore), 1e-7, 1.0 - 1e-7);
            if ((oobProb >= 0.5) == (trainSet[i].Direction > 0)) correct++;
            evaluated++;
        }
        return evaluated > 0 ? (double)correct / evaluated : 0;
    }

    private static (string[] Pairs, int[] DropIndices) ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int featureCount, double threshold, float[] importance)
    {
        if (trainSet.Count < 30) return ([], []);
        int n = Math.Min(trainSet.Count, 500);
        int numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));
        var featureMin = new double[featureCount]; var featureMax = new double[featureCount];
        var featureBinIdx = new int[featureCount * n];
        Array.Fill(featureMin, double.MaxValue); Array.Fill(featureMax, double.MinValue);

        for (int j = 0; j < featureCount; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[j];
                if (v < featureMin[j]) featureMin[j] = v;
                if (v > featureMax[j]) featureMax[j] = v;
            }
            double range = featureMax[j] - featureMin[j];
            double binWidth = range > 1e-15 ? range / numBins : 1.0;
            for (int i = 0; i < n; i++)
                featureBinIdx[j * n + i] = Math.Clamp((int)((trainSet[i].Features[j] - featureMin[j]) / binWidth), 0, numBins - 1);
        }

        var pairs = new List<string>(); var dropIndices = new List<int>();
        double invN = 1.0 / n;

        for (int a = 0; a < featureCount; a++)
        {
            for (int bi = a + 1; bi < featureCount; bi++)
            {
                var joint = new int[numBins * numBins]; var margA = new int[numBins]; var margB = new int[numBins];
                for (int i = 0; i < n; i++)
                {
                    int ba = featureBinIdx[a * n + i]; int bb = featureBinIdx[bi * n + i];
                    joint[ba * numBins + bb]++; margA[ba]++; margB[bb]++;
                }

                double mi = 0;
                for (int ia = 0; ia < numBins; ia++)
                {
                    if (margA[ia] == 0) continue; double pA = margA[ia] * invN;
                    for (int ib = 0; ib < numBins; ib++)
                    {
                        int jCount = joint[ia * numBins + ib];
                        if (jCount == 0 || margB[ib] == 0) continue;
                        double pJ = jCount * invN, pB = margB[ib] * invN;
                        mi += pJ * Math.Log(pJ / (pA * pB));
                    }
                }

                if (mi > threshold * Math.Log(2))
                {
                    string nameA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nameB = bi < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[bi] : $"F{bi}";
                    pairs.Add($"{nameA}:{nameB}");
                    // Item 35: recommend dropping the less important feature
                    float impA = a < importance.Length ? importance[a] : 0;
                    float impB = bi < importance.Length ? importance[bi] : 0;
                    dropIndices.Add(impA >= impB ? bi : a);
                }
            }
        }
        return (pairs.ToArray(), dropIndices.ToArray());
    }

    private static double ComputeAccuracy(
        List<TrainingSample> set, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null, double decisionThreshold = 0.5)
    {
        int correct = 0;
        foreach (var s in set)
            if ((GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates) >= decisionThreshold) == (s.Direction > 0))
                correct++;
        return set.Count > 0 ? (double)correct / set.Count : 0;
    }

    private static double ComputeDurbinWatson(
        List<TrainingSample> train, double[] magWeights, double magBias, int featureCount)
    {
        if (train.Count < 10 || magWeights.Length == 0) return 2.0;
        var residuals = new double[train.Count];
        for (int i = 0; i < train.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, train[i].Features.Length); j++)
                pred += magWeights[j] * train[i].Features[j];
            residuals[i] = train[i].Magnitude - pred;
        }
        double numSum = 0, denSum = 0;
        for (int i = 1; i < residuals.Length; i++) { double d = residuals[i] - residuals[i - 1]; numSum += d * d; }
        for (int i = 0; i < residuals.Length; i++) denSum += residuals[i] * residuals[i];
        return denSum > 1e-15 ? numSum / denSum : 2.0;
    }

    /// <summary>Item 39: Gain-weighted tree split importance.</summary>
    private static float[] ComputeGainWeightedImportance(List<GbmTree> trees, int featureCount)
    {
        var importance = new float[featureCount];
        foreach (var tree in trees)
        {
            if (tree.Nodes is null) continue;
            foreach (var node in tree.Nodes)
                if (!node.IsLeaf && node.SplitFeature < featureCount)
                    importance[node.SplitFeature] += (float)node.SplitGain;
        }
        float total = importance.Sum();
        if (total > 1e-6f) for (int j = 0; j < featureCount; j++) importance[j] /= total;
        return importance;
    }

    private static double StdDev(IList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        foreach (double v in values) sum += (v - mean) * (v - mean);
        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int featureCount)
    {
        if (threshold <= 0.0 || featureCount == 0)
            return BuildAllTrueMask(featureCount);
        double minImportance = threshold / featureCount;
        var mask = new bool[featureCount];
        for (int j = 0; j < featureCount; j++) mask[j] = importance[j] >= minImportance;
        return mask;
    }

    private static double Quantile(double[] values, double probability)
    {
        if (values.Length == 0)
            return 0.5;

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Round(Math.Clamp(probability, 0.0, 1.0) * (sorted.Length - 1));
        return sorted[index];
    }

    private static ConditionalPlattBranchFit FitConditionalPlattBranch(
        IReadOnlyList<(double Logit, double BaseProb, double Y)> pairs)
    {
        if (pairs.Count == 0)
            return new ConditionalPlattBranchFit(0, 0.0, 0.0, 0.0, 0.0);

        double baselineLoss = ComputeConditionalBranchNll(pairs);
        if (pairs.Count < 10)
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

        int nPos = pairs.Count(pair => pair.Y > 0.5);
        int nNeg = pairs.Count - nPos;
        double targetPos = (nPos + 1.0) / (nPos + 2.0);
        double targetNeg = 1.0 / (nNeg + 2.0);
        var smoothedY = pairs.Select(pair => pair.Y > 0.5 ? targetPos : targetNeg).ToArray();

        const double sgdLr = 0.01;
        const int maxEpochs = 200;
        double a = 1.0, b = 0.0;
        double bestA = a, bestB = b, bestLoss = baselineLoss;

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            double dA = 0.0, dB = 0.0;
            for (int i = 0; i < pairs.Count; i++)
            {
                double calibP = Sigmoid(a * pairs[i].Logit + b);
                double err = calibP - smoothedY[i];
                dA += err * pairs[i].Logit;
                dB += err;
            }

            a -= sgdLr * dA / pairs.Count;
            b -= sgdLr * dB / pairs.Count;

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
                : Math.Clamp(pairs[i].BaseProb, 1e-7, 1.0 - 1e-7);
            loss -= pairs[i].Y * Math.Log(Math.Max(p, 1e-7))
                  + (1.0 - pairs[i].Y) * Math.Log(Math.Max(1.0 - p, 1e-7));
        }

        return loss / pairs.Count;
    }

    // ── Reliability diagram ───────────────────────────────────────────────────

    private static (double[] BinConf, double[] BinAcc, int[] BinCounts) ComputeReliabilityDiagram(
        List<TrainingSample> set, IReadOnlyList<GbmTree> trees, double blo, double lr, int F,
        ModelSnapshot calibSnap, IReadOnlyList<double>? perTreeLr, int bins = 10)
    {
        var binConf = new double[bins]; var binAcc = new double[bins]; var binCounts = new int[bins];
        if (set.Count < bins) return (binConf, binAcc, binCounts);
        foreach (var s in set)
        {
            double p = GbmCalibProb(s.Features, trees, blo, lr, F, calibSnap, perTreeLr);
            int b = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[b] += p; if (s.Direction > 0) binAcc[b]++; binCounts[b]++;
        }
        for (int b = 0; b < bins; b++) if (binCounts[b] > 0) { binConf[b] /= binCounts[b]; binAcc[b] /= binCounts[b]; }
        return (binConf, binAcc, binCounts);
    }

    // ── Calibration residual stats ────────────────────────────────────────────

    private static (double Mean, double Std, double Threshold) ComputeCalibrationResidualStats(
        List<TrainingSample> set, IReadOnlyList<GbmTree> trees, double blo, double lr, int F,
        ModelSnapshot calibSnap, IReadOnlyList<double>? perTreeLr)
    {
        if (set.Count < 10) return (0.0, 0.0, 1.0);
        var residuals = new double[set.Count];
        for (int i = 0; i < set.Count; i++)
        {
            double p = GbmCalibProb(set[i].Features, trees, blo, lr, F, calibSnap, perTreeLr);
            residuals[i] = Math.Abs(p - (set[i].Direction > 0 ? 1.0 : 0.0));
        }
        double mean = 0; foreach (double r in residuals) mean += r; mean /= residuals.Length;
        double variance = 0; foreach (double r in residuals) { double d = r - mean; variance += d * d; }
        double std = residuals.Length > 1 ? Math.Sqrt(variance / (residuals.Length - 1)) : 0.0;
        return (mean, std, mean + 2.0 * std);
    }

    // ── Feature variances ─────────────────────────────────────────────────────

    private static double[] ComputeFeatureVariances(List<TrainingSample> set, int F)
    {
        if (set.Count < 2) return new double[F];
        var v = new double[F]; int n = set.Count;
        for (int j = 0; j < F; j++)
        {
            double sum = 0, sumSq = 0;
            for (int i = 0; i < n; i++) { double val = set[i].Features[j]; sum += val; sumSq += val * val; }
            double mean = sum / n; v[j] = sumSq / n - mean * mean;
        }
        return v;
    }
}
