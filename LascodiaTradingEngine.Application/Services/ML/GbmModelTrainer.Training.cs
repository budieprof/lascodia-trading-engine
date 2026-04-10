using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    private (List<GbmTree> Trees, double BaseLogOdds, List<HashSet<int>> TreeBagMasks, int InnerTrainCount, List<double> PerTreeLr) FitGbmEnsemble(
        List<TrainingSample> train,
        int                  featureCount,
        int                  numRounds,
        int                  maxDepth,
        double               learningRate,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        TrainingHyperparams  hp,
        CancellationToken    ct,
        int[][]?             interactionConstraints = null,
        int                  baseSeed = 0)
    {
        double temporalDecayLambda = hp.TemporalDecayLambda;
        double colSampleRatio      = hp.FeatureSampleRatio;
        double l2Lambda            = hp.L2Lambda;
        bool   useClassWeights     = hp.UseClassWeights;
        double rowSubsampleRatio   = hp.GbmRowSubsampleRatio;
        int    minSamplesLeaf      = hp.GbmMinSamplesLeaf > 0 ? hp.GbmMinSamplesLeaf : 4;
        double minSplitGain        = hp.GbmMinSplitGain;
        double minSplitGainDecay   = hp.GbmMinSplitGainDecayPerDepth;
        bool   shrinkageAnnealing  = hp.GbmShrinkageAnnealing;
        double dartDropRate        = hp.GbmDartDropRate;
        bool   useHistogram        = hp.GbmUseHistogramSplits;
        int    histogramBins       = hp.GbmHistogramBins > 0 ? hp.GbmHistogramBins : 256;
        bool   leafWise            = hp.GbmUseLeafWiseGrowth;
        int    maxLeaves           = hp.GbmMaxLeaves > 0 ? hp.GbmMaxLeaves : (1 << maxDepth);
        int    valCheckFreq        = hp.GbmValCheckFrequency > 0 ? hp.GbmValCheckFrequency : (numRounds < 30 ? 1 : 5);
        int    earlyStoppingPatience = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : Math.Max(3, numRounds / 10);

        // Clamp valSize so inner trainSet always has at least 10 samples
        int valSize  = Math.Min(Math.Max(20, train.Count / 10), Math.Max(0, train.Count - 10));
        if (valSize < 5) valSize = 0;
        var valSet   = valSize > 0 ? train[^valSize..] : new List<TrainingSample>();
        var trainSet = valSize > 0 ? train[..^valSize] : train;

        // Temporal + density blended weights
        var temporalWeights = ComputeTemporalWeights(trainSet.Count, temporalDecayLambda);
        if (densityWeights is { Length: > 0 } && densityWeights.Length >= temporalWeights.Length)
        {
            double sum = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                temporalWeights[i] *= densityWeights[i];
                sum += temporalWeights[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < temporalWeights.Length; i++) temporalWeights[i] /= sum;
        }

        int n = trainSet.Count;

        // Class weights
        double classWeightBuy  = 1.0;
        double classWeightSell = 1.0;
        if (useClassWeights)
        {
            int buyCount  = trainSet.Count(s => s.Direction > 0);
            int sellCount = n - buyCount;
            if (buyCount > 0 && sellCount > 0)
            {
                classWeightBuy  = (double)n / (2.0 * buyCount);
                classWeightSell = (double)n / (2.0 * sellCount);
            }
        }

        // Row subsampling (configurable)
        double rowSubsampleFrac = rowSubsampleRatio is > 0.0 and <= 1.0 ? rowSubsampleRatio : 0.8;
        int rowSubsampleCount   = Math.Max(10, (int)(n * rowSubsampleFrac));

        // Column subsampling
        bool useColSubsample = colSampleRatio > 0.0 && colSampleRatio < 1.0;
        int colSubsampleCount = useColSubsample
            ? Math.Max(1, (int)Math.Ceiling(colSampleRatio * featureCount))
            : featureCount;

        // Base rate log-odds
        double basePosRate = n > 0
            ? (double)trainSet.Count(s => s.Direction > 0) / n
            : 0.5;
        basePosRate = Math.Clamp(basePosRate, 1e-6, 1 - 1e-6);
        double baseLogOdds = Math.Log(basePosRate / (1 - basePosRate));

        // Warm-start: load prior trees (Item 48: with pruning)
        var trees = new List<GbmTree>(numRounds);
        var perTreeLr = new List<double>(numRounds); // per-tree effective learning rates

        if (warmStart?.GbmTreesJson is not null && warmStart.Type == ModelType)
        {
            bool versionCompatible = TryParseVersion(warmStart.Version, out var warmVersion)
                && warmVersion >= new Version(2, 1);
            if (!versionCompatible)
            {
                _logger.LogWarning(
                    "GBM warm-start: discarding prior trees from version {V} (leaf sign incompatible with ≥2.1)",
                    warmStart.Version);
            }
            else
            {
                try
                {
                    var priorTrees = JsonSerializer.Deserialize<List<GbmTree>>(warmStart.GbmTreesJson, JsonOptions);
                    if (priorTrees is { Count: > 0 })
                    {
                        // Item 48: prune tail of warm-start trees if max configured
                        int maxWarmStart = hp.GbmMaxWarmStartTrees > 0
                            ? hp.GbmMaxWarmStartTrees
                            : priorTrees.Count;
                        if (priorTrees.Count > maxWarmStart)
                        {
                            _logger.LogInformation("GBM warm-start: pruning {Old}→{New} prior trees",
                                priorTrees.Count, maxWarmStart);
                            priorTrees = priorTrees[..maxWarmStart];
                        }
                        trees.AddRange(priorTrees);

                        // Restore per-tree LRs from warm-start, or assume uniform
                        if (warmStart.GbmPerTreeLearningRates is { Length: > 0 } wsLr)
                        {
                            int count = Math.Min(wsLr.Length, trees.Count);
                            for (int ti = 0; ti < count; ti++) perTreeLr.Add(wsLr[ti]);
                            // Pad if warm-start had fewer LRs than trees
                            while (perTreeLr.Count < trees.Count) perTreeLr.Add(learningRate);
                        }
                        else
                        {
                            for (int ti = 0; ti < trees.Count; ti++) perTreeLr.Add(learningRate);
                        }

                        _logger.LogInformation("GBM warm-start: loaded {N} prior trees (gen={Gen})",
                            trees.Count, warmStart.GenerationNumber);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "GBM warm-start: failed to deserialise prior trees, starting fresh.");
                }
            }
        }

        // Initialise scores using per-tree LRs
        double[] scores = new double[n];
        for (int i = 0; i < n; i++)
        {
            scores[i] = baseLogOdds;
            for (int ti = 0; ti < trees.Count; ti++)
                scores[i] += perTreeLr[ti] * Predict(trees[ti], trainSet[i].Features);
        }

        // Item 19: early stopping baseline accounts for warm-start quality on current data
        double bestValLoss = double.MaxValue;
        int patience = 0;
        int bestRound = trees.Count;
        var bestTrees = new List<GbmTree>(trees);
        var bestPerTreeLr = new List<double>(perTreeLr);

        // Compute initial warm-start val loss so we have a proper baseline (Item 19)
        if (valSet.Count >= 10 && trees.Count > 0)
        {
            bestValLoss = ComputeValLoss(valSet, trees, perTreeLr, baseLogOdds);
            bestRound = trees.Count;
            bestTrees = [..trees];
            bestPerTreeLr = [..perTreeLr];
        }

        // Item 25: replay warm-start bag masks with full trainSet coverage
        var bagMasks = new List<HashSet<int>>(numRounds);
        for (int w = 0; w < trees.Count; w++)
        {
            // Warm-start trees were fit on a prior generation. Relative to the current train window
            // they behave like externally supplied predictors, so treat them as OOB for replay metrics.
            bagMasks.Add([]);
        }
        var bestBagMasks = new List<HashSet<int>>(bagMasks);

        // Precompute histogram bins if using histogram-based splits (Item 1)
        int[][]? histBins = null;
        double[][]? histBinEdges = null;
        if (useHistogram)
        {
            (histBins, histBinEdges) = PrecomputeHistogramBins(trainSet, featureCount, histogramBins);
        }

        // CDF for weighted row sampling (Item 4: stall guard)
        var cdf = new double[n];
        cdf[0] = temporalWeights[0];
        for (int i = 1; i < n; i++) cdf[i] = cdf[i - 1] + temporalWeights[i];
        bool useWeightedSampling = rowSubsampleCount < n && cdf[^1] > 1e-15;

        // DART: track active tree mask
        var dartActiveFlags = new bool[numRounds + trees.Count];
        Array.Fill(dartActiveFlags, true);

        for (int r = 0; r < numRounds && !ct.IsCancellationRequested; r++)
        {
            // Item 47: fine-grained cancellation check
            ct.ThrowIfCancellationRequested();

            // Item 16: shrinkage annealing
            double effectiveLr = shrinkageAnnealing
                ? learningRate * (1.0 - (double)r / numRounds)
                : learningRate;
            effectiveLr = Math.Max(effectiveLr, learningRate * 0.01); // floor at 1% of base

            // Item 13: DART — randomly drop trees
            double[] dartScores = scores;
            HashSet<int>? droppedTrees = null;
            if (dartDropRate > 0.0 && trees.Count > 1)
            {
                var dartRng = CreateSeededRandom(baseSeed, r * 97 + 13);
                droppedTrees = new HashSet<int>();
                for (int ti = 0; ti < trees.Count; ti++)
                {
                    if (dartRng.NextDouble() < dartDropRate) droppedTrees.Add(ti);
                }
                if (droppedTrees.Count > 0 && droppedTrees.Count < trees.Count)
                {
                    dartScores = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        dartScores[i] = baseLogOdds;
                        for (int ti = 0; ti < trees.Count; ti++)
                        {
                            if (!droppedTrees.Contains(ti))
                                dartScores[i] += perTreeLr[ti] * Predict(trees[ti], trainSet[i].Features);
                        }
                    }
                }
                else droppedTrees = null;
            }

            // Row subsampling (Item 4: stall guard with fallback cap)
            var rng = CreateSeededRandom(baseSeed, r * 31 + 7);
            int[] rowSample;
            if (rowSubsampleCount < n)
            {
                if (useWeightedSampling)
                {
                    var selected = new HashSet<int>(rowSubsampleCount);
                    int maxAttempts = rowSubsampleCount * 3; // Item 4: stall guard
                    int attempts = 0;
                    while (selected.Count < rowSubsampleCount && attempts < maxAttempts)
                    {
                        double u = rng.NextDouble() * cdf[^1];
                        int lo = 0, hi = n - 1;
                        while (lo < hi)
                        {
                            int mid = (lo + hi) >> 1;
                            if (cdf[mid] < u) lo = mid + 1; else hi = mid;
                        }
                        selected.Add(lo);
                        attempts++;
                    }
                    // Pad with uniform if stalled
                    while (selected.Count < rowSubsampleCount)
                        selected.Add(rng.Next(n));
                    rowSample = [..selected];
                }
                else
                {
                    var allIdx = new int[n];
                    for (int i = 0; i < n; i++) allIdx[i] = i;
                    for (int i = 0; i < rowSubsampleCount; i++)
                    {
                        int j = i + rng.Next(n - i);
                        (allIdx[i], allIdx[j]) = (allIdx[j], allIdx[i]);
                    }
                    rowSample = allIdx[..rowSubsampleCount];
                }
            }
            else
            {
                rowSample = Enumerable.Range(0, n).ToArray();
            }

            bagMasks.Add(new HashSet<int>(rowSample));

            // Column subsampling (with interaction constraints — Item 18)
            int[] colSample;
            if (useColSubsample)
            {
                var allCols = new int[featureCount];
                for (int i = 0; i < featureCount; i++) allCols[i] = i;
                for (int i = 0; i < colSubsampleCount; i++)
                {
                    int j = i + rng.Next(featureCount - i);
                    (allCols[i], allCols[j]) = (allCols[j], allCols[i]);
                }
                colSample = allCols[..colSubsampleCount];
                Array.Sort(colSample);
            }
            else
            {
                colSample = Enumerable.Range(0, featureCount).ToArray();
            }

            // Compute pseudo-residuals (use dartScores if DART active)
            var activeScores = droppedTrees is not null ? dartScores : scores;
            var residuals     = new double[n];
            var hessians      = new double[n];
            var sampleWeights = new double[n];
            for (int i = 0; i < n; i++)
            {
                double p = Sigmoid(activeScores[i]);
                int rawY = trainSet[i].Direction > 0 ? 1 : 0;
                double y = labelSmoothing > 0
                    ? rawY * (1 - labelSmoothing) + 0.5 * labelSmoothing
                    : rawY;
                double cw = trainSet[i].Direction > 0 ? classWeightBuy : classWeightSell;
                residuals[i]     = (y - p) * cw;
                hessians[i]      = p * (1 - p) * cw;
                sampleWeights[i] = temporalWeights[i];
            }

            // Item 14: depth-decayed min split gain
            double scaledMinCW = n > 0 ? MinChildWeight / n : MinChildWeight;
            var indices = rowSample.ToList();
            var tree = new GbmTree();

            if (leafWise)
            {
                // Item 2: Leaf-wise (best-first) tree growth
                BuildTreeLeafWise(tree, indices, trainSet, residuals, hessians, sampleWeights,
                    colSample, maxDepth, l2Lambda, scaledMinCW, minSamplesLeaf,
                    minSplitGain, minSplitGainDecay, maxLeaves,
                    interactionConstraints, useHistogram ? histBins : null,
                    useHistogram ? histBinEdges : null, histogramBins, ct);
            }
            else
            {
                BuildTree(tree, indices, trainSet, residuals, hessians, sampleWeights,
                    colSample, maxDepth, l2Lambda, scaledMinCW, minSamplesLeaf,
                    minSplitGain, minSplitGainDecay, interactionConstraints,
                    useHistogram ? histBins : null, useHistogram ? histBinEdges : null,
                    histogramBins, ct);
            }

            trees.Add(tree);
            ClipLeafValues(tree, LeafClipValue);

            // DART: correct rescaling per the DART paper
            // New tree: scale by 1/(D+1), each dropped tree: scale by D/(D+1)
            if (droppedTrees is { Count: > 0 })
            {
                int D = droppedTrees.Count;
                double newTreeScale = 1.0 / (D + 1);
                double droppedTreeScale = (double)D / (D + 1);
                ScaleTreeLeaves(tree, newTreeScale);
                foreach (int di in droppedTrees)
                    ScaleTreeLeaves(trees[di], droppedTreeScale);

                // Bug fix 1: recompute scores from scratch after DART rescaling
                // to avoid drift between scores[] and actual tree outputs
                perTreeLr.Add(effectiveLr);
                for (int i = 0; i < n; i++)
                {
                    scores[i] = baseLogOdds;
                    for (int ti = 0; ti < trees.Count; ti++)
                        scores[i] += perTreeLr[ti] * Predict(trees[ti], trainSet[i].Features);
                }
            }
            else
            {
                // Non-DART: record per-tree LR and incrementally update scores
                perTreeLr.Add(effectiveLr);
                for (int i = 0; i < n; i++)
                    scores[i] += effectiveLr * Predict(tree, trainSet[i].Features);
            }

            // Validation loss for early stopping (Item 17: configurable frequency)
            if (valSet.Count >= 10 && r % valCheckFreq == valCheckFreq - 1)
            {
                double valLoss = ComputeValLoss(valSet, trees, perTreeLr, baseLogOdds);

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss  = valLoss;
                    bestRound    = trees.Count;
                    bestTrees    = [..trees];
                    bestPerTreeLr = [..perTreeLr];
                    bestBagMasks = [..bagMasks];
                    patience     = 0;
                }
                else if (++patience >= earlyStoppingPatience)
                {
                    _logger.LogDebug("GBM early stopping at round {R} (best at {Best})", trees.Count, bestRound);
                    break;
                }
            }
        }

        // Restore best ensemble
        if (bestTrees.Count > 0 && bestTrees.Count < trees.Count)
        {
            trees     = bestTrees;
            bagMasks  = bestBagMasks;
            perTreeLr = bestPerTreeLr;
        }

        return (trees, baseLogOdds, bagMasks, trainSet.Count, perTreeLr);

        static bool TryParseVersion(string? rawVersion, out Version version)
        {
            if (Version.TryParse(rawVersion, out version!))
                return true;

            if (!string.IsNullOrWhiteSpace(rawVersion))
            {
                var numericPrefix = new string(rawVersion
                    .TakeWhile(c => char.IsDigit(c) || c == '.')
                    .ToArray());
                if (Version.TryParse(numericPrefix, out version!))
                    return true;
            }

            version = new Version(0, 0);
            return false;
        }
    }

    // ── Validation loss using per-tree learning rates ──────────────────────
    private static double ComputeValLoss(List<TrainingSample> valSet, List<GbmTree> trees,
        List<double> perTreeLr, double baseLogOdds)
    {
        double totalLoss = 0;
        foreach (var s in valSet)
        {
            double sc = baseLogOdds;
            for (int ti = 0; ti < trees.Count; ti++)
                sc += perTreeLr[ti] * Predict(trees[ti], s.Features);
            double p = Sigmoid(sc);
            int y    = s.Direction > 0 ? 1 : 0;
            totalLoss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
        }
        return totalLoss / valSet.Count;
    }

    // ── XGBoost-style constants ──────────────────────────────────────────────
    private const double MinChildWeight = 1.0;
    private const double LeafClipValue  = 5.0;

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE BUILDING — Level-wise (default)
    // ═══════════════════════════════════════════════════════════════════════

    private static void BuildTree(
        GbmTree tree, List<int> indices,
        IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight,
        int minSamplesLeaf = 4, double minSplitGain = 0.0,
        double minSplitGainDecay = 0.0, int[][]? interactionConstraints = null,
        int[][]? histBins = null, double[][]? histBinEdges = null, int histogramBinCount = 256,
        CancellationToken ct = default)
    {
        // Item 5: sequential node allocation (no gaps)
        tree.Nodes = new List<GbmNode>();
        BuildNodeSequential(tree.Nodes, indices, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, 0, minSamplesLeaf, minSplitGain,
            minSplitGainDecay, interactionConstraints, null,
            histBins, histBinEdges, histogramBinCount, ct);
    }

    /// <summary>
    /// Builds nodes using sequential allocation (no heap gaps). Each node stores
    /// explicit LeftChild/RightChild indices into the flat Nodes list.
    /// </summary>
    private static void BuildNodeSequential(
        List<GbmNode> nodes, List<int> indices,
        IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight,
        int depth, int minSamplesLeaf, double minSplitGain,
        double minSplitGainDecay, int[][]? interactionConstraints,
        HashSet<int>? usedFeatureGroups,
        int[][]? histBins, double[][]? histBinEdges, int histogramBinCount,
        CancellationToken ct)
    {
        // Item 47: cancellation check in tree building
        if (ct.IsCancellationRequested) { AddLeafNode(nodes, 0); return; }

        int nodeIdx = nodes.Count;
        nodes.Add(new GbmNode());
        var node = nodes[nodeIdx];

        double G = 0, H = 0;
        foreach (int i in indices)
        {
            G += sampleWeights[i] * gradients[i];
            H += sampleWeights[i] * hessians[i];
        }

        double leafVal = (H + l2Lambda) > 1e-15 ? G / (H + l2Lambda) : 0;
        node.LeafValue = leafVal;

        if (depth >= maxDepth || indices.Count < minSamplesLeaf || H < minChildWeight)
        {
            node.IsLeaf = true;
            return;
        }

        // Item 14: depth-decayed min split gain
        double effectiveMinGain = minSplitGain;
        if (minSplitGainDecay > 0.0)
            effectiveMinGain = minSplitGain * Math.Pow(1.0 - minSplitGainDecay, depth);

        // Item 18: filter columns by interaction constraints
        int[] effectiveCols = colSubset;
        if (interactionConstraints is not null && usedFeatureGroups is not null)
        {
            effectiveCols = FilterByInteractionConstraints(colSubset, interactionConstraints, usedFeatureGroups);
            if (effectiveCols.Length == 0) effectiveCols = colSubset; // fallback
        }

        var (bestGain, bestFi, bestThresh) = histBins is not null
            ? FindBestSplitHistogram(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight, histBins, histBinEdges!, histogramBinCount)
            : FindBestSplitExact(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight);

        if (bestGain <= effectiveMinGain) { node.IsLeaf = true; return; }

        node.SplitFeature   = bestFi;
        node.SplitThreshold = bestThresh;
        node.SplitGain      = bestGain;

        var leftIdx  = indices.Where(i => samples[i].Features[bestFi] <= bestThresh).ToList();
        var rightIdx = indices.Where(i => samples[i].Features[bestFi] > bestThresh).ToList();

        if (leftIdx.Count < minSamplesLeaf || rightIdx.Count < minSamplesLeaf)
        {
            node.IsLeaf = true;
            return;
        }

        // Track used feature groups for interaction constraints
        var nextUsedGroups = usedFeatureGroups is not null ? new HashSet<int>(usedFeatureGroups) : new HashSet<int>();
        if (interactionConstraints is not null)
        {
            for (int g = 0; g < interactionConstraints.Length; g++)
                if (interactionConstraints[g].Contains(bestFi))
                    nextUsedGroups.Add(g);
        }

        node.LeftChild = nodes.Count; // will be next allocated
        BuildNodeSequential(nodes, leftIdx, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, depth + 1, minSamplesLeaf, minSplitGain,
            minSplitGainDecay, interactionConstraints, nextUsedGroups,
            histBins, histBinEdges, histogramBinCount, ct);

        node.RightChild = nodes.Count; // will be next allocated
        BuildNodeSequential(nodes, rightIdx, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, depth + 1, minSamplesLeaf, minSplitGain,
            minSplitGainDecay, interactionConstraints, nextUsedGroups,
            histBins, histBinEdges, histogramBinCount, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE BUILDING — Leaf-wise (best-first) (Item 2)
    // ═══════════════════════════════════════════════════════════════════════

    private static void BuildTreeLeafWise(
        GbmTree tree, List<int> indices,
        IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight,
        int minSamplesLeaf, double minSplitGain, double minSplitGainDecay,
        int maxLeaves, int[][]? interactionConstraints,
        int[][]? histBins, double[][]? histBinEdges, int histogramBinCount,
        CancellationToken ct)
    {
        tree.Nodes = new List<GbmNode>();

        // Priority queue caches (nodeIdx, indices, depth, usedGroups, splitFeature, splitThreshold, gain)
        // to avoid redundant split recomputation on dequeue
        var queue = new PriorityQueue<(int NodeIdx, List<int> Indices, int Depth, HashSet<int> UsedGroups, int SplitFi, double SplitThresh, double Gain), double>();
        int leafCount = 1;

        // Create root
        var root = CreateLeafNode(tree.Nodes, indices, gradients, hessians, sampleWeights, l2Lambda);
        var rootUsedGroups = new HashSet<int>();
        var (rootGain, rootFi, rootThresh) = FindBestSplitForNode(indices, samples, gradients, hessians,
            sampleWeights, colSubset, l2Lambda, minChildWeight, histBins, histBinEdges, histogramBinCount,
            interactionConstraints, rootUsedGroups);
        if (rootGain > minSplitGain)
            queue.Enqueue((root, indices, 0, rootUsedGroups, rootFi, rootThresh, rootGain), -rootGain);

        while (queue.Count > 0 && leafCount < maxLeaves && !ct.IsCancellationRequested)
        {
            var (nodeIdx, nodeIndices, depth, usedGroups, fi, thresh, gain) = queue.Dequeue();
            var node = tree.Nodes[nodeIdx];

            double effectiveMinGain = minSplitGain;
            if (minSplitGainDecay > 0.0)
                effectiveMinGain = minSplitGain * Math.Pow(1.0 - minSplitGainDecay, depth);

            if (gain <= effectiveMinGain || depth >= maxDepth) continue;

            var leftIdx  = nodeIndices.Where(i => samples[i].Features[fi] <= thresh).ToList();
            var rightIdx = nodeIndices.Where(i => samples[i].Features[fi] > thresh).ToList();

            if (leftIdx.Count < minSamplesLeaf || rightIdx.Count < minSamplesLeaf) continue;

            node.IsLeaf = false;
            node.SplitFeature = fi;
            node.SplitThreshold = thresh;
            node.SplitGain = gain;
            node.LeftChild = tree.Nodes.Count;
            int leftNodeIdx = CreateLeafNode(tree.Nodes, leftIdx, gradients, hessians, sampleWeights, l2Lambda);
            node.RightChild = tree.Nodes.Count;
            int rightNodeIdx = CreateLeafNode(tree.Nodes, rightIdx, gradients, hessians, sampleWeights, l2Lambda);
            leafCount++; // net +1 (split one leaf into two)

            var nextUsedGroups = new HashSet<int>(usedGroups);
            if (interactionConstraints is not null)
            {
                for (int g = 0; g < interactionConstraints.Length; g++)
                {
                    if (interactionConstraints[g].Contains(fi))
                        nextUsedGroups.Add(g);
                }
            }

            // Enqueue children with pre-computed splits
            if (depth + 1 < maxDepth && leafCount < maxLeaves)
            {
                var (lGain, lFi, lThresh) = FindBestSplitForNode(leftIdx, samples, gradients, hessians,
                    sampleWeights, colSubset, l2Lambda, minChildWeight, histBins, histBinEdges, histogramBinCount,
                    interactionConstraints, nextUsedGroups);
                if (lGain > 0)
                    queue.Enqueue((leftNodeIdx, leftIdx, depth + 1, new HashSet<int>(nextUsedGroups), lFi, lThresh, lGain), -lGain);

                var (rGain, rFi, rThresh) = FindBestSplitForNode(rightIdx, samples, gradients, hessians,
                    sampleWeights, colSubset, l2Lambda, minChildWeight, histBins, histBinEdges, histogramBinCount,
                    interactionConstraints, nextUsedGroups);
                if (rGain > 0)
                    queue.Enqueue((rightNodeIdx, rightIdx, depth + 1, new HashSet<int>(nextUsedGroups), rFi, rThresh, rGain), -rGain);
            }
        }
    }

    private static int CreateLeafNode(List<GbmNode> nodes, List<int> indices,
        double[] gradients, double[] hessians, double[] sampleWeights, double l2Lambda)
    {
        double G = 0, H = 0;
        foreach (int i in indices) { G += sampleWeights[i] * gradients[i]; H += sampleWeights[i] * hessians[i]; }
        int idx = nodes.Count;
        nodes.Add(new GbmNode { IsLeaf = true, LeafValue = (H + l2Lambda) > 1e-15 ? G / (H + l2Lambda) : 0 });
        return idx;
    }

    private static void AddLeafNode(List<GbmNode> nodes, double value)
    {
        nodes.Add(new GbmNode { IsLeaf = true, LeafValue = value });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SPLIT FINDING — Exact and Histogram
    // ═══════════════════════════════════════════════════════════════════════

    private static (double Gain, int Feature, double Threshold) FindBestSplitForNode(
        List<int> indices, IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, double l2Lambda, double minChildWeight,
        int[][]? histBins, double[][]? histBinEdges, int histogramBinCount,
        int[][]? interactionConstraints = null, HashSet<int>? usedFeatureGroups = null)
    {
        double G = 0, H = 0;
        foreach (int i in indices) { G += sampleWeights[i] * gradients[i]; H += sampleWeights[i] * hessians[i]; }

        int[] effectiveCols = colSubset;
        if (interactionConstraints is not null && usedFeatureGroups is not null)
        {
            effectiveCols = FilterByInteractionConstraints(colSubset, interactionConstraints, usedFeatureGroups);
            if (effectiveCols.Length == 0)
                effectiveCols = colSubset;
        }

        return histBins is not null
            ? FindBestSplitHistogram(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight, histBins, histBinEdges!, histogramBinCount)
            : FindBestSplitExact(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight);
    }

    /// <summary>Exact split search: O(n·m·log n) per node.</summary>
    private static (double Gain, int Feature, double Threshold) FindBestSplitExact(
        List<int> indices, IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, double G, double H, double l2Lambda, double minChildWeight)
    {
        double bestGain = 0, bestThresh = 0;
        int bestFi = 0;
        double parentScore = G * G / (H + l2Lambda);
        var sortBuf = new int[indices.Count];

        foreach (int fi in colSubset)
        {
            indices.CopyTo(sortBuf);
            Array.Sort(sortBuf, 0, indices.Count,
                Comparer<int>.Create((a, b) => samples[a].Features[fi].CompareTo(samples[b].Features[fi])));

            double leftG = 0, leftH = 0;
            for (int ti = 0; ti < indices.Count - 1; ti++)
            {
                int idx = sortBuf[ti];
                double wi = sampleWeights[idx];
                leftG  += wi * gradients[idx];
                leftH  += wi * hessians[idx];
                double rightG = G - leftG, rightH = H - leftH;

                if (Math.Abs(samples[idx].Features[fi] - samples[sortBuf[ti + 1]].Features[fi]) < 1e-10)
                    continue;
                if (leftH < minChildWeight || rightH < minChildWeight) continue;

                double gain = 0.5 * (leftG * leftG / (leftH + l2Lambda)
                                   + rightG * rightG / (rightH + l2Lambda)
                                   - parentScore);

                if (gain > bestGain)
                {
                    bestGain = gain; bestFi = fi;
                    bestThresh = (samples[idx].Features[fi] + samples[sortBuf[ti + 1]].Features[fi]) / 2.0;
                }
            }
        }

        return (bestGain, bestFi, bestThresh);
    }

    /// <summary>Item 1: Histogram-based split search: O(n + bins·m) per node.</summary>
    private static (double Gain, int Feature, double Threshold) FindBestSplitHistogram(
        List<int> indices, IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, double G, double H, double l2Lambda, double minChildWeight,
        int[][] histBins, double[][] histBinEdges, int numBins)
    {
        double bestGain = 0, bestThresh = 0;
        int bestFi = 0;
        double parentScore = G * G / (H + l2Lambda);

        foreach (int fi in colSubset)
        {
            var binG = new double[numBins];
            var binH = new double[numBins];

            foreach (int idx in indices)
            {
                int bin = histBins[fi][idx];
                double wi = sampleWeights[idx];
                binG[bin] += wi * gradients[idx];
                binH[bin] += wi * hessians[idx];
            }

            double leftG = 0, leftH = 0;
            for (int b = 0; b < numBins - 1; b++)
            {
                leftG += binG[b];
                leftH += binH[b];
                double rightG = G - leftG, rightH = H - leftH;

                if (leftH < minChildWeight || rightH < minChildWeight) continue;

                double gain = 0.5 * (leftG * leftG / (leftH + l2Lambda)
                                   + rightG * rightG / (rightH + l2Lambda)
                                   - parentScore);
                if (gain > bestGain)
                {
                    bestGain = gain; bestFi = fi;
                    bestThresh = histBinEdges[fi][b];
                }
            }
        }

        return (bestGain, bestFi, bestThresh);
    }

    /// <summary>Item 1: Precompute histogram bins for all features.</summary>
    private static (int[][] Bins, double[][] BinEdges) PrecomputeHistogramBins(
        List<TrainingSample> samples, int featureCount, int numBins)
    {
        int n = samples.Count;
        var bins = new int[featureCount][];
        var binEdges = new double[featureCount][];

        for (int fi = 0; fi < featureCount; fi++)
        {
            var values = new float[n];
            for (int i = 0; i < n; i++) values[i] = samples[i].Features[fi];

            float fmin = values.Min(), fmax = values.Max();
            double range = fmax - fmin;
            double binWidth = range > 1e-15 ? range / numBins : 1.0;

            bins[fi] = new int[n];
            binEdges[fi] = new double[numBins];
            for (int b = 0; b < numBins; b++)
                binEdges[fi][b] = fmin + (b + 1) * binWidth;

            for (int i = 0; i < n; i++)
                bins[fi][i] = Math.Clamp((int)((values[i] - fmin) / binWidth), 0, numBins - 1);
        }

        return (bins, binEdges);
    }

    /// <summary>Item 18: Filter columns by interaction constraints.</summary>
    private static int[] FilterByInteractionConstraints(int[] colSubset, int[][] constraints, HashSet<int> usedGroups)
    {
        if (usedGroups.Count == 0) return colSubset;
        var allowed = new HashSet<int>();
        foreach (int g in usedGroups)
            if (g < constraints.Length)
                foreach (int f in constraints[g])
                    allowed.Add(f);
        // Also allow features not in any group
        var allGrouped = new HashSet<int>();
        foreach (var group in constraints)
            foreach (int f in group)
                allGrouped.Add(f);

        var result = colSubset.Where(c => allowed.Contains(c) || !allGrouped.Contains(c)).ToArray();
        return result.Length > 0 ? result : colSubset;
    }

    private static void ClipLeafValues(GbmTree tree, double clipValue)
    {
        if (tree.Nodes is null) return;
        foreach (var node in tree.Nodes)
            if (node.IsLeaf)
                node.LeafValue = Math.Clamp(node.LeafValue, -clipValue, clipValue);
    }

    private static void ScaleTreeLeaves(GbmTree tree, double scale)
    {
        if (tree.Nodes is null) return;
        foreach (var node in tree.Nodes)
            if (node.IsLeaf)
                node.LeafValue *= scale;
    }

    private static double[] ComputeTemporalWeights(int count, double lambda)
    {
        if (count == 0) return [];
        var w = new double[count];
        for (int i = 0; i < count; i++) w[i] = Math.Exp(lambda * ((double)i / Math.Max(1, count - 1) - 1.0));
        double sum = w.Sum();
        if (sum > 1e-15) for (int i = 0; i < count; i++) w[i] /= sum;
        return w;
    }

    private static bool IsNonStationary(double[] series)
    {
        int N = series.Length;
        if (N < 20) return false;
        var dx = new double[N - 1];
        for (int i = 0; i < dx.Length; i++) dx[i] = series[i + 1] - series[i];

        int p = Math.Min(12, (int)Math.Floor(Math.Pow(N - 1, 1.0 / 3.0)));
        int start = p + 1;
        int nObs = dx.Length - start;
        if (nObs < 10) return false;

        int cols = 2 + p;
        var X = new double[nObs * cols]; var Y = new double[nObs];
        for (int t = 0; t < nObs; t++)
        {
            int ti = start + t; Y[t] = dx[ti];
            X[t * cols + 0] = 1.0; X[t * cols + 1] = series[ti];
            for (int k = 0; k < p; k++) X[t * cols + 2 + k] = dx[ti - 1 - k];
        }

        var xtx = new double[cols * cols]; var xty = new double[cols];
        for (int t = 0; t < nObs; t++)
            for (int a = 0; a < cols; a++)
            {
                double xa = X[t * cols + a]; xty[a] += xa * Y[t];
                for (int b2 = a; b2 < cols; b2++)
                { double v = xa * X[t * cols + b2]; xtx[a * cols + b2] += v; if (a != b2) xtx[b2 * cols + a] += v; }
            }

        var L = new double[cols * cols];
        for (int i = 0; i < cols; i++)
        {
            for (int j2 = 0; j2 <= i; j2++)
            {
                double sum = 0;
                for (int k = 0; k < j2; k++) sum += L[i * cols + k] * L[j2 * cols + k];
                if (i == j2) { double diag = xtx[i * cols + i] - sum; if (diag <= 1e-15) return false; L[i * cols + j2] = Math.Sqrt(diag); }
                else L[i * cols + j2] = (xtx[i * cols + j2] - sum) / L[j2 * cols + j2];
            }
        }

        var z = new double[cols];
        for (int i = 0; i < cols; i++) { double sum = 0; for (int k = 0; k < i; k++) sum += L[i * cols + k] * z[k]; z[i] = (xty[i] - sum) / L[i * cols + i]; }
        var beta = new double[cols];
        for (int i = cols - 1; i >= 0; i--) { double sum = 0; for (int k = i + 1; k < cols; k++) sum += L[k * cols + i] * beta[k]; beta[i] = (z[i] - sum) / L[i * cols + i]; }

        double gamma = beta[1];
        double sse = 0;
        for (int t = 0; t < nObs; t++) { double pred = 0; for (int c = 0; c < cols; c++) pred += X[t * cols + c] * beta[c]; double resid = Y[t] - pred; sse += resid * resid; }
        double sigma2 = sse / Math.Max(1, nObs - cols);

        var Linv = new double[cols * cols];
        for (int i = 0; i < cols; i++)
        {
            Linv[i * cols + i] = 1.0 / L[i * cols + i];
            for (int j2 = i + 1; j2 < cols; j2++)
            { double sum = 0; for (int k = i; k < j2; k++) sum += L[j2 * cols + k] * Linv[k * cols + i]; Linv[j2 * cols + i] = -sum / L[j2 * cols + j2]; }
        }

        double varGamma = 0;
        for (int k = 0; k < cols; k++) varGamma += Linv[k * cols + 1] * Linv[k * cols + 1];
        varGamma *= sigma2;
        if (varGamma <= 1e-15) return false;

        double tStat = gamma / Math.Sqrt(varGamma);

        // Item 34: Interpolated ADF critical values (5% level, with constant)
        double criticalValue = InterpolateAdfCritical(nObs);
        return tStat > criticalValue;
    }

    /// <summary>Item 34: Interpolate ADF critical values from standard table.</summary>
    private static double InterpolateAdfCritical(int n)
    {
        // (N, critical_value_5pct) from Fuller's table (with constant, no trend)
        ReadOnlySpan<(int N, double CV)> table = [(25, -3.00), (50, -2.93), (100, -2.89), (250, -2.88), (500, -2.86), (1000, -2.86)];
        if (n <= table[0].N) return table[0].CV;
        if (n >= table[^1].N) return table[^1].CV;
        for (int i = 0; i < table.Length - 1; i++)
        {
            if (n <= table[i + 1].N)
            {
                double frac = (double)(n - table[i].N) / (table[i + 1].N - table[i].N);
                return table[i].CV + frac * (table[i + 1].CV - table[i].CV);
            }
        }
        return -2.86;
    }
}
