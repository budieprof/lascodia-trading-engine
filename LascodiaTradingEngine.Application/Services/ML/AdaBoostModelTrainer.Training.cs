using LascodiaTradingEngine.Application.MLModels.Shared;
using static TorchSharp.torch;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    /// <summary>
    /// Initialises per-sample boosting weights with exponential temporal-decay (most recent samples
    /// receive the highest base weight) and class-balance correction (Buy and Sell classes each
    /// receive equal total initial weight = 0.5 before temporal rescaling).
    /// Weights are normalised to sum to 1.
    /// </summary>
    private static double[] InitialiseBoostWeights(
        List<TrainingSample> train,
        double               temporalDecayLambda,
        double[]?            blendWeights = null)
    {
        int n = train.Count;

        int posCount = 0;
        foreach (var s in train) if (s.Direction > 0) posCount++;
        int negCount = n - posCount;

        // Equal total weight per class; uniform within each class
        double posBase = (posCount > 0 && negCount > 0) ? 0.5 / posCount : 1.0 / n;
        double negBase = (posCount > 0 && negCount > 0) ? 0.5 / negCount : 1.0 / n;

        double lambda  = temporalDecayLambda > 0 ? temporalDecayLambda : 0.0;
        double wSum    = 0;
        var    weights = new double[n];

        for (int i = 0; i < n; i++)
        {
            double w = train[i].Direction > 0 ? posBase : negBase;
            if (lambda > 0)
            {
                // t in [0,1]: 0 = oldest, 1 = most recent → recency boost
                double t = (double)i / Math.Max(1, n - 1);
                w *= Math.Exp(lambda * t);
            }
            // Blend in density-ratio / covariate-shift weights (normalised to mean=1 externally)
            if (blendWeights is { Length: > 0 } && i < blendWeights.Length)
                w *= blendWeights[i];
            weights[i] = w;
            wSum += w;
        }

        if (wSum > 0)
            for (int i = 0; i < n; i++) weights[i] /= wSum;
        else
            Array.Fill(weights, 1.0 / n);

        return weights;
    }

    // ── Warm-start weight adjustment ──────────────────────────────────────────

    /// <summary>
    /// Replays the boosting weight updates of the parent ensemble on the current training
    /// set, focusing new residual rounds on the parent's failures.
    /// Supports both SAMME (leaf values ±1, alpha from error) and SAMME.R (leaf values
    /// ½·logit(p), alpha=1.0) via the unified formula:
    ///   w_i ← w_i · exp(−α · y_i · h_k(x_i))
    /// where <c>h_k(x_i)</c> is obtained by the generalized <see cref="PredictStump"/>
    /// traversal — correct for depth-1 stumps and depth-2 trees alike.
    /// Weights are re-normalised after each tree to maintain the AdaBoost sum=1 invariant.
    /// </summary>
    private static void AdjustWarmStartWeights(
        double[]             weights,
        int[]                labels,
        List<TrainingSample> train,
        List<GbmTree>        warmStumps,
        List<double>         warmAlphas)
    {
        int n = train.Count;
        for (int k = 0; k < warmStumps.Count && k < warmAlphas.Count; k++)
        {
            double alpha = warmAlphas[k];
            if (!double.IsFinite(alpha)) continue;

            var stump = warmStumps[k];
            if (stump.Nodes is not { Count: >= 3 }) continue;

            double wSum = 0;
            for (int i = 0; i < n; i++)
            {
                // PredictStump handles depth-1 and depth-2 trees, SAMME (±1) and SAMME.R
                // (½·logit) leaf values. Returns 0 for feature-index out-of-bounds → weight
                // unchanged (exp(0)=1), which is the safest no-op for mismatched features.
                double leafVal = PredictStump(stump, train[i].Features);
                weights[i] *= Math.Exp(-alpha * labels[i] * leafVal);
                wSum += weights[i];
            }
            if (wSum > 0)
                for (int i = 0; i < n; i++) weights[i] /= wSum;
        }
    }

    // ── Best stump search (O(m log m) sorted prefix-sum sweep) ───────────────

    /// <summary>
    /// Finds the weighted-error-minimising decision stump across all F features.
    /// Uses a sorted prefix-sum sweep: O(m log m) per feature vs the naïve O(V×m) scan.
    /// <paramref name="sortKeys"/> and <paramref name="sortIndices"/> are caller-owned reusable
    /// buffers of length ≥ m that are overwritten each call; pre-allocating them outside the
    /// boosting loop eliminates repeated heap allocations.
    /// </summary>
    private static (int Fi, double Thresh, int Parity, double Err) FindBestStump(
        List<TrainingSample> train,
        int[]                labels,
        double[]             weights,
        int                  F,
        double[]             sortKeys,
        int[]                sortIndices,
        bool[]?              activeMask = null)
    {
        int    m          = train.Count;
        double bestErr    = double.MaxValue;
        int    bestFi     = 0;
        double bestThresh = 0;
        int    bestParity = 1;

        for (int fi = 0; fi < F; fi++)
        {
            if (activeMask is not null && fi < activeMask.Length && !activeMask[fi]) continue;
            // Fill sort buffers for this feature
            for (int i = 0; i < m; i++)
            {
                sortKeys[i]    = train[i].Features[fi];
                sortIndices[i] = i;
            }
            // Co-sort keys and indices so indices track original positions
            Array.Sort(sortKeys, sortIndices, 0, m);

            // Compute total positive and negative weight sums
            double totalPos = 0, totalNeg = 0;
            for (int i = 0; i < m; i++)
            {
                int oi = sortIndices[i];
                if (labels[oi] > 0) totalPos += weights[oi];
                else                totalNeg += weights[oi];
            }

            // Sweep thresholds via cumulative prefix sums
            double cumPosLeft = 0, cumNegLeft = 0;
            for (int ti = 0; ti < m - 1; ti++)
            {
                int oi = sortIndices[ti];
                if (labels[oi] > 0) cumPosLeft += weights[oi];
                else                cumNegLeft += weights[oi];

                // Threshold only between adjacent distinct feature values
                if (sortKeys[ti + 1] <= sortKeys[ti] + 1e-12) continue;

                double thresh = (sortKeys[ti] + sortKeys[ti + 1]) * 0.5;

                // Parity +1: predict +1 when x ≤ thresh, −1 when x > thresh
                // err1 = Σ w[i∈neg-left]  + Σ w[i∈pos-right]
                double err1 = cumNegLeft + (totalPos - cumPosLeft);
                double err2 = cumPosLeft + (totalNeg - cumNegLeft);

                if (err1 < bestErr) { bestErr = err1; bestFi = fi; bestThresh = thresh; bestParity =  1; }
                if (err2 < bestErr) { bestErr = err2; bestFi = fi; bestThresh = thresh; bestParity = -1; }
            }
        }

        return (bestFi, bestThresh, bestParity, bestErr);
    }

    // ── Stump construction ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a depth-1 decision tree (stump).
    /// For discrete SAMME <paramref name="leftLeafValue"/> / <paramref name="rightLeafValue"/>
    /// are omitted and the leaves get ±1 values derived from <paramref name="parity"/>.
    /// For SAMME.R supply pre-computed half-logit leaf contributions
    /// (½·logit(p_leaf)) so the existing <see cref="PredictStump"/> path works unchanged.
    /// </summary>
    private static GbmTree BuildStump(
        int    featureIndex,
        double threshold,
        int    parity,
        double leftLeafValue  = double.NaN,
        double rightLeafValue = double.NaN)
    {
        double lv = double.IsNaN(leftLeafValue)  ? (parity > 0 ?  1.0 : -1.0) : leftLeafValue;
        double rv = double.IsNaN(rightLeafValue) ? (parity > 0 ? -1.0 :  1.0) : rightLeafValue;
        return new GbmTree
        {
            Nodes =
            [
                new GbmNode
                {
                    IsLeaf         = false,
                    SplitFeature   = featureIndex,
                    SplitThreshold = threshold,
                    LeftChild      = 1,
                    RightChild     = 2,
                    LeafValue      = 0,
                },
                new GbmNode { IsLeaf = true, LeafValue = lv },
                new GbmNode { IsLeaf = true, LeafValue = rv },
            ]
        };
    }

    // ── SAMME.R stump constructor ──────────────────────────────────────────────

    /// <summary>
    /// Builds a depth-1 SAMME.R stump.  Each leaf stores ½·logit(p_leaf) where
    /// p_leaf = weighted fraction of positive samples in that partition.
    /// Clamped to [Eps, 1−Eps] before logit to avoid ±∞ leaf values.
    /// </summary>
    private static GbmTree BuildSammeRStump(
        int                  featureIndex,
        double               threshold,
        List<TrainingSample> train,
        int[]                labels,
        double[]             weights,
        int                  m)
    {
        double posLeft = 0, totLeft = 0, posRight = 0, totRight = 0;
        for (int i = 0; i < m; i++)
        {
            double xf = train[i].Features[featureIndex];
            if (xf <= threshold) { totLeft  += weights[i]; if (labels[i] > 0) posLeft  += weights[i]; }
            else                 { totRight += weights[i]; if (labels[i] > 0) posRight += weights[i]; }
        }
        double pL = totLeft  > 1e-15 ? Math.Clamp(posLeft  / totLeft,  Eps, 1.0 - Eps) : 0.5;
        double pR = totRight > 1e-15 ? Math.Clamp(posRight / totRight, Eps, 1.0 - Eps) : 0.5;
        double lv = 0.5 * MLFeatureHelper.Logit(pL);
        double rv = 0.5 * MLFeatureHelper.Logit(pR);
        // parity is implicit: lv > rv means left leaf favours Buy, which is consistent with
        // how FindBestStump assigned parity.  We pass parity=1 as a placeholder; BuildStump
        // ignores it when explicit leaf values are provided.
        return BuildStump(featureIndex, threshold, 1, leftLeafValue: lv, rightLeafValue: rv);
    }

    // ── FindBestStumpInSubset ─────────────────────────────────────────────────

    /// <summary>
    /// Same O(m log m) prefix-sum sweep as <see cref="FindBestStump"/> but restricted to
    /// a caller-supplied subset of sample indices.  Used by <see cref="BuildDepth2Tree"/>
    /// to find the best split within the left or right partition of the root node.
    /// Returns (Fi, Thresh, Parity, BestErr, ProbLeft, ProbRight) where ProbLeft/Right are
    /// the weighted fractions of positive samples in each resulting leaf (for SAMME.R).
    /// </summary>
    private static (int Fi, double Thresh, int Parity, double BestErr, double ProbLeft, double ProbRight)
        FindBestStumpInSubset(
            List<TrainingSample> train,
            int[]                labels,
            double[]             weights,
            int                  F,
            List<int>            subset,
            bool[]?              activeMask,
            double[]             tmpKeys,
            int[]                tmpIdx)
    {
        int sz = subset.Count;
        if (sz == 0) return (-1, 0, 1, 0.5, 0.5, 0.5);

        double bestErr    = double.MaxValue;
        int    bestFi     = 0;
        double bestThresh = 0;
        int    bestParity = 1;

        for (int fi = 0; fi < F; fi++)
        {
            if (activeMask is not null && fi < activeMask.Length && !activeMask[fi]) continue;

            for (int k = 0; k < sz; k++)
            {
                int oi = subset[k];
                tmpKeys[k] = train[oi].Features[fi];
                tmpIdx[k]  = oi;
            }
            Array.Sort(tmpKeys, tmpIdx, 0, sz);

            double totalPos = 0, totalNeg = 0;
            for (int k = 0; k < sz; k++)
            {
                int oi = tmpIdx[k];
                if (labels[oi] > 0) totalPos += weights[oi]; else totalNeg += weights[oi];
            }

            double cumPosLeft = 0, cumNegLeft = 0;
            for (int ti = 0; ti < sz - 1; ti++)
            {
                int oi = tmpIdx[ti];
                if (labels[oi] > 0) cumPosLeft += weights[oi]; else cumNegLeft += weights[oi];
                if (tmpKeys[ti + 1] <= tmpKeys[ti] + 1e-12) continue;
                double thresh = (tmpKeys[ti] + tmpKeys[ti + 1]) * 0.5;
                double err1   = cumNegLeft + (totalPos - cumPosLeft);
                double err2   = cumPosLeft + (totalNeg - cumNegLeft);
                if (err1 < bestErr) { bestErr = err1; bestFi = fi; bestThresh = thresh; bestParity =  1; }
                if (err2 < bestErr) { bestErr = err2; bestFi = fi; bestThresh = thresh; bestParity = -1; }
            }
        }

        // Compute leaf probabilities for SAMME.R from the winning split
        double posL = 0, totL = 0, posR = 0, totR = 0;
        foreach (int oi in subset)
        {
            double xf = bestFi < train[oi].Features.Length ? train[oi].Features[bestFi] : 0;
            if (xf <= bestThresh) { totL += weights[oi]; if (labels[oi] > 0) posL += weights[oi]; }
            else                  { totR += weights[oi]; if (labels[oi] > 0) posR += weights[oi]; }
        }
        double pLeft  = totL > 1e-15 ? Math.Clamp(posL / totL, Eps, 1.0 - Eps) : 0.5;
        double pRight = totR > 1e-15 ? Math.Clamp(posR / totR, Eps, 1.0 - Eps) : 0.5;

        return (bestFi, bestThresh, bestParity, bestErr, pLeft, pRight);
    }

    // ── Jointly-optimal depth-2 tree builder ─────────────────────────────────

    /// <summary>
    /// Builds a depth-2 classification tree by jointly optimising root and child splits.
    /// For every candidate root split (feature × threshold), partitions the data, finds the
    /// best child split in each partition via <see cref="FindBestStumpInSubset"/>, and scores
    /// the full tree by total weighted classification error across all 4 leaves.
    /// Selects the (root, left-child, right-child) triple that minimises total error.
    /// Complexity: O(F² · m · log m) per call — use only when F is moderate (≤ 30).
    /// Falls back to the greedy <see cref="BuildDepth2Tree"/> when no valid root split exists.
    /// </summary>
    private static GbmTree BuildJointDepth2Tree(
        List<TrainingSample> train,
        int[]                labels,
        double[]             weights,
        int                  F,
        double[]             sortKeys,
        int[]                sortIndices,
        bool                 sammeR,
        bool[]?              activeMask)
    {
        int m = train.Count;

        // Gather all candidate root splits: for each feature, sweep sorted values
        var rootCandidates = new List<(int Fi, double Thresh)>();
        for (int fi = 0; fi < F; fi++)
        {
            if (activeMask is not null && fi < activeMask.Length && !activeMask[fi]) continue;
            for (int i = 0; i < m; i++)
            {
                sortKeys[i]    = train[i].Features[fi];
                sortIndices[i] = i;
            }
            Array.Sort(sortKeys, sortIndices, 0, m);
            for (int ti = 0; ti < m - 1; ti++)
            {
                if (sortKeys[ti + 1] <= sortKeys[ti] + 1e-12) continue;
                rootCandidates.Add((fi, (sortKeys[ti] + sortKeys[ti + 1]) * 0.5));
            }
        }

        if (rootCandidates.Count == 0)
        {
            // No valid split — return a trivial stump
            double posW = 0, totW = 0;
            for (int i = 0; i < m; i++) { totW += weights[i]; if (labels[i] > 0) posW += weights[i]; }
            double p = totW > 1e-15 ? Math.Clamp(posW / totW, Eps, 1.0 - Eps) : 0.5;
            double lv = sammeR ? 0.5 * MLFeatureHelper.Logit(p) : (p >= 0.5 ? 1.0 : -1.0);
            return BuildStump(0, 0, 1, lv, lv);
        }

        // Temporary buffers for child-split search (avoid allocating inside the loop)
        var tmpKeys = new double[m];
        var tmpIdx  = new int[m];

        double bestTotalErr = double.MaxValue;
        int    bestRootFi   = 0;
        double bestRootThr  = 0;
        int    bestLFi = 0; double bestLThr = 0; int bestLPar = 1; double bestLProbL = 0.5; double bestLProbR = 0.5;
        int    bestRFi = 0; double bestRThr = 0; int bestRPar = 1; double bestRProbL = 0.5; double bestRProbR = 0.5;

        // Re-usable partition lists
        var leftIdx  = new List<int>(m);
        var rightIdx = new List<int>(m);

        foreach (var (rootFi, rootThr) in rootCandidates)
        {
            // Partition samples by root split
            leftIdx.Clear();
            rightIdx.Clear();
            for (int i = 0; i < m; i++)
            {
                if (rootFi < train[i].Features.Length && train[i].Features[rootFi] <= rootThr)
                    leftIdx.Add(i);
                else
                    rightIdx.Add(i);
            }
            if (leftIdx.Count == 0 || rightIdx.Count == 0) continue;

            // Find best child splits
            var (lFi, lThr, lPar, lErr, lPL, lPR) =
                FindBestStumpInSubset(train, labels, weights, F, leftIdx, activeMask, tmpKeys, tmpIdx);
            var (rFi, rThr, rPar, rErr, rPL, rPR) =
                FindBestStumpInSubset(train, labels, weights, F, rightIdx, activeMask, tmpKeys, tmpIdx);

            // Total tree error = left-child error + right-child error (both already weighted)
            double totalErr = lErr + rErr;
            if (totalErr < bestTotalErr)
            {
                bestTotalErr = totalErr;
                bestRootFi   = rootFi;
                bestRootThr  = rootThr;
                bestLFi = lFi; bestLThr = lThr; bestLPar = lPar; bestLProbL = lPL; bestLProbR = lPR;
                bestRFi = rFi; bestRThr = rThr; bestRPar = rPar; bestRProbL = rPL; bestRProbR = rPR;
            }
        }

        // Leaf values
        double llv, lrv, rlv, rrv;
        if (sammeR)
        {
            llv = 0.5 * MLFeatureHelper.Logit(bestLProbL);
            lrv = 0.5 * MLFeatureHelper.Logit(bestLProbR);
            rlv = 0.5 * MLFeatureHelper.Logit(bestRProbL);
            rrv = 0.5 * MLFeatureHelper.Logit(bestRProbR);
        }
        else
        {
            llv = bestLPar > 0 ?  1.0 : -1.0;
            lrv = bestLPar > 0 ? -1.0 :  1.0;
            rlv = bestRPar > 0 ?  1.0 : -1.0;
            rrv = bestRPar > 0 ? -1.0 :  1.0;
        }

        return new GbmTree
        {
            Nodes =
            [
                new GbmNode { IsLeaf = false, SplitFeature = bestRootFi, SplitThreshold = bestRootThr, LeftChild = 1, RightChild = 2 },
                new GbmNode { IsLeaf = false, SplitFeature = bestLFi,    SplitThreshold = bestLThr,    LeftChild = 3, RightChild = 4 },
                new GbmNode { IsLeaf = false, SplitFeature = bestRFi,    SplitThreshold = bestRThr,    LeftChild = 5, RightChild = 6 },
                new GbmNode { IsLeaf = true,  LeafValue = llv },
                new GbmNode { IsLeaf = true,  LeafValue = lrv },
                new GbmNode { IsLeaf = true,  LeafValue = rlv },
                new GbmNode { IsLeaf = true,  LeafValue = rrv },
            ]
        };
    }

    // ── Depth-2 tree builder (greedy) ────────────────────────────────────────

    /// <summary>
    /// Greedily builds a depth-2 classification tree.
    /// <list type="number">
    ///   <item>Partition samples by the already-found root split (rootFi, rootThresh).</item>
    ///   <item>For each partition run <see cref="FindBestStumpInSubset"/> to find the best child split.</item>
    ///   <item>Assemble a 7-node tree:
    ///     node 0 = root; 1 = left-child split; 2 = right-child split;
    ///     3 = LL leaf; 4 = LR leaf; 5 = RL leaf; 6 = RR leaf.</item>
    /// </list>
    /// For SAMME (<paramref name="sammeR"/>=false) leaf values are ±1 from child parity.
    /// For SAMME.R leaf values are ½·logit(p_leaf) derived from weighted class fractions.
    /// Re-uses the caller's <paramref name="sortKeys"/>/<paramref name="sortIndices"/> buffers
    /// (length ≥ m) sequentially — never concurrently.
    /// </summary>
    private static GbmTree BuildDepth2Tree(
        int                  rootFi,
        double               rootThresh,
        List<TrainingSample> train,
        int[]                labels,
        double[]             weights,
        int                  F,
        double[]             sortKeys,
        int[]                sortIndices,
        bool                 sammeR,
        bool[]?              activeMask = null)
    {
        int m = train.Count;

        var leftIdx  = new List<int>(m / 2 + 1);
        var rightIdx = new List<int>(m / 2 + 1);
        for (int i = 0; i < m; i++)
        {
            if (rootFi < train[i].Features.Length && train[i].Features[rootFi] <= rootThresh)
                leftIdx.Add(i);
            else
                rightIdx.Add(i);
        }

        // Find best child splits (reuse sort buffers sequentially)
        var (lFi, lThresh, lParity, _, lProbL, lProbR) =
            FindBestStumpInSubset(train, labels, weights, F, leftIdx,  activeMask, sortKeys, sortIndices);
        var (rFi, rThresh, rParity, _, rProbL, rProbR) =
            FindBestStumpInSubset(train, labels, weights, F, rightIdx, activeMask, sortKeys, sortIndices);

        // Leaf values: ±1 for SAMME, ½·logit(p) for SAMME.R
        double llv, lrv, rlv, rrv;
        if (sammeR)
        {
            llv = 0.5 * MLFeatureHelper.Logit(lProbL);
            lrv = 0.5 * MLFeatureHelper.Logit(lProbR);
            rlv = 0.5 * MLFeatureHelper.Logit(rProbL);
            rrv = 0.5 * MLFeatureHelper.Logit(rProbR);
        }
        else
        {
            llv = lParity > 0 ?  1.0 : -1.0;
            lrv = lParity > 0 ? -1.0 :  1.0;
            rlv = rParity > 0 ?  1.0 : -1.0;
            rrv = rParity > 0 ? -1.0 :  1.0;
        }

        // Node layout: 0=root, 1=left split, 2=right split, 3=LL, 4=LR, 5=RL, 6=RR
        return new GbmTree
        {
            Nodes =
            [
                new GbmNode { IsLeaf = false, SplitFeature = rootFi, SplitThreshold = rootThresh, LeftChild = 1, RightChild = 2 },
                new GbmNode { IsLeaf = false, SplitFeature = lFi,    SplitThreshold = lThresh,    LeftChild = 3, RightChild = 4 },
                new GbmNode { IsLeaf = false, SplitFeature = rFi,    SplitThreshold = rThresh,    LeftChild = 5, RightChild = 6 },
                new GbmNode { IsLeaf = true,  LeafValue = llv },
                new GbmNode { IsLeaf = true,  LeafValue = lrv },
                new GbmNode { IsLeaf = true,  LeafValue = rlv },
                new GbmNode { IsLeaf = true,  LeafValue = rrv },
            ]
        };
    }
}
