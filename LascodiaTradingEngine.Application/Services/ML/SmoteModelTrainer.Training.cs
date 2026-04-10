using System.Buffers;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class SmoteModelTrainer
{

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        CancellationToken    ct)
    {
        int folds    = Math.Clamp(hp.WalkForwardFolds, 2, 5);
        int embargo  = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);

        if (foldSize < MinFoldSize)
        {
            _logger.LogWarning("SmoteModelTrainer CV: fold size too small ({Size}), skipping.", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        // ── H3/H14/M15: Parallel fold results ────────────────────────────────
        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        // Limit CV parallelism to avoid thread pool starvation from nested Parallel.For
        int cvMaxDop = Math.Max(1, Environment.ProcessorCount / 2);
        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = cvMaxDop }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;

            // M15: PurgeHorizonBars — remove samples whose label horizon overlaps test fold
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples) return;

            var foldTrain = samples[..trainEnd].ToList();

            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count)
                    foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) return;

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(20, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(3,  hp.EarlyStoppingPatience / 2),
                K                     = Math.Max(10, hp.K / 3),
                NclLambda             = 0.0,  // force independent/parallel in CV folds
                DiversityLambda       = 0.0,
            };

            int foldOrigCount = foldTrain.Count;
            var (balancedFold, _, _) = ApplySmote(foldTrain, hp, F, ct);
            var cvEnsResult = FitEnsemble(
                balancedFold, cvHp, F, cvHp.K, hp.LabelSmoothing, null, null, ct,
                forceSequential: false, originalCount: foldOrigCount);
            var w = cvEnsResult.Weights; var b = cvEnsResult.Biases;
            var subs = cvEnsResult.FeatureSubsets; var cvMlpHW = cvEnsResult.MlpHW; var cvMlpHB = cvEnsResult.MlpHB;
            var (mw, mb) = FitLinearRegressor(balancedFold, F, cvHp);

            // Per-fold calibration: split test into mini-cal (30%) and mini-eval (70%)
            int miniCalEnd = Math.Max(1, foldTest.Count * 3 / 10);
            var foldMiniCal = foldTest[..miniCalEnd];
            var foldMiniEval = foldTest[miniCalEnd..];
            var foldEns = new EnsembleState(w, b, F, subs, MetaLearner.None, cvMlpHW, cvMlpHB, cvHp.MlpHiddenDim);
            var (foldPlattA, foldPlattB) = foldMiniCal.Count >= 5
                ? FitPlattScaling(foldMiniCal, foldEns) : (1.0, 0.0);
            var m = foldMiniEval.Count >= 10
                ? EvaluateEnsemble(foldMiniEval, w, b, mw, mb, foldPlattA, foldPlattB, F, subs, MetaLearner.None, 0.0,
                    cvMlpHW, cvMlpHB, cvHp.MlpHiddenDim)
                : EvaluateEnsemble(foldTest, w, b, mw, mb, 1.0, 0.0, F, subs, MetaLearner.None, 0.0,
                    cvMlpHW, cvMlpHB, cvHp.MlpHiddenDim);

            // H14: Per-feature mean |weight| for stability scoring
            var foldImp = new double[F];
            for (int ki = 0; ki < w.Length; ki++)
            {
                int[] s2 = subs is not null && ki < subs.Length
                    ? subs[ki]
                    : [.. Enumerable.Range(0, Math.Min(w[ki].Length, F))];
                foreach (int j in s2)
                    if (j < F) foldImp[j] += Math.Abs(w[ki][j]);
            }
            double kCount = w.Length > 0 ? w.Length : 1.0;
            for (int j = 0; j < F; j++) foldImp[j] /= kCount;

            // H3: Equity-curve gate — simulate P&L on fold test
            var preds = new (int Pred, int Actual)[foldTest.Count];
            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                double rawP = EnsembleProb(foldTest[pi].Features, w, b, F, subs, MetaLearner.None,
                    cvMlpHW, cvMlpHB, cvHp.MlpHiddenDim);
                double logitP = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                double calibP = Sigmoid(foldPlattA * logitP + foldPlattB);
                preds[pi] = (calibP >= 0.5 ? 1 : -1, foldTest[pi].Direction > 0 ? 1 : -1);
            }
            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(preds);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBad);
        });

        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds        = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc); f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV); sharpeList.Add(r.Value.Sharpe);
            foldImportances.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            for (int fi = 0; fi < foldResults.Length; fi++)
            {
                if (foldResults[fi] is null) continue;
                var fr = foldResults[fi]!.Value;
                _logger.LogInformation(
                    "CV fold {Fold}: acc={Acc:P1} f1={F1:F3} sharpe={Sh:F2} bad={Bad}",
                    fi + 1, fr.Acc, fr.F1, fr.Sharpe, fr.IsBad);
            }
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "Equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        // H4: Sharpe trend gate
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model rejected.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // H14: Feature stability scores (CV = σ/μ of mean |weight| per feature)
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int fc = foldImportances.Count;
            for (int j = 0; j < F; j++)
            {
                double sumI = 0.0;
                for (int fi = 0; fi < fc; fi++) sumI += foldImportances[fi][j];
                double meanI = sumI / fc;
                double varI  = 0.0;
                for (int fi = 0; fi < fc; fi++) { double d = foldImportances[fi][j] - meanI; varI += d * d; }
                double stdI = fc > 1 ? Math.Sqrt(varI / (fc - 1)) : 0.0;
                featureStabilityScores[j] = meanI > 1e-10 ? stdI / meanI : 0.0;
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

    // ── Borderline-SMOTE oversampling ─────────────────────────────────────────
    //
    // Classifies each minority sample by its K nearest neighbours drawn from ALL
    // training samples:
    //   SAFE   (0 majority neighbours)              → skip (no augmentation needed)
    //   DANGER (1 ≤ majorityCount ≤ ⌈K/2⌉)         → oversample from these
    //   NOISE  (majorityCount > ⌈K/2⌉)             → skip (dominated by majority)
    //
    // Synthetics are generated by interpolating between a DANGER sample and one of
    // its minority-only K nearest neighbours, exactly as in classic SMOTE.
    // If no DANGER samples exist the method falls back to all minority samples.
    //
    // KNN searches use an O(n × K) insertion-sorted buffer (K is small, typically 5)
    // and are parallelised across minority samples.

    private static (List<TrainingSample> Balanced, int SyntheticCount, int SmoteSeed) ApplySmote(
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  F,
        CancellationToken    ct)
    {
        var pos   = trainSet.Where(s => s.Direction > 0).ToList();
        var neg   = trainSet.Where(s => s.Direction <= 0).ToList();
        double ratio = neg.Count > 0 ? (double)pos.Count / neg.Count : 1.0;

        if ((ratio >= 0.8 && ratio <= 1.2) || pos.Count == 0 || neg.Count == 0)
            return (new List<TrainingSample>(trainSet), 0, 0);

        var minority = ratio < 1.0 ? pos : neg;
        var majority = ratio >= 1.0 ? pos : neg;
        double targetRatio = hp.SmoteTargetRatio is > 0.0 and <= 1.0 ? hp.SmoteTargetRatio : 1.0;
        int targetMinorityCount = (int)(majority.Count * targetRatio);
        int deficit = Math.Max(0, targetMinorityCount - minority.Count);
        int kSmote   = Math.Max(1, Math.Min(hp.SmoteKNeighbors ?? 5, minority.Count - 1));

        if (minority.Count < 2 || deficit <= 0)
            return (new List<TrainingSample>(trainSet), 0, 0);

        // ── Step 1: classify minority samples using all-sample KNN (parallel) ──
        // For each minority[i], count how many of its K nearest (in trainSet) are majority.
        // O(|trainSet| × K) per minority sample; parallelised across i.
        var majorityNeighborCounts = new int[minority.Count];
        var popts = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, minority.Count, popts, i =>
        {
            // Insertion-sorted buffer of K best distances (K is small, typically 5)
            var buf = new (double D, bool IsMaj)[kSmote];
            for (int ki = 0; ki < kSmote; ki++) buf[ki] = (double.MaxValue, false);
            double threshold = double.MaxValue;

            for (int j = 0; j < trainSet.Count; j++)
            {
                if (ReferenceEquals(trainSet[j], minority[i])) continue;
                double d = EuclideanDistSq(minority[i].Features, trainSet[j].Features);
                if (d >= threshold) continue;

                // Insert at tail and bubble up to maintain ascending order
                buf[kSmote - 1] = (d, trainSet[j].Direction != minority[i].Direction);
                int ins = kSmote - 1;
                while (ins > 0 && buf[ins].D < buf[ins - 1].D)
                {
                    (buf[ins], buf[ins - 1]) = (buf[ins - 1], buf[ins]);
                    ins--;
                }
                threshold = buf[kSmote - 1].D;
            }

            int majCount = 0;
            for (int ki = 0; ki < kSmote; ki++)
                if (buf[ki].D < double.MaxValue && buf[ki].IsMaj) majCount++;
            majorityNeighborCounts[i] = majCount;
        });

        // Collect DANGER-zone indices; fall back to all minority when none found
        var borderline = new List<int>(minority.Count);
        int dangerThresh = (kSmote / 2) + 1;
        for (int i = 0; i < minority.Count; i++)
            if (majorityNeighborCounts[i] >= 1 && majorityNeighborCounts[i] <= dangerThresh)
                borderline.Add(i);
        if (borderline.Count == 0)
            borderline.AddRange(Enumerable.Range(0, minority.Count));

        // ── Step 2: minority-only KNN for DANGER samples (interpolation targets) ─
        // O(|minority|² × K) — parallelised across i.
        var knnLists = new int[minority.Count][];
        Parallel.For(0, minority.Count, popts, i =>
        {
            var buf = new (double D, int Idx)[kSmote];
            for (int ki = 0; ki < kSmote; ki++) buf[ki] = (double.MaxValue, -1);
            double threshold = double.MaxValue;

            for (int j = 0; j < minority.Count; j++)
            {
                if (j == i) continue;
                double d = EuclideanDistSq(minority[i].Features, minority[j].Features);
                if (d >= threshold) continue;

                buf[kSmote - 1] = (d, j);
                int ins = kSmote - 1;
                while (ins > 0 && buf[ins].D < buf[ins - 1].D)
                {
                    (buf[ins], buf[ins - 1]) = (buf[ins - 1], buf[ins]);
                    ins--;
                }
                threshold = buf[kSmote - 1].D;
            }
            knnLists[i] = [.. buf.Where(b => b.Idx >= 0).Select(b => b.Idx)];
        });

        // ── Step 3: generate synthetics from DANGER samples ───────────────────
        // Seed is derived from the training data so the same dataset always produces
        // the same synthetics (reproducible) while different datasets differ.
        int smoteSeed = hp.SmoteSeed ?? HashCode.Combine(trainSet.Count, minority.Count, kSmote);
        var rng       = new Random(smoteSeed);
        var synthetic = new List<TrainingSample>(deficit);

        // ADASYN-weighted seed selection: harder samples (more majority neighbors) get more synthetics
        var borderlineCdf = new double[borderline.Count];
        double cdfSum = 0;
        for (int bi = 0; bi < borderline.Count; bi++)
        {
            cdfSum += Math.Max(1, majorityNeighborCounts[borderline[bi]]);
            borderlineCdf[bi] = cdfSum;
        }

        for (int s = 0; s < deficit; s++)
        {
            if (ct.IsCancellationRequested) break;

            // ADASYN: sample proportionally to majority neighbor count
            double target = rng.NextDouble() * cdfSum;
            int bi2 = Array.BinarySearch(borderlineCdf, target);
            if (bi2 < 0) bi2 = ~bi2;
            bi2 = Math.Min(bi2, borderline.Count - 1);
            int seedIdx = borderline[bi2];
            var        seedSample = minority[seedIdx];
            var        neighbors  = knnLists[seedIdx];
            if (neighbors.Length == 0) continue;

            var        neighbor   = minority[neighbors[rng.Next(neighbors.Length)]];
            float[]    synth      = new float[F];
            double     t          = rng.NextDouble();

            for (int j = 0; j < F; j++)
                synth[j] = (float)(seedSample.Features[j] + t * (neighbor.Features[j] - seedSample.Features[j]));

            synthetic.Add(new TrainingSample(synth, seedSample.Direction, seedSample.Magnitude));
        }

        // SMOTE-ENN: remove synthetics whose 3-NN majority disagrees with their label
        if (synthetic.Count > 0 && hp.SmoteEnnEnabled)
        {
            var allSamples = new List<TrainingSample>(trainSet.Count + synthetic.Count);
            allSamples.AddRange(trainSet);
            allSamples.AddRange(synthetic);
            int kEnn = 3;
            var toRemove = new HashSet<int>();
            Parallel.For(0, synthetic.Count, popts, si =>
            {
                int idx = trainSet.Count + si;
                var sample = allSamples[idx];
                var buf = new (double D, int Dir)[kEnn];
                for (int ki = 0; ki < kEnn; ki++) buf[ki] = (double.MaxValue, 0);
                double thresh = double.MaxValue;
                for (int j = 0; j < allSamples.Count; j++)
                {
                    if (j == idx) continue;
                    double d = EuclideanDistSq(sample.Features, allSamples[j].Features);
                    if (d >= thresh) continue;
                    buf[kEnn - 1] = (d, allSamples[j].Direction);
                    int ins = kEnn - 1;
                    while (ins > 0 && buf[ins].D < buf[ins - 1].D)
                    {
                        (buf[ins], buf[ins - 1]) = (buf[ins - 1], buf[ins]);
                        ins--;
                    }
                    thresh = buf[kEnn - 1].D;
                }
                int disagree = 0;
                for (int ki = 0; ki < kEnn; ki++)
                    if (buf[ki].D < double.MaxValue && (buf[ki].Dir > 0) != (sample.Direction > 0))
                        disagree++;
                if (disagree > kEnn / 2)
                    lock (toRemove) toRemove.Add(si);
            });
            if (toRemove.Count > 0)
            {
                synthetic = [.. synthetic.Where((_, i) => !toRemove.Contains(i))];
            }
        }

        var balanced = new List<TrainingSample>(trainSet.Count + synthetic.Count);
        balanced.AddRange(trainSet);
        balanced.AddRange(synthetic);
        return (balanced, synthetic.Count, smoteSeed);
    }

    // ── Ensemble fit ──────────────────────────────────────────────────────────

    private EnsembleTrainResult FitEnsemble(
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  F,
        int                  K,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct,
        bool                 forceSequential = false,
        int                  originalCount   = 0)
    {
        int    n       = trainSet.Count;
        double lr0     = hp.LearningRate > 0  ? hp.LearningRate  : 0.01;
        double l2      = hp.L2Lambda     > 0  ? hp.L2Lambda      : 0.001;
        double l1      = hp.L1Lambda     > 0  ? hp.L1Lambda      : 0.0;
        double noise   = hp.NoiseSigma   > 0  ? hp.NoiseSigma    : 0.0;
        double maxGrad = hp.MaxGradNorm  > 0  ? hp.MaxGradNorm   : 0.0;
        int    epochs  = hp.MaxEpochs    > 0  ? hp.MaxEpochs     : 20;
        int    patience = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : 5;
        int    batchSize = Math.Max(1, hp.MiniBatchSize);
        int    hiddenDim = Math.Max(0, hp.MlpHiddenDim);
        bool   useMlp    = hiddenDim > 0;

        // L8: Polynomial learners
        int polyStart        = hp.PolyLearnerFraction > 0 ? (int)(K * (1.0 - hp.PolyLearnerFraction)) : K;
        int polyFeatureCount = F + PolyPairCount;
        int[] top5Indices    = GetTop5FeatureIndices(warmStart, F);

        // Feature subsampling
        double fsr     = hp.FeatureSampleRatio > 0 ? hp.FeatureSampleRatio : 0.0;
        bool useSubsets = fsr > 0 && fsr < 1.0;

        // M8: biased feature sampling from warm-start importance scores
        bool useBiasedSampling = warmStart?.FeatureImportanceScores?.Length == F && useSubsets;

        // Temporal weights (blended with density-ratio weights if provided)
        double[] temporalWeights = ComputeTemporalWeights(n, hp.TemporalDecayLambda);
        // densityWeights may be shorter than temporalWeights (pre-SMOTE vs post-SMOTE).
        // Blend only the overlapping prefix; synthetic samples keep temporal-only weights.
        if (densityWeights is { Length: > 0 } && densityWeights.Length >= 1)
        {
            var blended = new double[n];
            double wSum = 0;
            for (int i = 0; i < n; i++)
            {
                blended[i] = temporalWeights[i] * (i < densityWeights.Length ? densityWeights[i] : 1.0);
                wSum += blended[i];
            }
            if (wSum > 1e-15) for (int i = 0; i < n; i++) blended[i] /= wSum;
            temporalWeights = blended;
        }

        // Bootstrap/OOB only over original samples (not SMOTE synthetics)
        int origN = originalCount > 0 ? Math.Min(originalCount, n) : n;
        var posIdx = Enumerable.Range(0, origN).Where(i => trainSet[i].Direction > 0).ToArray();
        var negIdx = Enumerable.Range(0, origN).Where(i => trainSet[i].Direction <= 0).ToArray();

        if (posIdx.Length == 0 || negIdx.Length == 0)
            _logger.LogWarning("StratifiedBootstrap: {Class} class is empty among original samples — bootstrap will use uniform sampling.",
                posIdx.Length == 0 ? "positive" : "negative");

        // Result arrays
        var weights      = new double[K][];
        var biases       = new double[K];
        var oobMasks     = new bool[K][];
        int[][]? fsubs   = useSubsets ? new int[K][] : null;
        double[][]? mlpHW = useMlp ? new double[K][] : null;
        double[][]? mlpHB = useMlp ? new double[K][] : null;
        var swaCountPerLearner = new int[K];

        // Determine if learners are independent (can run in parallel).
        // Only NCL and DiversityLambda require sequential execution (they read prior learners' weights).
        // NoiseCorrectionThreshold only uses the current sample's p and yRaw — safe to parallelize.
        bool learnersIndependent =
            hp.NclLambda             <= 0.0 &&
            hp.DiversityLambda       <= 0.0;
        bool runParallel = !forceSequential && learnersIndependent;

        // Split off a small validation set for adaptive LR decay (M1)
        // Must come from ORIGINAL samples only (not synthetics) so LR decay tracks real data.
        // valSet indices are excluded from OOB to avoid feedback between LR decay and early stopping.
        int valSize  = Math.Max(20, origN / 10);
        int valStart = origN - valSize; // index within original samples
        var valSet   = trainSet[valStart..origN]; // only original samples, excludes synthetics
        var fitSet   = trainSet[..valStart];

        // ── Per-learner training closure ──────────────────────────────────────
        void TrainLearner(int k)
        {
            ct.ThrowIfCancellationRequested();

            bool isPoly    = hp.PolyLearnerFraction > 0 && k >= polyStart;
            int effF       = isPoly ? polyFeatureCount : F;
            var rng        = new Random(42 + k * 97 + 13);
            // 2-layer MLP: pack L1 (hiddenDim×fk) + L2 (hiddenDim×hiddenDim) into hW,
            // and L1 biases (hiddenDim) + L2 biases (hiddenDim) into hB.
            bool isDeep2   = useMlp && hp.MlpHiddenLayers >= 2;

            // Feature subset
            int[] subset;
            if (useSubsets)
                subset = isPoly ? GenerateFeatureSubset(effF, fsr, 42 + k * 97)
                       : useBiasedSampling ? GenerateBiasedFeatureSubset(F, fsr, warmStart!.FeatureImportanceScores, 42 + k * 97)
                       : GenerateFeatureSubset(F, fsr, 42 + k * 97);
            else
                subset = [.. Enumerable.Range(0, effF)];

            if (fsubs is not null) fsubs[k] = subset;
            int fk = subset.Length;

            // MLP output dim = hiddenDim (MLP), or fk (linear)
            int outDim = useMlp ? hiddenDim : fk;

            // Weight initialisation
            double[] w;
            double   b;
            double[]? hW = null;
            double[]? hB = null;

            if (useMlp)
            {
                // Packed layout: L1 weights (hiddenDim×fk), then L2 weights (hiddenDim×hiddenDim) if deep.
                int initHWSize = isDeep2 ? hiddenDim * fk + hiddenDim * hiddenDim : hiddenDim * fk;
                int initHBSize = isDeep2 ? hiddenDim * 2 : hiddenDim;
                hW = new double[initHWSize];
                hB = new double[initHBSize];
                double xavStd1 = Math.Sqrt(2.0 / (fk + hiddenDim));
                for (int i = 0; i < hiddenDim * fk; i++) hW[i] = SampleNormal(rng) * xavStd1;
                if (isDeep2)
                {
                    double xavStd2 = Math.Sqrt(2.0 / (hiddenDim + hiddenDim));
                    for (int i = hiddenDim * fk; i < initHWSize; i++) hW[i] = SampleNormal(rng) * xavStd2;
                }

                if (warmStart is not null && k < warmStart.Weights?.Length &&
                    warmStart.Weights[k].Length == outDim)
                {
                    w = [.. warmStart.Weights[k]];
                    b = k < warmStart.Biases?.Length ? warmStart.Biases[k] : 0.0;
                    // Size check: exact match required (handles 1-layer ↔ 2-layer transition gracefully)
                    if (warmStart.MlpHiddenWeights?[k]?.Length == hW.Length)
                        Array.Copy(warmStart.MlpHiddenWeights[k], hW, hW.Length);
                    if (warmStart.MlpHiddenBiases?[k]?.Length == hB.Length)
                        Array.Copy(warmStart.MlpHiddenBiases[k], hB, hB.Length);
                }
                else
                {
                    w = new double[outDim];
                    double outScale = Math.Sqrt(6.0 / (hiddenDim + 1));
                    for (int ji = 0; ji < outDim; ji++) w[ji] = (rng.NextDouble() * 2.0 - 1.0) * outScale;
                    b = 0.0;
                }
            }
            else
            {
                w = InitWeights(warmStart, k, fk, subset, F, rng);
                b = warmStart is not null && k < warmStart.Biases?.Length ? warmStart.Biases[k] : 0.0;
            }

            // Bootstrap over original samples + append all synthetic indices so SMOTE data is used
            int[] origBootstrap = StratifiedBootstrap(posIdx, negIdx, origN, temporalWeights, rng);
            int syntheticStart = origN;
            int syntheticEnd   = n;
            int syntheticN     = syntheticEnd - syntheticStart;
            var bootstrap      = new int[origBootstrap.Length + syntheticN];
            Array.Copy(origBootstrap, bootstrap, origBootstrap.Length);
            for (int si = 0; si < syntheticN; si++)
                bootstrap[origBootstrap.Length + si] = syntheticStart + si;
            var   inBag     = new HashSet<int>(origBootstrap);
            // OOB mask: original samples not in bag and not in valSet are OOB;
            // synthetics (index >= origN) and valSet samples (index >= valStart) are never OOB
            oobMasks[k] = [.. Enumerable.Range(0, n).Select(i => i < origN && i < valStart && !inBag.Contains(i))];
            bool hasOobSamples = oobMasks[k].Any(x => x);

            // Adam state — M19: ArrayPool for Adam moment arrays
            int hWSize = useMlp ? hW!.Length : 0;
            int hBSize = useMlp ? hB!.Length : 0;
            double[] m1   = ArrayPool<double>.Shared.Rent(outDim);
            double[] v1   = ArrayPool<double>.Shared.Rent(outDim);
            double[] hm1  = useMlp ? ArrayPool<double>.Shared.Rent(hWSize) : [];
            double[] hv1  = useMlp ? ArrayPool<double>.Shared.Rent(hWSize) : [];
            double[] hbm1 = useMlp ? ArrayPool<double>.Shared.Rent(hBSize) : [];
            double[] hbv1 = useMlp ? ArrayPool<double>.Shared.Rent(hBSize) : [];
            Array.Clear(m1, 0, outDim);
            Array.Clear(v1, 0, outDim);
            if (useMlp) { Array.Clear(hm1, 0, hWSize); Array.Clear(hv1, 0, hWSize); Array.Clear(hbm1, 0, hBSize); Array.Clear(hbv1, 0, hBSize); }
            double mb = 0, vb = 0;

            // C1: Running bias-correction products (avoid per-sample Math.Pow)
            double beta1t = 1.0, beta2t = 1.0;
            int    step   = 0;

            // Early stopping
            double   bestLoss = double.MaxValue;
            int      noImprove = 0;
            double[] bestW = (double[])w.Clone();
            double   bestB = b;
            double[]? bestHW = useMlp ? (double[])hW!.Clone() : null;
            double[]? bestHB = useMlp ? (double[])hB!.Clone() : null;

            // SWA accumulators (M17)
            double[] swaW  = new double[outDim];
            double   swaB  = 0.0;
            double[]? swaHW = useMlp ? new double[hWSize] : null;
            double[]? swaHB = useMlp ? new double[hBSize] : null;
            int swaCount   = 0;

            // M1: Adaptive LR decay state
            double adaptedLr0 = lr0;
            double valAccBest = 0.0;
            bool   lrDecayed  = false;

            // Cosine-annealing LR
            double GetLr(int ep) => adaptedLr0 * 0.5 * (1.0 + Math.Cos(Math.PI * ep / epochs));

            // Preallocate gradient arrays (reused per batch)
            double[] gw   = new double[outDim];
            double[]? ghW = useMlp ? new double[hWSize] : null;
            double[]? ghB = useMlp ? new double[hBSize] : null;

            // Preallocate MLP activation arrays (reused per sample)
            double[]? hL1PreBuf = useMlp ? new double[hiddenDim] : null;
            double[]? hL1ActBuf = useMlp ? new double[hiddenDim] : null;
            double[]? hL2PreBuf = isDeep2 ? new double[hiddenDim] : null;
            double[]? hL2ActBuf = isDeep2 ? new double[hiddenDim] : null;
            double[]? dL1Buf    = isDeep2 ? new double[hiddenDim] : null;
            double[] hFinalRef  = isDeep2 ? hL2ActBuf! : (useMlp ? hL1ActBuf! : []);

            try
            {
                for (int ep = 0; ep < epochs && !ct.IsCancellationRequested; ep++)
                {
                    double lr = GetLr(ep);

                    // Shuffle bootstrap
                    for (int i = bootstrap.Length - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        (bootstrap[i], bootstrap[j]) = (bootstrap[j], bootstrap[i]);
                    }

                    bool nanHit = false;

                    // L10: Mini-batch loop
                    for (int bStart = 0; bStart < bootstrap.Length && !nanHit; bStart += batchSize)
                    {
                        int actual = Math.Min(batchSize, bootstrap.Length - bStart);

                        // Clear gradient accumulators
                        Array.Clear(gw, 0, outDim);
                        double gb = 0;
                        if (useMlp) { Array.Clear(ghW!, 0, hWSize); Array.Clear(ghB!, 0, hBSize); }

                        for (int bi = 0; bi < actual; bi++)
                        {
                            int idx = bootstrap[bStart + bi];
                            float[] xFull = trainSet[idx].Features;

                            // L8: Augment features for poly learners
                            if (isPoly) xFull = BuildPolyAugmentedFeatures(xFull, top5Indices, F);

                            // L6: AtrLabelSensitivity — soft labels (compute before Mixup so both samples get labels)
                            double yRaw;
                            if (hp.AtrLabelSensitivity > 0.0)
                            {
                                double signedMag = trainSet[idx].Magnitude * (trainSet[idx].Direction > 0 ? 1.0 : -1.0);
                                yRaw = Sigmoid(signedMag / hp.AtrLabelSensitivity);
                            }
                            else
                            {
                                yRaw = trainSet[idx].Direction > 0 ? 1.0 : 0.0;
                            }

                            // L7: Mixup — interpolate both features AND labels (Zhang et al., 2018)
                            if (hp.MixupAlpha > 0.0 && rng.NextDouble() < 0.5)
                            {
                                int partnerIdx = bootstrap[rng.Next(bootstrap.Length)];
                                float[] xPartner = trainSet[partnerIdx].Features;
                                if (isPoly) xPartner = BuildPolyAugmentedFeatures(xPartner, top5Indices, F);
                                double lam = SampleBeta(hp.MixupAlpha, rng);
                                var xMixed = new float[xFull.Length];
                                for (int j = 0; j < xFull.Length; j++)
                                    xMixed[j] = (float)(lam * xFull[j] + (1 - lam) * xPartner[j]);
                                xFull = xMixed;

                                // Interpolate label: yRaw_mix = λ·yRaw_primary + (1-λ)·yRaw_partner
                                double yRawPartner = hp.AtrLabelSensitivity > 0.0
                                    ? Sigmoid(trainSet[partnerIdx].Magnitude
                                        * (trainSet[partnerIdx].Direction > 0 ? 1.0 : -1.0)
                                        / hp.AtrLabelSensitivity)
                                    : (trainSet[partnerIdx].Direction > 0 ? 1.0 : 0.0);
                                yRaw = lam * yRaw + (1 - lam) * yRawPartner;
                            }

                            double y = labelSmoothing > 0
                                ? yRaw * (1.0 - labelSmoothing) + 0.5 * labelSmoothing
                                : yRaw;

                            // Forward pass (reuse preallocated activation buffers)
                            double logit;

                            if (useMlp)
                            {
                                // Layer 1: input → hiddenDim
                                for (int hj = 0; hj < hiddenDim; hj++)
                                {
                                    double act = hB![hj];
                                    for (int ji = 0; ji < fk; ji++)
                                        act += hW![hj * fk + ji] * xFull[subset[ji]];
                                    hL1PreBuf![hj] = act;
                                    hL1ActBuf![hj] = Math.Max(0, act); // ReLU
                                }
                                if (isDeep2)
                                {
                                    // Layer 2: hiddenDim → hiddenDim
                                    int l2WOff = hiddenDim * fk;
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        double act = hB![hiddenDim + hj];
                                        for (int ji = 0; ji < hiddenDim; ji++)
                                            act += hW![l2WOff + hj * hiddenDim + ji] * hL1ActBuf![ji];
                                        hL2PreBuf![hj] = act;
                                        hL2ActBuf![hj] = Math.Max(0, act); // ReLU
                                    }
                                }
                                logit = b;
                                for (int hj = 0; hj < hiddenDim; hj++) logit += w[hj] * hFinalRef[hj];
                            }
                            else
                            {
                                logit = b;
                                for (int ji = 0; ji < fk; ji++)
                                {
                                    double xj = xFull[subset[ji]];
                                    if (noise > 0) xj += noise * SampleNormal(rng);
                                    logit += w[ji] * xj;
                                }
                            }

                            double p   = Sigmoid(logit);
                            double err = p - y;

                            err = ApplyLossModifiers(err, p, y, yRaw, hp, xFull, weights, biases, k, F,
                                fsubs, mlpHW, mlpHB, hiddenDim);

                            // Accumulate bias gradient
                            gb += err;

                            if (useMlp)
                            {
                                if (isDeep2)
                                {
                                    int l2WOff = hiddenDim * fk;
                                    Array.Clear(dL1Buf!, 0, hiddenDim);
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        gw[hj] += err * hL2ActBuf![hj];
                                        double reluGate2 = hL2PreBuf![hj] > 0 ? 1.0 : 0.0;
                                        double dOut = err * w[hj] * reluGate2;
                                        ghB![hiddenDim + hj] += dOut;
                                        for (int ji = 0; ji < hiddenDim; ji++)
                                        {
                                            int wIdx = l2WOff + hj * hiddenDim + ji;
                                            ghW![wIdx] += dOut * hL1ActBuf![ji];
                                            dL1Buf![ji] += dOut * hW![wIdx];
                                        }
                                    }
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        double reluGate1 = hL1PreBuf![hj] > 0 ? 1.0 : 0.0;
                                        double dAct1     = dL1Buf![hj] * reluGate1;
                                        ghB![hj] += dAct1;
                                        for (int ji2 = 0; ji2 < fk; ji2++)
                                            ghW![hj * fk + ji2] += dAct1 * xFull[subset[ji2]];
                                    }
                                }
                                else
                                {
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        gw[hj] += err * hL1ActBuf![hj];
                                        double reluGate = hL1PreBuf![hj] > 0 ? 1.0 : 0.0;
                                        double dH = err * w[hj] * reluGate;
                                        ghB![hj] += dH;
                                        for (int ji2 = 0; ji2 < fk; ji2++)
                                            ghW![hj * fk + ji2] += dH * xFull[subset[ji2]];
                                    }
                                }
                            }
                            else
                            {
                                for (int ji = 0; ji < fk; ji++)
                                    gw[ji] += err * xFull[subset[ji]];
                            }
                        } // end per-sample accumulation

                        // Average gradients over batch
                        double invBatch = 1.0 / actual;
                        gb *= invBatch;
                        for (int ji = 0; ji < outDim; ji++) gw[ji] *= invBatch;
                        if (useMlp)
                        {
                            for (int i = 0; i < hWSize; i++) ghW![i] *= invBatch;
                            for (int hj = 0; hj < hBSize; hj++) ghB![hj] *= invBatch;
                        }

                        // Gradient clipping
                        if (maxGrad > 0)
                        {
                            double gnorm = gb * gb;
                            for (int ji = 0; ji < outDim; ji++) gnorm += gw[ji] * gw[ji];
                            if (useMlp)
                            {
                                for (int i = 0; i < hWSize; i++) gnorm += ghW![i] * ghW![i];
                                for (int hj = 0; hj < hBSize; hj++) gnorm += ghB![hj] * ghB![hj];
                            }
                            gnorm = Math.Sqrt(gnorm);
                            if (gnorm > maxGrad)
                            {
                                double scale = maxGrad / gnorm;
                                gb *= scale;
                                for (int ji = 0; ji < outDim; ji++) gw[ji] *= scale;
                                if (useMlp)
                                {
                                    for (int i = 0; i < hWSize; i++) ghW![i] *= scale;
                                    for (int hj = 0; hj < hBSize; hj++) ghB![hj] *= scale;
                                }
                            }
                        }

                        // C1: Running Adam bias-correction products (one step per batch)
                        step++;
                        beta1t *= AdamBeta1;
                        beta2t *= AdamBeta2;

                        // Adam update: bias
                        mb = AdamBeta1 * mb + (1 - AdamBeta1) * gb;
                        vb = AdamBeta2 * vb + (1 - AdamBeta2) * gb * gb;
                        b -= lr * (mb / (1 - beta1t)) / (Math.Sqrt(vb / (1 - beta2t)) + AdamEpsilon);

                        // Adam update: output weights
                        for (int ji = 0; ji < outDim; ji++)
                        {
                            m1[ji] = AdamBeta1 * m1[ji] + (1 - AdamBeta1) * gw[ji];
                            v1[ji] = AdamBeta2 * v1[ji] + (1 - AdamBeta2) * gw[ji] * gw[ji];
                            w[ji] -= lr * (m1[ji] / (1 - beta1t)) / (Math.Sqrt(v1[ji] / (1 - beta2t)) + AdamEpsilon);

                            // AdamW: decoupled weight decay
                            if (l2 > 0) w[ji] *= (1.0 - lr * l2);

                            // L1 proximal soft-thresholding
                            if (l1 > 0)
                                w[ji] = Math.Sign(w[ji]) * Math.Max(0, Math.Abs(w[ji]) - l1 * lr);

                            // M2: Weight magnitude clipping
                            if (hp.MaxWeightMagnitude > 0)
                                w[ji] = Math.Clamp(w[ji], -hp.MaxWeightMagnitude, hp.MaxWeightMagnitude);
                        }

                        // Adam update: MLP hidden layers
                        if (useMlp)
                        {
                            for (int i = 0; i < hWSize; i++)
                            {
                                hm1[i] = AdamBeta1 * hm1[i] + (1 - AdamBeta1) * ghW![i];
                                hv1[i] = AdamBeta2 * hv1[i] + (1 - AdamBeta2) * ghW![i] * ghW![i];
                                hW![i] -= lr * (hm1[i] / (1 - beta1t)) / (Math.Sqrt(hv1[i] / (1 - beta2t)) + AdamEpsilon);
                                if (l2 > 0) hW![i] *= (1.0 - lr * l2);
                                if (hp.MaxWeightMagnitude > 0)
                                    hW![i] = Math.Clamp(hW![i], -hp.MaxWeightMagnitude, hp.MaxWeightMagnitude);
                            }
                            for (int hj = 0; hj < hBSize; hj++)
                            {
                                hbm1[hj] = AdamBeta1 * hbm1[hj] + (1 - AdamBeta1) * ghB![hj];
                                hbv1[hj] = AdamBeta2 * hbv1[hj] + (1 - AdamBeta2) * ghB![hj] * ghB![hj];
                                hB![hj] -= lr * (hbm1[hj] / (1 - beta1t)) / (Math.Sqrt(hbv1[hj] / (1 - beta2t)) + AdamEpsilon);
                            }
                        }

                        // C2: Intra-epoch NaN/Inf guard — immediate rollback
                        bool hasNaN = !double.IsFinite(b);
                        if (!hasNaN)
                            for (int ji = 0; ji < outDim; ji++)
                                if (!double.IsFinite(w[ji])) { hasNaN = true; break; }

                        if (hasNaN)
                        {
                            w = (double[])bestW.Clone(); b = bestB;
                            if (useMlp && bestHW is not null) { Array.Copy(bestHW, hW!, hW!.Length); Array.Copy(bestHB!, hB!, hB!.Length); }
                            nanHit = true;
                        }
                    } // end batch loop

                    if (nanHit) break;

                    // M17: SWA weight accumulation
                    if (hp.SwaStartEpoch > 0 && ep >= hp.SwaStartEpoch &&
                        hp.SwaFrequency > 0 && (ep - hp.SwaStartEpoch) % hp.SwaFrequency == 0)
                    {
                        swaCount++;
                        for (int ji = 0; ji < outDim; ji++)
                            swaW[ji] += (w[ji] - swaW[ji]) / swaCount;
                        swaB += (b - swaB) / swaCount;
                        if (useMlp && swaHW is not null)
                        {
                            for (int i = 0; i < hWSize; i++) swaHW![i] += (hW![i] - swaHW![i]) / swaCount;
                            for (int hj = 0; hj < hBSize; hj++) swaHB![hj] += (hB![hj] - swaHB![hj]) / swaCount;
                        }
                    }

                    // Per-learner early stopping via OOB cross-entropy loss
                    if (patience > 0 && hasOobSamples)
                    {
                        double oobLoss = ComputeOobLoss(fitSet, oobMasks[k], w, b, subset, fk,
                            labelSmoothing, hW, hB, hiddenDim);
                        if (oobLoss < bestLoss - 1e-5)
                        {
                            bestLoss = oobLoss; noImprove = 0;
                            bestW = (double[])w.Clone(); bestB = b;
                            if (useMlp) { bestHW = (double[])hW!.Clone(); bestHB = (double[])hB!.Clone(); }
                        }
                        else if (++noImprove >= patience) break;
                    }

                    // M1: Adaptive LR decay — trigger once if rolling val acc drops > 5%
                    if (!lrDecayed && hp.AdaptiveLrDecayFactor > 0.0 && valSet.Count > 0 && ep > 0 && ep % 5 == 0)
                    {
                        double valAcc = ComputeValAccuracy(valSet, w, b, subset, fk, hW, hB, hiddenDim);
                        if (valAccBest == 0.0) valAccBest = valAcc;
                        else if (valAcc < valAccBest - 0.05)
                        {
                            adaptedLr0 *= hp.AdaptiveLrDecayFactor;
                            lrDecayed   = true;
                        }
                        else
                            valAccBest = Math.Max(valAccBest, valAcc);
                    }
                } // end epoch loop

                // Apply SWA weights if accumulated, but validate against early-stopped best
                if (swaCount > 0)
                {
                    Array.Copy(swaW, w, outDim);
                    b = swaB;
                    if (useMlp && swaHW is not null) { Array.Copy(swaHW, hW!, hW!.Length); Array.Copy(swaHB!, hB!, hB!.Length); }

                    // If early stopping tracked a best loss, verify SWA didn't degrade
                    if (patience > 0 && bestLoss < double.MaxValue && hasOobSamples)
                    {
                        double swaLoss = ComputeOobLoss(fitSet, oobMasks[k], w, b, subset, fk,
                            labelSmoothing, hW, hB, hiddenDim);
                        if (swaLoss > bestLoss * 1.1) // SWA is >10% worse — fall back
                        {
                            w = bestW; b = bestB;
                            if (useMlp && bestHW is not null) { Array.Copy(bestHW, hW!, hW!.Length); Array.Copy(bestHB!, hB!, hB!.Length); }
                            swaCount = 0; // mark as not used
                        }
                    }
                }
                else if (patience > 0)
                {
                    w = bestW; b = bestB;
                    if (useMlp && bestHW is not null) { Array.Copy(bestHW, hW!, hW!.Length); Array.Copy(bestHB!, hB!, hB!.Length); }
                }

                // Expand linear weights to full F (no expansion for MLP — output weights stay as [hiddenDim])
                if (!useMlp && useSubsets)
                {
                    var fullW = new double[F];
                    for (int ji = 0; ji < fk; ji++) fullW[subset[ji]] = w[ji];
                    weights[k] = fullW;
                }
                else
                {
                    weights[k] = w;
                }

                biases[k] = b;
                if (useMlp) { mlpHW![k] = hW!; mlpHB![k] = hB!; }
                swaCountPerLearner[k] = swaCount;
            }
            finally
            {
                ArrayPool<double>.Shared.Return(m1);
                ArrayPool<double>.Shared.Return(v1);
                if (useMlp)
                {
                    ArrayPool<double>.Shared.Return(hm1);
                    ArrayPool<double>.Shared.Return(hv1);
                    ArrayPool<double>.Shared.Return(hbm1);
                    ArrayPool<double>.Shared.Return(hbv1);
                }
            }
        } // end TrainLearner

        // ── Run learner training ──────────────────────────────────────────────
        if (runParallel)
        {
            Parallel.For(0, K, new ParallelOptions { CancellationToken = ct }, k =>
            {
                try { TrainLearner(k); }
                catch (OperationCanceledException) { throw; }
            });
        }
        else
        {
            for (int k = 0; k < K; k++)
            {
                ct.ThrowIfCancellationRequested();
                TrainLearner(k);
            }
        }

        // M13: MaxLearnerCorrelation enforcement — re-init highly correlated pairs
        if (hp.MaxLearnerCorrelation is > 0.0 and < 1.0)
        {
            // For MLP learners, use prediction-based correlation on valSet
            double[][]? predCache = null;
            if (useMlp && valSet.Count > 0)
            {
                predCache = new double[K][];
                for (int ki = 0; ki < K; ki++)
                {
                    predCache[ki] = new double[valSet.Count];
                    for (int vi = 0; vi < valSet.Count; vi++)
                        predCache[ki][vi] = SingleLearnerProb(valSet[vi].Features, weights[ki], biases[ki],
                            fsubs?[ki], F, mlpHW?[ki], mlpHB?[ki], hiddenDim);
                }
            }

            for (int i = 0; i < K - 1; i++)
            for (int j = i + 1; j < K; j++)
            {
                double rho = predCache is not null
                    ? PearsonCorrelation(predCache[i], predCache[j], valSet.Count)
                    : PearsonCorrelation(weights[i], weights[j], F);
                if (rho > hp.MaxLearnerCorrelation)
                {
                    // Zero out the later learner — effectively prunes it from the ensemble
                    // rather than injecting untrained random noise
                    Array.Clear(weights[j], 0, weights[j].Length);
                    biases[j] = 0.0;
                    if (mlpHW?[j] is not null) Array.Clear(mlpHW[j], 0, mlpHW[j].Length);
                    if (mlpHB?[j] is not null) Array.Clear(mlpHB[j], 0, mlpHB[j].Length);
                }
            }
        }

        int totalSwaCount = swaCountPerLearner.Length > 0 ? swaCountPerLearner.Max() : 0;
        return new EnsembleTrainResult(weights, biases, fsubs, polyStart, oobMasks, mlpHW, mlpHB, totalSwaCount);
    }

    private static double ApplyLossModifiers(
        double err, double p, double y, double yRaw,
        TrainingHyperparams hp,
        float[] xFull, double[][] weights, double[] biases, int k, int F,
        int[][]? featureSubsets, double[][]? mlpHW, double[][]? mlpHB, int hiddenDim)
    {
        // L4: FpCostWeight asymmetric BCE gradient
        if (hp.FpCostWeight > 0.0 && Math.Abs(hp.FpCostWeight - 0.5) > 1e-6)
        {
            double yBin = yRaw > 0.5 ? 1.0 : 0.0;
            double fpW  = yBin > 0.5 ? hp.FpCostWeight : (1.0 - hp.FpCostWeight);
            err *= fpW / 0.5;
        }

        // L3: SCE — add reverse cross-entropy gradient
        if (hp.UseSymmetricCE && hp.SymmetricCeAlpha > 0.0)
        {
            double rceGrad = (-Math.Log(Math.Max(y, 1e-7)) + Math.Log(Math.Max(1 - y, 1e-7)))
                * p * (1 - p);
            err += hp.SymmetricCeAlpha * rceGrad;
        }

        // L1: NCL — gradient penalty using prior learners' avg prediction
        if (hp.NclLambda > 0.0 && k > 0)
        {
            double avgP = ComputeAvgPriorLearners(xFull, weights, biases, k, F, featureSubsets,
                mlpHW, mlpHB, hiddenDim);
            err += hp.NclLambda * p * (1 - p) * (2 * p - avgP);
        }

        // L2: DiversityLambda — push away from ensemble mean
        if (hp.DiversityLambda > 0.0 && k > 0)
        {
            double avgP = ComputeAvgPriorLearners(xFull, weights, biases, k, F, featureSubsets,
                mlpHW, mlpHB, hiddenDim);
            err += -hp.DiversityLambda * 2.0 * (p - avgP) * p * (1 - p);
        }

        // L5: Noise correction — downweight likely mislabelled samples
        if (hp.NoiseCorrectionThreshold > 0.0)
        {
            double yBin = yRaw > 0.5 ? 1.0 : 0.0;
            double noiseW = 1.0;
            if (yBin == 1.0 && p < hp.NoiseCorrectionThreshold)
                noiseW = p;
            else if (yBin == 0.0 && (1 - p) < hp.NoiseCorrectionThreshold)
                noiseW = 1 - p;
            err *= noiseW;
        }

        return err;
    }

    // ── M6: OOB-contribution pruning ─────────────────────────────────────────

    private static int PruneByOobContribution(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0,
        int                  K      = 0)
    {
        if (K <= 0) K = weights.Length;

        // Baseline accuracy
        double baselineAcc = 0;
        int baseCorrect = 0;
        foreach (var s in trainSet)
        {
            double p = EnsembleProb(s.Features, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
            if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        baselineAcc = (double)baseCorrect / Math.Max(1, trainSet.Count);

        int pruned = 0;
        for (int k = 0; k < K; k++)
        {
            if (weights[k].All(w => w == 0.0) && biases[k] == 0.0) continue;

            // Temporarily zero learner k
            var savedW = weights[k];
            var savedB = biases[k];
            weights[k] = new double[savedW.Length];
            biases[k]  = 0.0;

            int correct = 0;
            foreach (var s in trainSet)
            {
                double p = EnsembleProb(s.Features, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
                if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
            }
            double accWithout = (double)correct / Math.Max(1, trainSet.Count);

            if (accWithout >= baselineAcc)
            {
                // Removing learner k improved or maintained accuracy — keep zeroed
                pruned++;
                baselineAcc = accWithout;
            }
            else
            {
                // Restore
                weights[k] = savedW;
                biases[k]  = savedB;
            }
        }

        return pruned;
    }

    // ── GES (greedy ensemble selection) ──────────────────────────────────────

    private static double[] RunGreedyEnsembleSelection(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        int K = weights.Length;
        int N = calSet.Count;
        var useCounts = new int[K];

        // Cache per-learner predictions to avoid redundant computation
        var cache = new double[K][];
        var labels = new int[N];
        for (int k = 0; k < K; k++)
        {
            cache[k] = new double[N];
            for (int i = 0; i < N; i++)
                cache[k][i] = SingleLearnerProb(calSet[i].Features, weights[k], biases[k],
                    featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
        }
        for (int i = 0; i < N; i++)
            labels[i] = calSet[i].Direction > 0 ? 1 : 0;

        var selected = new List<int>();
        var runningSum = new double[N];

        for (int r = 0; r < GesRounds; r++)
        {
            double bestAcc = -1;
            int    bestK   = 0;
            int    count   = selected.Count + 1;

            for (int k = 0; k < K; k++)
            {
                int correct = 0;
                for (int i = 0; i < N; i++)
                {
                    double avgP = (runningSum[i] + cache[k][i]) / count;
                    if ((avgP >= 0.5 ? 1 : 0) == labels[i]) correct++;
                }
                double acc = (double)correct / N;
                if (acc > bestAcc) { bestAcc = acc; bestK = k; }
            }

            selected.Add(bestK);
            useCounts[bestK]++;
            for (int i = 0; i < N; i++)
                runningSum[i] += cache[bestK][i];
        }

        double total = useCounts.Sum();
        if (total <= 0) return [];
        return useCounts.Select(c => c / total).ToArray();
    }



    // ── OOB cross-entropy loss (for per-learner early stopping) ───────────────

    private static double ComputeOobLoss(
        List<TrainingSample> trainSet,
        bool[]               oobMask,
        double[]             w,
        double               b,
        int[]                subset,
        int                  fk,
        double               labelSmoothing,
        double[]?            hW    = null,
        double[]?            hB    = null,
        int                  hidDim = 0)
    {
        double lossSum = 0; int cnt = 0;
        for (int i = 0; i < Math.Min(trainSet.Count, oobMask.Length); i++)
        {
            if (!oobMask[i]) continue;
            float[] x    = trainSet[i].Features;
            double  yRaw = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            double  y    = labelSmoothing > 0 ? yRaw * (1 - labelSmoothing) + 0.5 * labelSmoothing : yRaw;
            double  p    = SingleLearnerProb(x, w, b, subset, fk, hW, hB, hidDim);
            lossSum += -(y * Math.Log(Math.Max(p, 1e-9)) + (1 - y) * Math.Log(Math.Max(1 - p, 1e-9)));
            cnt++;
        }
        return cnt > 0 ? lossSum / cnt : 0.0;
    }

    // ── M1: Validation accuracy for adaptive LR decay ─────────────────────────

    private static double ComputeValAccuracy(
        List<TrainingSample> valSet,
        double[]             w,
        double               b,
        int[]                subset,
        int                  fk,
        double[]?            hW    = null,
        double[]?            hB    = null,
        int                  hidDim = 0)
    {
        if (valSet.Count == 0) return 0.0;
        int correct = 0;
        foreach (var s in valSet)
        {
            double p = SingleLearnerProb(s.Features, w, b, subset, fk, hW, hB, hidDim);
            if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
        }
        return (double)correct / valSet.Count;
    }

    // ── NCL: avg prediction from prior learners (sequential mode) ────────────

    private static double ComputeAvgPriorLearners(
        float[]     x,
        double[][]  weights,
        double[]    biases,
        int         k,
        int         F,
        int[][]?    featureSubsets,
        double[][]? mlpHW,
        double[][]? mlpHB,
        int         hidDim)
    {
        if (k == 0) return 0.5;
        double sum = 0;
        for (int ki = 0; ki < k; ki++)
        {
            if (weights[ki] is null) continue;
            sum += SingleLearnerProb(x, weights[ki], biases[ki], featureSubsets?[ki], F,
                mlpHW?[ki], mlpHB?[ki], hidDim);
        }
        return sum / k;
    }

    // ── Stratified temporally-weighted bootstrap ──────────────────────────────

    private readonly struct AliasTable
    {
        private readonly double[] _prob;
        private readonly int[] _alias;
        private readonly int _n;

        public AliasTable(int[] indices, double[] weights)
        {
            _n = indices.Length;
            _prob = new double[_n];
            _alias = new int[_n];
            if (_n == 0) return;

            double sum = 0;
            foreach (int i in indices) sum += weights[i];
            if (sum <= 0) { Array.Fill(_prob, 1.0); return; }

            double avg = sum / _n;
            var small = new Stack<int>(_n);
            var large = new Stack<int>(_n);
            var scaled = new double[_n];
            for (int i = 0; i < _n; i++)
            {
                scaled[i] = weights[indices[i]] / avg;
                if (scaled[i] < 1.0) small.Push(i); else large.Push(i);
            }
            while (small.Count > 0 && large.Count > 0)
            {
                int s = small.Pop(), l = large.Pop();
                _prob[s] = scaled[s];
                _alias[s] = l;
                scaled[l] -= (1.0 - scaled[s]);
                if (scaled[l] < 1.0) small.Push(l); else large.Push(l);
            }
            while (large.Count > 0) _prob[large.Pop()] = 1.0;
            while (small.Count > 0) _prob[small.Pop()] = 1.0;
        }

        public int Sample(int[] indices, Random rng)
        {
            if (_n == 0) return 0;
            int i = rng.Next(_n);
            return rng.NextDouble() < _prob[i] ? indices[i] : indices[_alias[i]];
        }
    }

    private static int[] StratifiedBootstrap(
        int[]    posIdx,
        int[]    negIdx,
        int      n,
        double[] temporalWeights,
        Random   rng)
    {
        int halfN     = n / 2;
        var bootstrap = new int[n];

        var posAlias = new AliasTable(posIdx, temporalWeights);
        var negAlias = new AliasTable(negIdx, temporalWeights);

        for (int i = 0; i < halfN; i++)
            bootstrap[i] = posIdx.Length > 0 ? posAlias.Sample(posIdx, rng) : rng.Next(n);
        for (int i = halfN; i < n; i++)
            bootstrap[i] = negIdx.Length > 0 ? negAlias.Sample(negIdx, rng) : rng.Next(n);
        return bootstrap;
    }

    // ── Warm-start weight initialisation ──────────────────────────────────────

    private static double[] InitWeights(
        ModelSnapshot? warmStart,
        int            k,
        int            fk,
        int[]          subset,
        int            F,
        Random         rng)
    {
        var w = new double[fk];
        if (warmStart?.Weights is { Length: > 0 } ws && k < ws.Length && ws[k].Length == F)
        {
            for (int ji = 0; ji < fk; ji++) w[ji] = ws[k][subset[ji]];
        }
        else
        {
            double scale = Math.Sqrt(6.0 / (fk + 1));
            for (int ji = 0; ji < fk; ji++) w[ji] = (rng.NextDouble() * 2.0 - 1.0) * scale;
        }
        return w;
    }
}
