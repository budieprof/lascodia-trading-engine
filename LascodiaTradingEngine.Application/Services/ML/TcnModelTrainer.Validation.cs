using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ── Item 17: Combinatorial Purged Cross-Validation (CPCV) ────────────────

    /// <summary>
    /// Runs Combinatorial Purged Cross-Validation (CPCV) with N groups and p test groups.
    /// Generates C(N,p) unique (train, test) splits with purging and embargo.
    /// Returns aggregate metrics across all splits.
    /// More expensive than expanding-window CV but provides unbiased Sharpe estimates.
    /// </summary>
    internal (WalkForwardResult Result, bool EquityCurveGateFailed) RunCpcv(
        List<TrainingSample> samples,
        TrainingHyperparams hp,
        int filters, int numBlocks, int[] dilations,
        bool useLayerNorm, bool useAttentionPool, TcnActivation activation,
        int attentionHeads,
        CancellationToken ct,
        int nGroups = 6, int pTestGroups = 2)
    {
        int embargo = hp.EmbargoBarCount;
        int groupSize = samples.Count / nGroups;
        if (groupSize < 30)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        // Generate all C(N,p) combinations
        var combinations = GenerateCombinations(nGroups, pTestGroups);

        var accList = new List<double>();
        var f1List = new List<double>();
        var evList = new List<double>();
        var sharpeList = new List<double>();
        int badFolds = 0;
        var plattParams = new List<(double A, double B)>();
        var foldMetrics = new List<WalkForwardFoldMetric>();
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);

        var cvHp = hp with
        {
            MaxEpochs = Math.Max(30, hp.MaxEpochs / 3),
            EarlyStoppingPatience = Math.Max(5, hp.EarlyStoppingPatience / 2),
        };

        int cvParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
        var results = new (double Acc, double F1, double EV, double Sharpe, bool IsBad, double PlattA, double PlattB, double MaxDD)?[combinations.Count];

        Parallel.For(0, combinations.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = cvParallelism }, ci =>
        {
            var testGroups = combinations[ci];
            var testIndices = new HashSet<int>();
            foreach (int g in testGroups)
                for (int i = g * groupSize; i < Math.Min((g + 1) * groupSize, samples.Count); i++)
                    testIndices.Add(i);

            // Purge: remove training samples near test boundaries
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            var purgedIndices = new HashSet<int>(testIndices);
            foreach (int ti in testIndices)
            {
                for (int j = 1; j <= embargo + purgeExtra; j++)
                {
                    purgedIndices.Add(ti - j);
                    purgedIndices.Add(ti + j);
                }
            }

            var foldTrain = new List<TrainingSample>();
            var foldTest = new List<TrainingSample>();
            for (int i = 0; i < samples.Count; i++)
            {
                if (testIndices.Contains(i)) foldTest.Add(samples[i]);
                else if (!purgedIndices.Contains(i)) foldTrain.Add(samples[i]);
            }

            if (foldTrain.Count < hp.MinSamples || foldTest.Count < 20) return;

            var (foldTrainFit, foldCal) = CreateFoldTrainCalibrationSplit(foldTrain, embargo);
            var tcn = FitTcnModel(foldTrainFit, cvHp, null,
                filters, numBlocks, dilations, useLayerNorm, useAttentionPool, activation, attentionHeads, null, ct);

            TcnCalibrationArtifacts foldCalibration = CreateIdentityCalibrationArtifacts();
            if (foldCal.Count >= 10)
            {
                var foldCalRawProbs = PrecomputeRawProbs(foldCal, tcn, filters, useAttentionPool);
                foldCalibration = FitCalibrationArtifacts(
                    foldCal,
                    foldCalRawProbs,
                    cvHp,
                    conformalAlpha,
                    DefaultConditionalRoutingThreshold);
            }

            var m = Evaluate(foldTest, tcn, foldCalibration, filters, useAttentionPool);

            bool isBad = false;
            double maxDD = 0.0;
            if (hp.MaxFoldDrawdown < 1.0 || hp.MinFoldCurveSharpe > -99.0)
            {
                var preds = new (int Predicted, int Actual)[foldTest.Count];
                for (int pi = 0; pi < foldTest.Count; pi++)
                {
                    double rawP = TcnProb(foldTest[pi], tcn, filters, useAttentionPool);
                    double calibP = ApplyTcnCalibration(rawP, foldCalibration);
                    preds[pi] = (
                        calibP >= CalibrationThreshold(foldCalibration) ? 1 : -1,
                        foldTest[pi].Direction > 0 ? 1 : -1);
                }
                var (foldMaxDD, curveSharpe) = ComputeEquityCurveStats(preds);
                maxDD = foldMaxDD;
                if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
                if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;
            }

            results[ci] = (
                m.Accuracy,
                m.F1,
                m.ExpectedValue,
                m.SharpeRatio,
                isBad,
                foldCalibration.PlattA,
                foldCalibration.PlattB,
                maxDD);
        });

        foreach (var r in results)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc); f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV); sharpeList.Add(r.Value.Sharpe);
            plattParams.Add((r.Value.PlattA, r.Value.PlattB));
            foldMetrics.Add(new WalkForwardFoldMetric(r.Value.Acc, r.Value.F1, r.Value.EV, r.Value.Sharpe, r.Value.MaxDD));
            if (r.Value.IsBad) badFolds++;
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool failed = badFolds > (int)(accList.Count * badFoldThreshold);

        double avgAcc = accList.Average();
        double stdAcc = accList.Count > 1 ? Math.Sqrt(accList.Sum(a => (a - avgAcc) * (a - avgAcc)) / (accList.Count - 1)) : 0;
        var (recalibrationStabilityA, recalibrationStabilityB) = ComputeRecalibrationStability(plattParams);

        return (new WalkForwardResult(avgAcc, stdAcc, f1List.Average(), evList.Average(),
            sharpeList.Average(), accList.Count,
            FoldMetrics: foldMetrics.ToArray(),
            RecalibrationStabilityA: recalibrationStabilityA,
            RecalibrationStabilityB: recalibrationStabilityB), failed);
    }

    /// <summary>Generates all C(n, k) combinations of indices.</summary>
    internal static List<int[]> GenerateCombinations(int n, int k)
    {
        var result = new List<int[]>();
        var combo = new int[k];
        GenerateCombosRecursive(result, combo, 0, 0, n, k);
        return result;
    }

    private static void GenerateCombosRecursive(List<int[]> result, int[] combo, int start, int depth, int n, int k)
    {
        if (depth == k) { result.Add((int[])combo.Clone()); return; }
        for (int i = start; i <= n - k + depth; i++)
        {
            combo[depth] = i;
            GenerateCombosRecursive(result, combo, i + 1, depth + 1, n, k);
        }
    }

    // ── Item 18: Monte Carlo Permutation Test ────────────────────────────────

    /// <summary>
    /// Runs Monte Carlo permutation test: shuffle labels N times, re-run a simplified CV,
    /// compute p-value as fraction of shuffled accuracies >= observed accuracy.
    /// </summary>
    internal double RunMonteCarloPermutationTest(
        List<TrainingSample> samples, double observedAccuracy,
        TrainingHyperparams hp, int filters, int numBlocks, int[] dilations,
        bool useLayerNorm, bool useAttentionPool, TcnActivation activation,
        int attentionHeads, int numPermutations, CancellationToken ct)
    {
        if (numPermutations <= 0 || samples.Count < 50) return 1.0;

        int exceeded = 0;
        int cvParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
        var simpleHp = hp with
        {
            MaxEpochs = Math.Max(20, hp.MaxEpochs / 5),
            EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 3),
        };

        // Use a single 70/30 split for speed
        int trainEnd = (int)(samples.Count * 0.70);

        Parallel.For(0, numPermutations, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = cvParallelism }, pi =>
        {
            var rng = new Random(pi * 7919 + 42);

            // Shuffle labels
            var shuffled = new List<TrainingSample>(samples.Count);
            var directions = new int[samples.Count];
            for (int i = 0; i < samples.Count; i++) directions[i] = samples[i].Direction;
            // Fisher-Yates on directions
            for (int i = directions.Length - 1; i > 0; i--)
            { int j = rng.Next(i + 1); (directions[i], directions[j]) = (directions[j], directions[i]); }
            for (int i = 0; i < samples.Count; i++)
                shuffled.Add(samples[i] with { Direction = directions[i] });

            var train = shuffled[..trainEnd];
            var test = shuffled[trainEnd..];

            var tcn = FitTcnModel(train, simpleHp, null,
                filters, numBlocks, dilations, useLayerNorm, useAttentionPool, activation, attentionHeads, null, ct);
            var metrics = Evaluate(test, tcn, CreateIdentityCalibrationArtifacts(), filters, useAttentionPool);

            if (metrics.Accuracy >= observedAccuracy)
                Interlocked.Increment(ref exceeded);
        });

        return (double)(exceeded + 1) / (numPermutations + 1); // +1 for the observed run
    }

    // ── Item 19: Walk-Forward Fold Weighting ─────────────────────────────────

    /// <summary>
    /// Computes exponentially decayed fold weights so later folds (closer to deployment regime)
    /// contribute more to aggregate metrics. Returns weights normalised to sum to 1.
    /// </summary>
    internal static double[] ComputeFoldWeights(int foldCount, double decayLambda = 0.3)
    {
        var w = new double[foldCount];
        double sum = 0;
        for (int i = 0; i < foldCount; i++)
        {
            w[i] = Math.Exp(decayLambda * (i - foldCount + 1)); // later folds get higher weight
            sum += w[i];
        }
        for (int i = 0; i < foldCount; i++) w[i] /= sum;
        return w;
    }

    /// <summary>
    /// Computes weighted average of fold metrics using fold weights.
    /// </summary>
    internal static double WeightedAverage(List<double> values, double[] weights)
    {
        if (values.Count == 0 || weights.Length == 0) return 0;
        double sum = 0;
        int n = Math.Min(values.Count, weights.Length);
        for (int i = 0; i < n; i++) sum += values[i] * weights[i];
        return sum;
    }

    // ── Item 20: OOS Degradation Rate Tracking ──────────────────────────────

    /// <summary>
    /// Computes linear regression slopes for accuracy and F1 across fold indices.
    /// Negative slopes indicate performance degradation over time.
    /// </summary>
    internal static (double AccuracyDecay, double F1Decay) ComputeDegradationRates(
        List<double> foldAccuracies, List<double> foldF1Scores)
    {
        return (LinearSlope(foldAccuracies), LinearSlope(foldF1Scores));
    }

    private static double LinearSlope(List<double> values)
    {
        if (values.Count < 2) return 0;
        double xMean = (values.Count - 1) / 2.0;
        double yMean = values.Average();
        double num = 0, den = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double dx = i - xMean;
            num += dx * (values[i] - yMean);
            den += dx * dx;
        }
        return den > 1e-10 ? num / den : 0;
    }
}
