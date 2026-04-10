using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class QuantileRfModelTrainer
{

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        int                  sqrtF,
        int                  treeCount,
        CancellationToken    ct,
        int                  maxDepth = DefaultMaxDepth,
        int                  minLeaf  = DefaultMinLeaf)
    {
        int folds   = hp.WalkForwardFolds > 0 ? hp.WalkForwardFolds : 3;
        int embargo = hp.EmbargoBarCount;
        int cvTrees = Math.Max(10, treeCount / 2);

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning("QuantileRF CV: fold size too small ({Size} < 50), skipping CV.", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        // Folds are independent — run in parallel (#8)
        var foldResults =
            new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        // LookbackWindow-aware purge extra bars (#11)
        int lookbackPurge = MLFeatureHelper.LookbackWindow - 1;

        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;

            // Subtract LookbackWindow − 1 extra bars beyond the embargo so that no
            // feature computed from the test-period candles leaks into training (#11)
            int trainEnd  = Math.Max(0, testStart - embargo - lookbackPurge);

            // Purge horizon: remove samples whose forward label window overlaps test fold
            if (hp.PurgeHorizonBars > 0)
                trainEnd = Math.Max(0, Math.Min(trainEnd, testStart - hp.PurgeHorizonBars));

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("QuantileRF CV fold {Fold} skipped (trainEnd={N} < minSamples)", fold, trainEnd);
                return;
            }

            var foldTrain = samples[..trainEnd];
            var foldTest  = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) return;

            // Per-fold isolated RNG — no shared state between parallel folds
            var foldRng = new Random(fold * 31 + 17);

            int foldTrainCount = foldTrain.Count;
            double[] foldImpAccum = new double[F];
            int      foldImpSplits = 0;
            var      foldTrees    = new List<List<TreeNode>>(cvTrees);

            // Reserve the last 20 % of foldTrain for intra-fold calibration so that
            // (a) trees are not evaluated on their own training data when calibrating, and
            // (b) the fold threshold is learned on calibrated probabilities — matching the
            // final model's inference path (Platt + EV-optimal threshold).
            int foldCalSize  = Math.Max(10, foldTrainCount / 5);
            int foldBuildCnt = foldTrainCount - foldCalSize;
            var foldBuildSet = foldTrain[..foldBuildCnt];
            var foldCalSlice = foldTrain[foldBuildCnt..];

            for (int tIdx = 0; tIdx < cvTrees; tIdx++)
            {
                var bootstrapIdx = new List<int>(foldBuildCnt);
                for (int bi = 0; bi < foldBuildCnt; bi++)
                    bootstrapIdx.Add(foldRng.Next(foldBuildCnt));

                var nodes = new List<TreeNode>();
                BuildTree(foldBuildSet, bootstrapIdx, 0, nodes, foldRng, F, sqrtF, foldImpAccum, ref foldImpSplits,
                          importanceScores: null, maxDepth: maxDepth, minLeaf: minLeaf);
                if (nodes.Count > 0) foldTrees.Add(nodes);
            }

            if (foldTrees.Count == 0) return;

            // Intra-fold Platt calibration + EV-optimal threshold (no test-set leakage)
            var (foldPlattA, foldPlattB) = foldCalSlice.Count >= 10
                ? FitPlattScaling(foldCalSlice, foldTrees, foldBuildSet)
                : (1.0, 0.0);
            double foldThreshold = foldCalSlice.Count >= 30
                ? ComputeOptimalThreshold(foldCalSlice, foldTrees, foldBuildSet, foldPlattA, foldPlattB, [],
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax)
                : 0.5;

            int    nFold  = foldTest.Count;
            int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
            double brierSum = 0, evSum = 0;
            var    predictions = new (int Predicted, int Actual)[nFold];

            for (int i = 0; i < nFold; i++)
            {
                var    s    = foldTest[i];
                double prob = PredictProb(s.Features, foldTrees, foldBuildSet, foldPlattA, foldPlattB);
                int    yHat = prob >= foldThreshold ? 1 : 0;
                int    y    = s.Direction > 0 ? 1 : 0;
                if (yHat == y) correct++;
                if (yHat == 1 && y == 1) tp++;
                if (yHat == 1 && y == 0) fp++;
                if (yHat == 0 && y == 1) fn++;
                if (yHat == 0 && y == 0) tn++;
                brierSum      += (prob - y) * (prob - y);
                evSum         += (yHat == y ? 1 : -1) * (double)s.Magnitude;
                predictions[i] = (yHat, y);
            }

            double acc    = (double)correct / nFold;
            double prec   = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
            double rec    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
            double f1     = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
            double brier  = brierSum / nFold;
            double ev     = evSum / nFold;

            // Equity-curve Sharpe replaces the (acc−0.5)/(brier+0.01) proxy so that
            // the Sharpe trend gate and AvgSharpe metric reflect actual risk-adjusted returns.
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(predictions);
            double sharpe = curveSharpe;

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            double totalImpFold = 0;
            for (int fi = 0; fi < F; fi++) totalImpFold += foldImpAccum[fi];
            var normImp = new double[F];
            for (int fi = 0; fi < F; fi++)
                normImp[fi] = totalImpFold > Eps ? foldImpAccum[fi] / totalImpFold : 0.0;

            // Write to fold-indexed slot — no cross-slot races
            foldResults[fold] = (acc, f1, ev, sharpe, normImp, isBad);

            _logger.LogDebug(
                "QuantileRF CV fold {Fold}: acc={Acc:P1}, f1={F1:F3}, ev={EV:F4}, sharpe={Sharpe:F2}, maxDD={DD:P1}",
                fold, acc, f1, ev, sharpe, maxDD);
        });

        // Aggregate parallel results (preserve fold order for Sharpe trend)
        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var impLists   = new List<double[]>(folds);
        int badFolds   = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc);
            f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV);
            sharpeList.Add(r.Value.Sharpe);
            impLists.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
        }

        if (accList.Count == 0) return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        // Equity curve gate decision
        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "QuantileRF equity-curve gate: {BadFolds}/{TotalFolds} CV folds failed.", badFolds, accList.Count);

        // Sharpe trend gate
        double sharpeTrend = ComputeSharpeTrend(sharpeList);
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "QuantileRF Sharpe trend gate: slope={Slope:F3} < threshold={Thr:F3}",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // Feature stability scores (importance CoV across folds)
        double[]? featureStabilityScores = null;
        if (impLists.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int foldCount = impLists.Count;
            for (int j = 0; j < F; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += impLists[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = impLists[fi][j] - meanImp;
                    varImp += d * d;
                }
                double stdImp = foldCount > 1 ? Math.Sqrt(varImp / (foldCount - 1)) : 0.0;
                featureStabilityScores[j] = meanImp > 1e-10 ? stdImp / meanImp : 0.0;
            }
        }

        double avgAcc = accList.Average();
        return (new WalkForwardResult(
            AvgAccuracy:            avgAcc,
            StdAccuracy:            StdDev(accList, avgAcc),
            AvgF1:                  f1List.Average(),
            AvgEV:                  evList.Average(),
            AvgSharpe:              sharpeList.Average(),
            FoldCount:              accList.Count,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores),
            equityCurveGateFailed);
    }

    // ── Tree building ─────────────────────────────────────────────────────────

    /// <summary>
    /// Recursively builds a single decision tree using variance-reduction (Gini-equivalent
    /// for binary labels). Uses histogram-based split finding (256 equal-width bins) for
    /// O(N + 256) per feature candidate per node instead of O(N log N) sort-based search.
    /// Feature candidates are sampled via Fisher-Yates partial shuffle (unbiased) or
    /// importance-weighted CDF when warm-start scores are available.
    /// </summary>
    private static void BuildTree(
        List<TrainingSample> trainSet,
        List<int>            sampleIdx,
        int                  depth,
        List<TreeNode>       nodes,
        Random               rng,
        int                  F,
        int                  sqrtF,
        double[]             impAccum,
        ref int              impSplits,
        float[]?             importanceScores = null,
        int                  maxDepth = DefaultMaxDepth,
        int                  minLeaf  = DefaultMinLeaf)
    {
        var node = new TreeNode();
        nodes.Add(node);

        if (sampleIdx.Count < minLeaf || depth >= maxDepth)
        {
            PopulateLeafNode(node, trainSet, sampleIdx);
            return;
        }

        var candidateFeats = importanceScores is { Length: > 0 }
            ? GenerateBiasedCandidateFeats(F, sqrtF, importanceScores, rng)
            : FisherYatesPartialShuffle(F, sqrtF, rng);

        int    bestFeat   = -1;
        double bestThresh = 0.0;
        double bestGain   = -1.0;

        // Inline parent variance: p*(1-p) for binary labels
        int parentPos = 0;
        foreach (int idx in sampleIdx) if (trainSet[idx].Direction > 0) parentPos++;
        int    totalN    = sampleIdx.Count;
        double parentP   = (double)parentPos / totalN;
        double parentVar = parentP * (1.0 - parentP);

        // Histogram-based split finding: 256 equal-width bins per feature.
        // O(N + 256) per feature candidate, replacing O(N log N) sort.
        // Bin arrays are allocated once and reused across feature candidates.
        const int numBins = 256;
        var binPos   = new int[numBins];
        var binTotal = new int[numBins];

        foreach (int fi in candidateFeats)
        {
            // Pass 1: find min/max of this feature across samples in sampleIdx
            double fMin = double.MaxValue, fMax = double.MinValue;
            foreach (int idx in sampleIdx)
            {
                double v = trainSet[idx].Features[fi];
                if (v < fMin) fMin = v;
                if (v > fMax) fMax = v;
            }

            double range = fMax - fMin;
            if (range < 1e-12) continue; // all values identical — no split possible

            double binWidth = range / numBins;

            // Pass 2: bin samples and count pos/total per bin
            Array.Clear(binPos, 0, numBins);
            Array.Clear(binTotal, 0, numBins);
            foreach (int idx in sampleIdx)
            {
                int bin = Math.Min(numBins - 1, (int)((trainSet[idx].Features[fi] - fMin) / binWidth));
                binTotal[bin]++;
                if (trainSet[idx].Direction > 0) binPos[bin]++;
            }

            // Pass 3: sweep bins left-to-right, accumulate posLeft/totalLeft
            int leftTotal = 0, leftPos = 0;
            for (int b = 0; b < numBins - 1; b++)
            {
                leftTotal += binTotal[b];
                leftPos   += binPos[b];

                if (leftTotal < minLeaf) continue;
                int rightTotal = totalN - leftTotal;
                if (rightTotal < minLeaf) break;

                int rightPos = parentPos - leftPos;
                double meanL = (double)leftPos  / leftTotal;
                double meanR = (double)rightPos / rightTotal;

                double weightedVar =
                    (leftTotal  * meanL * (1.0 - meanL)
                   + rightTotal * meanR * (1.0 - meanR))
                   / totalN;

                double gain = parentVar - weightedVar;
                if (gain > bestGain)
                {
                    bestGain   = gain;
                    bestFeat   = fi;
                    bestThresh = fMin + (b + 1) * binWidth; // bin boundary
                }
            }
        }

        if (bestFeat < 0 || bestGain <= 0)
        {
            PopulateLeafNode(node, trainSet, sampleIdx);
            return;
        }

        // Partition into left/right child sets using the chosen threshold
        var leftIndices  = new List<int>(totalN);
        var rightIndices = new List<int>(totalN);
        foreach (int idx in sampleIdx)
        {
            if ((double)trainSet[idx].Features[bestFeat] <= bestThresh)
                leftIndices.Add(idx);
            else
                rightIndices.Add(idx);
        }

        if (leftIndices.Count < minLeaf || rightIndices.Count < minLeaf)
        {
            PopulateLeafNode(node, trainSet, sampleIdx);
            return;
        }

        impAccum[bestFeat] += bestGain;
        impSplits++;

        node.SplitFeat   = bestFeat;
        node.SplitThresh = bestThresh;
        node.LeftChild   = nodes.Count;
        BuildTree(trainSet, leftIndices,  depth + 1, nodes, rng, F, sqrtF, impAccum, ref impSplits, importanceScores, maxDepth, minLeaf);
        node.RightChild  = nodes.Count;
        BuildTree(trainSet, rightIndices, depth + 1, nodes, rng, F, sqrtF, impAccum, ref impSplits, importanceScores, maxDepth, minLeaf);
    }

    /// <summary>Populates a leaf node with class counts from the sample indices.</summary>
    private static void PopulateLeafNode(TreeNode node, List<TrainingSample> trainSet, List<int> sampleIdx)
    {
        int posCount = 0;
        foreach (int idx in sampleIdx) if (trainSet[idx].Direction > 0) posCount++;
        node.LeafDirection  = sampleIdx.Count > 0 ? (double)posCount / sampleIdx.Count : 0.5;
        node.LeafPosCount   = posCount;
        node.LeafTotalCount = sampleIdx.Count;
    }

    /// <summary>
    /// #2: Unbiased Fisher-Yates partial shuffle — selects <paramref name="k"/> indices
    /// from 0…<paramref name="n"/>-1 without replacement. O(k) time and allocation.
    /// </summary>
    private static List<int> FisherYatesPartialShuffle(int n, int k, Random rng)
    {
        k = Math.Min(k, n);
        var indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        for (int i = 0; i < k; i++)
        {
            int j = rng.Next(i, n);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        return [.. indices.AsSpan(0, k)];
    }

    /// <summary>
    /// Builds a forest without OOB tracking — used for the feature pruning re-train pass
    /// where OOB masks are not needed and speed is preferred.
    /// </summary>
    private static List<List<TreeNode>> BuildForestOnly(
        List<TrainingSample> trainSet,
        int                  treeCount,
        int                  F,
        int                  sqrtF,
        double[]?            cumDensity,
        Random               rng,
        CancellationToken    ct,
        float[]?             importanceScores = null,
        int                  maxDepth = DefaultMaxDepth,
        int                  minLeaf  = DefaultMinLeaf)
    {
        int trainCount  = trainSet.Count;
        var trees       = new List<List<TreeNode>>(treeCount);

        // Build pos/neg index lists for stratified bootstrap (matches main training loop)
        var posIdx = new List<int>(trainCount);
        var negIdx = new List<int>(trainCount);
        for (int i = 0; i < trainCount; i++)
        {
            if (trainSet[i].Direction > 0) posIdx.Add(i);
            else                           negIdx.Add(i);
        }
        bool useStratified = posIdx.Count >= MinStratifiedClassCount && negIdx.Count >= MinStratifiedClassCount;

        // #33: Parallel tree construction (matching main training loop)
        var treeSeeds = new int[treeCount];
        for (int i = 0; i < treeCount; i++) treeSeeds[i] = rng.Next();

        var treeSlots = new List<TreeNode>?[treeCount];
        Parallel.For(0, treeCount, new ParallelOptions { CancellationToken = ct }, tIdx =>
        {
            var localRng = new Random(treeSeeds[tIdx]);
            var localAccum = new double[F];
            int localSplits = 0;
            var bootstrapIdx = new List<int>(trainCount);

            if (useStratified)
            {
                for (int bi = 0; bi < posIdx.Count; bi++)
                    bootstrapIdx.Add(posIdx[localRng.Next(posIdx.Count)]);
                for (int bi = 0; bi < negIdx.Count; bi++)
                    bootstrapIdx.Add(negIdx[localRng.Next(negIdx.Count)]);
                for (int i = bootstrapIdx.Count - 1; i > 0; i--)
                {
                    int j = localRng.Next(i + 1);
                    (bootstrapIdx[i], bootstrapIdx[j]) = (bootstrapIdx[j], bootstrapIdx[i]);
                }
            }
            else
            {
                for (int bi = 0; bi < trainCount; bi++)
                    bootstrapIdx.Add(cumDensity is null ? localRng.Next(trainCount) : SampleWeighted(localRng, cumDensity));
            }

            var nodes = new List<TreeNode>();
            BuildTree(trainSet, bootstrapIdx, 0, nodes, localRng, F, sqrtF, localAccum, ref localSplits, importanceScores, maxDepth, minLeaf);
            treeSlots[tIdx] = nodes.Count > 0 ? nodes : null;
        });

        foreach (var slot in treeSlots)
            if (slot is not null) trees.Add(slot);

        return trees;
    }

    // ── Stacking meta-learner over per-tree probs (#2) ────────────────────────

    /// <summary>
    /// #20: Meta-learner with Adam optimizer (replaces vanilla SGD) for faster and
    /// more stable convergence. Fits T-weight logistic regression over per-tree
    /// leaf-fraction probabilities. Uniform initialisation (1/T).
    /// </summary>
    private static (double[] MetaWeights, double MetaBias) FitMetaLearner(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        int T = allTrees.Count;
        if (calSet.Count < MinCalSamplesPlatt || T < 2) return (new double[T], 0.0);

        int n = calSet.Count;
        var calLP     = new double[n][];
        var calLabels = new double[n];
        for (int i = 0; i < n; i++)
        {
            calLP[i]     = GetTreeProbs(calSet[i].Features, allTrees, trainSet);
            calLabels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        var mw = new double[T];
        for (int t = 0; t < T; t++) mw[t] = 1.0 / T;
        double mb = 0.0;

        // Adam moment buffers
        var    mMw = new double[T]; var vMw = new double[T];
        double mMb = 0, vMb = 0;
        double beta1t = 1.0, beta2t = 1.0;
        int    step = 0;

        const double Lr     = 0.01;
        const int    Epochs = 300;

        var dW = new double[T];
        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            Array.Clear(dW, 0, T);
            double dB = 0;
            for (int i = 0; i < n; i++)
            {
                var    lp  = calLP[i];
                double z   = mb;
                for (int t = 0; t < T; t++) z += mw[t] * lp[t];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - calLabels[i];
                for (int t = 0; t < T; t++) dW[t] += err * lp[t];
                dB += err;
            }

            step++;
            beta1t *= AdamBeta1;
            beta2t *= AdamBeta2;
            double bc1    = 1.0 - beta1t;
            double bc2    = 1.0 - beta2t;
            double alphAt = Lr * Math.Sqrt(bc2) / bc1;

            for (int t = 0; t < T; t++)
            {
                double g = dW[t] / n;
                mMw[t] = AdamBeta1 * mMw[t] + (1 - AdamBeta1) * g;
                vMw[t] = AdamBeta2 * vMw[t] + (1 - AdamBeta2) * g * g;
                mw[t] -= alphAt * mMw[t] / (Math.Sqrt(vMw[t]) + AdamEpsilon);
            }
            {
                double g = dB / n;
                mMb = AdamBeta1 * mMb + (1 - AdamBeta1) * g;
                vMb = AdamBeta2 * vMb + (1 - AdamBeta2) * g * g;
                mb -= alphAt * mMb / (Math.Sqrt(vMb) + AdamEpsilon);
            }
        }

        return (mw, mb);
    }

    // ── Greedy Ensemble Selection over trees (#3) ─────────────────────────────

    /// <summary>
    /// Forward greedy selection (Caruana et al. 2004) that minimises NLL on the cal set.
    /// Pre-computes allLP[n][T] once, then picks the tree that most reduces NLL each round.
    /// Returns normalised usage frequencies (sum = 1) for all T trees.
    /// Returns an empty array when the cal set is too small.
    /// </summary>
    /// <summary>
    /// #19: GES with configurable rounds and early stopping when NLL stops improving.
    /// #38: Returns tree selection counts for logging.
    /// </summary>
    private static double[] RunGreedyTreeSelection(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  rounds = DefaultGesRounds,
        int                  earlyStopPatience = 0)
    {
        int T = allTrees.Count;
        if (calSet.Count < MinCalSamples || T < 2) return [];

        int gesN  = calSet.Count;
        var allLP = new double[gesN][];
        for (int i = 0; i < gesN; i++)
            allLP[i] = GetTreeProbs(calSet[i].Features, allTrees, trainSet);

        var counts   = new int[T];
        var ensProbs = new double[gesN];
        int ensSize  = 0;
        double prevBestLoss = double.MaxValue;
        int    noImproveCnt = 0;

        for (int round = 0; round < rounds; round++)
        {
            int    bestT    = -1;
            double bestLoss = double.MaxValue;

            for (int t = 0; t < T; t++)
            {
                double loss = 0.0;
                int    n1   = ensSize + 1;
                for (int i = 0; i < gesN; i++)
                {
                    double avg = (ensProbs[i] * ensSize + allLP[i][t]) / n1;
                    double y   = calSet[i].Direction > 0 ? 1.0 : 0.0;
                    loss -= y * Math.Log(avg + 1e-15) + (1 - y) * Math.Log(1 - avg + 1e-15);
                }
                if (loss < bestLoss) { bestLoss = loss; bestT = t; }
            }

            if (bestT < 0) break;
            counts[bestT]++;
            ensSize++;
            for (int i = 0; i < gesN; i++)
                ensProbs[i] = (ensProbs[i] * (ensSize - 1) + allLP[i][bestT]) / ensSize;

            // #19: Early stop if NLL hasn't improved
            if (earlyStopPatience > 0)
            {
                if (bestLoss < prevBestLoss - 1e-8)
                {
                    prevBestLoss = bestLoss;
                    noImproveCnt = 0;
                }
                else if (++noImproveCnt >= earlyStopPatience)
                    break;
            }
        }

        double total = counts.Sum();
        if (total < 1e-10) return new double[T];
        return counts.Select(c => c / total).ToArray();
    }

    // ── OOB-contribution tree pruning (#4) ────────────────────────────────────

    /// <summary>
    /// For each newly built tree, measures whether removing it from the OOB ensemble
    /// improves OOB accuracy. Trees whose removal is beneficial are discarded from
    /// <paramref name="newTrees"/> and <paramref name="oobMasks"/>.
    /// Uses a skip-index approach to avoid list mutations during evaluation.
    /// Returns the count of pruned trees.
    /// </summary>
    private static int PruneByOobContribution(
        List<TrainingSample> trainSet,
        List<List<TreeNode>> newTrees,
        List<HashSet<int>>   oobMasks)
    {
        if (trainSet.Count < 20 || newTrees.Count < 2 || oobMasks.Count != newTrees.Count) return 0;

        static double ComputeOobAcc(
            List<TrainingSample> ts,
            List<List<TreeNode>> trees,
            List<HashSet<int>>   masks,
            int                  skipIdx = -1)
        {
            int correct = 0, evaluated = 0;
            for (int i = 0; i < ts.Count; i++)
            {
                double probSum  = 0.0;
                int    oobCount = 0;
                for (int t = 0; t < trees.Count; t++)
                {
                    if (t == skipIdx || !masks[t].Contains(i)) continue;
                    probSum += GetLeafProb(trees[t], 0, ts[i].Features);
                    oobCount++;
                }
                if (oobCount == 0) continue;
                if ((probSum / oobCount >= 0.5) == (ts[i].Direction > 0)) correct++;
                evaluated++;
            }
            return evaluated > 0 ? (double)correct / evaluated : 0.0;
        }

        // #10: Iterative re-evaluation — after removing one tree, recompute
        // baseline accuracy before evaluating the next candidate.
        int pruned = 0;
        bool changed = true;
        while (changed && newTrees.Count >= 2)
        {
            changed = false;
            double baseAcc = ComputeOobAcc(trainSet, newTrees, oobMasks);
            int    worstK  = -1;
            double bestAcc = baseAcc;

            for (int k = 0; k < newTrees.Count; k++)
            {
                if (newTrees.Count - 1 < 1) break;
                double accWithout = ComputeOobAcc(trainSet, newTrees, oobMasks, skipIdx: k);
                if (accWithout > bestAcc)
                {
                    bestAcc = accWithout;
                    worstK  = k;
                }
            }

            if (worstK >= 0)
            {
                newTrees.RemoveAt(worstK);
                oobMasks.RemoveAt(worstK);
                pruned++;
                changed = true;
            }
        }

        return pruned;
    }

    // ── Meta-label secondary classifier (#10) ────────────────────────────────

    /// <summary>
    /// #23: Meta-label classifier using [rawProb, treeStd, top-5-by-importance features]
    /// instead of the first 5 by index. When <paramref name="featureImportance"/> is available,
    /// the 5 features with highest importance are selected; otherwise falls back to feat[0..4].
    /// </summary>
    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        float[]?             featureImportance = null)
    {
        const int    MetaFeatureDim = 7;   // rawProb + treeStd + 5 raw features
        const int    Epochs         = 30;
        const double Lr             = 0.01;
        const double L2             = 0.001;

        if (calSet.Count < MinCalSamples)
            return (new double[MetaFeatureDim], 0.0);

        // #23: Select top-5 feature indices by importance (or first 5 as fallback)
        int F = calSet.Count > 0 ? calSet[0].Features.Length : 0;
        int topN = Math.Min(5, F);
        int[] topFeatIdx;
        if (featureImportance is { Length: > 0 } && featureImportance.Length >= F)
        {
            topFeatIdx = Enumerable.Range(0, F)
                .OrderByDescending(i => featureImportance[i])
                .Take(topN)
                .ToArray();
        }
        else
        {
            topFeatIdx = Enumerable.Range(0, topN).ToArray();
        }

        int T     = allTrees.Count;
        var mw    = new double[MetaFeatureDim];
        double mb = 0.0;
        var dW    = new double[MetaFeatureDim];
        var metaF = new double[MetaFeatureDim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, MetaFeatureDim);

            foreach (var s in calSet)
            {
                double[] tp       = GetTreeProbs(s.Features, allTrees, trainSet);
                double   rawProb  = tp.Average();
                double   variance = 0.0;
                for (int t = 0; t < tp.Length; t++) { double d = tp[t] - rawProb; variance += d * d; }
                double treeStd = T > 1 ? Math.Sqrt(variance / (T - 1)) : 0.0;

                metaF[0] = rawProb;
                metaF[1] = treeStd;
                for (int i = 0; i < topN; i++)
                    metaF[2 + i] = topFeatIdx[i] < s.Features.Length ? s.Features[topFeatIdx[i]] : 0.0;

                int predicted = rawProb >= 0.5 ? 1 : -1;
                int actual    = s.Direction > 0 ? 1 : -1;
                double label  = predicted == actual ? 1.0 : 0.0;

                double z = mb;
                for (int i = 0; i < MetaFeatureDim; i++) z += mw[i] * metaF[i];
                double pred = MLFeatureHelper.Sigmoid(z);
                double err  = pred - label;

                for (int i = 0; i < MetaFeatureDim; i++) dW[i] += err * metaF[i];
                dB += err;
            }

            int n = calSet.Count;
            for (int i = 0; i < MetaFeatureDim; i++)
                mw[i] -= Lr * (dW[i] / n + L2 * mw[i]);
            mb -= Lr * dB / n;
        }

        return (mw, mb);
    }

    // ── Abstention gate (#6) ──────────────────────────────────────────────────

    /// <summary>
    /// Trains a 3-feature logistic gate on [calibP, treeStd, metaLabelScore].
    /// Label = 1 when the calibrated QRF prediction was correct on the calibration sample.
    /// 50 epochs SGD with L2 regularisation.
    /// Returns (weights, bias, threshold=0.5).
    /// </summary>
    private static (double[] Weights, double Bias, double Threshold) FitAbstentionGate(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        bool                 sweepThreshold = false)
    {
        const int    Dim    = 3;   // [calibP, treeStd, metaLabelScore]
        const int    Epochs = 50;
        const double Lr     = 0.01;
        const double L2     = 0.001;

        if (calSet.Count < 10)
            return (new double[Dim], 0.0, 0.5);

        int T  = allTrees.Count;
        var aw = new double[Dim];
        double ab = 0.0;

        const int MetaDim = 7;
        var dW = new double[Dim];
        var mf = new double[MetaDim];
        var af = new double[Dim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, Dim);

            foreach (var s in calSet)
            {
                double[] tp       = GetTreeProbs(s.Features, allTrees, trainSet);
                double   rawProb  = tp.Average();
                double   rawPC    = Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
                double   calibP   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawPC) + plattB);

                double variance = 0.0;
                for (int t = 0; t < tp.Length; t++) { double d = tp[t] - rawProb; variance += d * d; }
                double treeStd  = T > 1 ? Math.Sqrt(variance / (T - 1)) : 0.0;

                mf[0] = rawProb; mf[1] = treeStd;
                int top = Math.Min(5, s.Features.Length);
                for (int i = 0; i < top; i++) mf[2 + i] = s.Features[i];
                double mz = metaLabelBias;
                for (int i = 0; i < MetaDim && i < metaLabelWeights.Length; i++)
                    mz += metaLabelWeights[i] * mf[i];
                double metaScore = MLFeatureHelper.Sigmoid(mz);

                af[0] = calibP; af[1] = treeStd; af[2] = metaScore;
                double lbl = (calibP >= 0.5) == (s.Direction > 0) ? 1.0 : 0.0;

                double z   = ab;
                for (int i = 0; i < Dim; i++) z += aw[i] * af[i];
                double pred = MLFeatureHelper.Sigmoid(z);
                double err  = pred - lbl;

                for (int i = 0; i < Dim; i++) dW[i] += err * af[i];
                dB += err;
            }

            int n = calSet.Count;
            for (int i = 0; i < Dim; i++)
                aw[i] -= Lr * (dW[i] / n + L2 * aw[i]);
            ab -= Lr * dB / n;
        }

        // #21: Learn optimal abstention threshold by sweeping cal set for best
        // precision at ≥50 % recall (when enabled)
        double threshold = 0.5;
        if (sweepThreshold)
        {
            double bestPrec = 0;
            for (int pct = 30; pct <= 70; pct++)
            {
                double thr = pct / 100.0;
                int tpA = 0, fpA = 0, fnA = 0;
                foreach (var s in calSet)
                {
                    double[] tp2 = GetTreeProbs(s.Features, allTrees, trainSet);
                    double rawP2 = tp2.Average();
                    double rawPC2 = Math.Clamp(rawP2, 1e-7, 1.0 - 1e-7);
                    double cP2 = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawPC2) + plattB);
                    double lbl2 = (cP2 >= 0.5) == (s.Direction > 0) ? 1.0 : 0.0;

                    // Recompute gate features per sample for the threshold sweep
                    double vr2 = 0;
                    for (int t2 = 0; t2 < tp2.Length; t2++) { double d2 = tp2[t2] - rawP2; vr2 += d2 * d2; }
                    double ts2 = T > 1 ? Math.Sqrt(vr2 / (T - 1)) : 0.0;
                    double[] mf2 = [rawP2, ts2, 0, 0, 0, 0, 0];
                    int top2 = Math.Min(5, s.Features.Length);
                    for (int i2 = 0; i2 < top2; i2++) mf2[2 + i2] = s.Features[i2];
                    double mz2 = metaLabelBias;
                    for (int i2 = 0; i2 < MetaDim && i2 < metaLabelWeights.Length; i2++)
                        mz2 += metaLabelWeights[i2] * mf2[i2];
                    double ms2 = MLFeatureHelper.Sigmoid(mz2);

                    double[] af2 = [cP2, ts2, ms2];
                    double gz = ab;
                    for (int i2 = 0; i2 < Dim; i2++) gz += aw[i2] * af2[i2];
                    double gateP = MLFeatureHelper.Sigmoid(gz);
                    bool pass = gateP >= thr;
                    if (pass && lbl2 >= 0.5) tpA++;
                    else if (pass && lbl2 < 0.5) fpA++;
                    else if (!pass && lbl2 >= 0.5) fnA++;
                }
                double recall2 = (tpA + fnA) > 0 ? (double)tpA / (tpA + fnA) : 0;
                double prec2   = (tpA + fpA) > 0 ? (double)tpA / (tpA + fpA) : 0;
                if (recall2 >= 0.50 && prec2 > bestPrec)
                {
                    bestPrec = prec2;
                    threshold = thr;
                }
            }
        }

        return (aw, ab, threshold);
    }

    // ── Per-tree calibration-set accuracy (#7) ────────────────────────────────

    /// <summary>
    /// Computes the binary classification accuracy of each individual tree on
    /// <paramref name="calSet"/> using leaf-fraction probabilities (threshold = 0.5).
    /// Returns an array of T accuracy values in [0, 1].
    /// </summary>
    private static double[] ComputePerTreeCalAccuracies(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        int T = allTrees.Count;
        if (calSet.Count == 0 || T == 0) return new double[T];

        var accuracies = new double[T];

        for (int t = 0; t < T; t++)
        {
            int correct = 0;
            foreach (var s in calSet)
            {
                double p = GetLeafProb(allTrees[t], 0, s.Features);
                if ((p >= 0.5) == (s.Direction > 0)) correct++;
            }
            accuracies[t] = (double)correct / calSet.Count;
        }
        return accuracies;
    }

    // ── Biased importance-weighted feature candidate selection (#12) ──────────

    /// <summary>
    /// Samples exactly <paramref name="sqrtF"/> candidate split features without replacement
    /// from 0…F-1, biased toward features with higher importance scores.
    /// Uses an importance+ε unnormalised weight CDF with binary-search reservoir sampling.
    /// Falls back to sequential padding if the desired count is not reached.
    /// </summary>
    private static List<int> GenerateBiasedCandidateFeats(
        int     F,
        int     sqrtF,
        float[] importanceScores,
        Random  rng)
    {
        double epsilon = 1.0 / F;

        double sum = 0.0;
        var rawWeights = new double[F];
        for (int j = 0; j < F; j++)
        {
            double w = (j < importanceScores.Length ? importanceScores[j] : 0.0) + epsilon;
            rawWeights[j] = w;
            sum += w;
        }

        var cdf = new double[F];
        cdf[0] = rawWeights[0] / sum;
        for (int j = 1; j < F; j++)
            cdf[j] = cdf[j - 1] + rawWeights[j] / sum;

        var selected = new HashSet<int>(sqrtF);
        int attempts = 0;
        while (selected.Count < sqrtF && attempts < F * 10)
        {
            attempts++;
            double u   = rng.NextDouble();
            int    idx = Array.BinarySearch(cdf, u);
            if (idx < 0) idx = ~idx;
            idx = Math.Clamp(idx, 0, F - 1);
            selected.Add(idx);
        }

        for (int j = 0; j < F && selected.Count < sqrtF; j++)
            selected.Add(j);

        return [.. selected];
    }

    // ── GbmTree ↔ TreeNode conversion for JSON persistence ───────────────────
    //
    // Note on naming: QRF trees are serialised using the GbmTree / GbmNode types
    // (and stored in ModelSnapshot.GbmTreesJson) because those types already exist
    // in the shared snapshot format and cover exactly the fields needed (split feature,
    // threshold, left/right child indices, leaf value).  They carry no GBM-specific
    // semantics here — LeafValue holds the QRF leaf-fraction (P(Buy)) rather than a
    // gradient-boosting residual.  Downstream consumers must check ModelSnapshot.Type
    // == "quantilerf" to interpret the leaf values correctly.

    private static List<TreeNode> ConvertGbmToTreeNodes(GbmTree gbmTree)
    {
        if (gbmTree.Nodes is not { Count: > 0 }) return [];

        var nodes = new List<TreeNode>(gbmTree.Nodes.Count);
        foreach (var gn in gbmTree.Nodes)
        {
            nodes.Add(new TreeNode
            {
                SplitFeat     = gn.IsLeaf ? -1 : gn.SplitFeature,
                SplitThresh   = gn.SplitThreshold,
                LeftChild     = gn.LeftChild,
                RightChild    = gn.RightChild,
                LeafDirection = gn.LeafValue,
                // LeafPosCount / LeafTotalCount are rebuilt by RepopulateLeafCounts after loading
            });
        }
        return nodes;
    }

    /// <summary>
    /// Routes every training sample through the loaded warm-start tree and rebuilds the
    /// compact leaf counts (LeafPosCount / LeafTotalCount) from the current training set,
    /// replacing any stale values from the serialised snapshot.
    /// </summary>
    private static void RepopulateLeafCounts(
        List<TreeNode>       nodes,
        int                  rootIndex,
        List<TrainingSample> trainSet)
    {
        // Reset all leaf counts before repopulating
        foreach (var n in nodes) { n.LeafPosCount = 0; n.LeafTotalCount = 0; }

        foreach (var s in trainSet)
            IncrementLeafCount(nodes, rootIndex, s.Features, s.Direction > 0);

        // Sync LeafDirection with repopulated counts so serialization matches training-time probs
        foreach (var n in nodes)
            if (n.SplitFeat < 0) // leaf node
                n.LeafDirection = n.LeafTotalCount > 0
                    ? (double)n.LeafPosCount / n.LeafTotalCount
                    : 0.5;
    }

    private static void IncrementLeafCount(List<TreeNode> nodes, int nodeIndex, float[] features, bool isPositive)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Count) return;
        var node = nodes[nodeIndex];

        if (node.SplitFeat < 0 || node.SplitFeat >= features.Length)
        {
            node.LeafTotalCount++;
            if (isPositive) node.LeafPosCount++;
            return;
        }

        if (features[node.SplitFeat] <= node.SplitThresh)
            IncrementLeafCount(nodes, node.LeftChild,  features, isPositive);
        else
            IncrementLeafCount(nodes, node.RightChild, features, isPositive);
    }

    private static GbmTree ConvertTreeNodesToGbm(List<TreeNode> nodes)
    {
        var gbmNodes = new List<GbmNode>(nodes.Count);
        foreach (var n in nodes)
        {
            bool isLeaf = n.SplitFeat < 0;
            gbmNodes.Add(new GbmNode
            {
                IsLeaf         = isLeaf,
                LeafValue      = n.LeafDirection,
                SplitFeature   = isLeaf ? 0 : n.SplitFeat,
                SplitThreshold = n.SplitThresh,
                LeftChild      = n.LeftChild,
                RightChild     = n.RightChild,
            });
        }
        return new GbmTree { Nodes = gbmNodes };
    }
}
