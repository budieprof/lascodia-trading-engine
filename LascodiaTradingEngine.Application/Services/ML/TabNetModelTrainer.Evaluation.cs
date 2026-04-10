using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using System.Security.Cryptography;
using System.Text;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    private readonly record struct TemporalCrossFitFold(
        int HoldoutStartIndex,
        int HoldoutCount,
        int[] TrainIndices,
        int[] HoldoutIndices,
        string Hash);

    private readonly record struct AdaptiveHeadCrossFitResult(
        bool Used,
        double[] MetaLabelWeights,
        double MetaLabelBias,
        double MetaLabelThreshold,
        double[] AbstentionWeights,
        double AbstentionBias,
        double AbstentionThreshold,
        int FoldCount,
        int[] FoldStartIndices,
        int[] FoldCounts,
        string[] FoldHashes);

    // ═══════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateTabNet(
        IReadOnlyList<TrainingSample> evalSet, TabNetWeights w,
        ModelSnapshot calibrationSnapshot, double[] magWeights, double magBias, int origF, double decisionThreshold = 0.5)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;
        double weightSum = 0, correctWeighted = 0;
        int n = evalSet.Count;
        double evSum = 0;
        var returns = new List<double>(n);

        // Batch forward pass with pooled buffers to reduce per-sample allocation overhead
        double[] batchProbs = TabNetCalibProbBatch(evalSet, w, calibrationSnapshot);

        for (int idx = 0; idx < n; idx++)
        {
            var s   = evalSet[idx];
            double p = batchProbs[idx];
            int yHat = p >= decisionThreshold ? 1 : 0;
            int y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);

            double sign = (yHat == y) ? 1.0 : -1.0;
            double ret  = sign * Math.Abs(s.Magnitude);
            evSum += ret;
            returns.Add(ret);

            if (magWeights.Length > 0)
            {
                double pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    pred += magWeights[j] * s.Features[j];
                magSse += (pred - s.Magnitude) * (pred - s.Magnitude);
            }

            double wt = 1.0 + (double)idx / n;
            weightSum += wt;
            if (yHat == y) correctWeighted += wt;
        }

        double accuracy  = n > 0 ? (double)correct / n : 0;
        double brier     = n > 0 ? brierSum / n : 1;
        double magRmse   = n > 0 && magSse > 0 ? Math.Sqrt(magSse / n) : 0;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = n > 0 ? evSum / n : 0;
        double wAcc      = weightSum > 0 ? correctWeighted / weightSum : accuracy;

        double avgRet = returns.Count > 0 ? returns.Average() : 0;
        double stdRet = returns.Count > 1 ? StdDev(returns, avgRet) : 0;
        // Per-bar Sharpe without annualization — timeframe is unknown
        double sharpe = stdRet > 1e-10 ? avgRet / stdRet : 0;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: wAcc, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    private static TabNetMetricSummary CreateMetricSummary(
        string splitName,
        EvalMetrics metrics,
        double ece,
        double threshold,
        int sampleCount)
    {
        return new TabNetMetricSummary
        {
            SplitName = splitName,
            SampleCount = sampleCount,
            Threshold = threshold,
            Accuracy = metrics.Accuracy,
            Precision = metrics.Precision,
            Recall = metrics.Recall,
            F1 = metrics.F1,
            ExpectedValue = metrics.ExpectedValue,
            BrierScore = metrics.BrierScore,
            WeightedAccuracy = metrics.WeightedAccuracy,
            SharpeRatio = metrics.SharpeRatio,
            Ece = ece,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PERMUTATION FEATURE IMPORTANCE
    // ═══════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        IReadOnlyList<TrainingSample> testSet, TabNetWeights w, ModelSnapshot calibrationSnapshot, double decisionThreshold, CancellationToken ct)
    {
        int n = testSet.Count, F = w.F;
        double baseline = 0;
        foreach (var s in testSet)
            if ((TabNetCalibProb(s.Features, w, calibrationSnapshot) >= decisionThreshold) == (s.Direction > 0)) baseline++;
        baseline /= n;
        var importance = new float[F];
        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(j * 13 + TrainerSeed);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = testSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }
            int correct = 0;
            var scratch = new float[testSet[0].Features.Length]; // thread-local scratch buffer
            for (int idx = 0; idx < n; idx++)
            {
                Array.Copy(testSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if ((TabNetCalibProb(scratch, w, calibrationSnapshot) >= decisionThreshold) == (testSet[idx].Direction > 0)) correct++;
                scratch[j] = testSet[idx].Features[j]; // restore for next iteration
            }
            importance[j] = (float)Math.Max(0, baseline - (double)correct / n);
        });
        float total = importance.Sum();
        if (total > 1e-6f) for (int j = 0; j < F; j++) importance[j] /= total;
        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double decisionThreshold, CancellationToken ct)
    {
        int n = calSet.Count, F = w.F;
        double baseAcc = 0;
        foreach (var s in calSet)
            if ((TabNetRawProb(s.Features, w) >= decisionThreshold) == (s.Direction > 0)) baseAcc++;
        baseAcc /= n;
        var importance = new double[F];
        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(j * 17 + TrainerSeed);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }
            int correct = 0;
            var scratch = new float[calSet[0].Features.Length]; // thread-local scratch buffer
            for (int idx = 0; idx < n; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if ((TabNetRawProb(scratch, w) >= decisionThreshold) == (calSet[idx].Direction > 0)) correct++;
                scratch[j] = calSet[idx].Features[j]; // restore
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FUSED ATTENTION ANALYSIS (single forward-pass loop)
    //  Computes mean attention, per-step attention, and attention entropy
    //  in one pass — eliminates 2× redundant forward passes.
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] MeanAttn, double[][] PerStepAttn, double[] Entropy) ComputeAttentionStats(
        IReadOnlyList<TrainingSample> samples, TabNetWeights w)
    {
        int F = w.F, nSteps = w.NSteps;
        int count = Math.Min(samples.Count, MeanAttentionSampleCap);

        var meanAttn = new double[F];
        var perStep  = new double[nSteps][];
        for (int s = 0; s < nSteps; s++) perStep[s] = new double[F];
        var entropy = new double[nSteps];

        var priorBuf = new double[F];
        var attnBuf  = new double[F];

        for (int i = 0; i < count; i++)
        {
            var fwd = ForwardPass(samples[i].Features, w, priorBuf, attnBuf, false, 0, null);

            for (int s = 0; s < nSteps; s++)
            {
                double h = 0;
                for (int j = 0; j < F && j < fwd.StepAttn[s].Length; j++)
                {
                    double a = fwd.StepAttn[s][j];
                    meanAttn[j]    += a / (nSteps * count);
                    perStep[s][j]  += a / count;
                    if (a > Eps) h -= a * Math.Log(a);
                }
                entropy[s] += h / count;
            }
        }

        return (meanAttn, perStep, entropy);
    }

    private static double[] ComputePerStepSparsity(double[][] perStepAttention)
    {
        var sparsity = new double[perStepAttention.Length];
        for (int s = 0; s < perStepAttention.Length; s++)
        {
            if (perStepAttention[s].Length == 0)
                continue;
            int nonZero = 0;
            for (int j = 0; j < perStepAttention[s].Length; j++)
                if (perStepAttention[s][j] > 1e-6)
                    nonZero++;
            sparsity[s] = 1.0 - (double)nonZero / perStepAttention[s].Length;
        }
        return sparsity;
    }

    private static (double[] BinConfidence, double[] BinAccuracy, int[] BinCounts) ComputeReliabilityDiagram(
        IReadOnlyList<TrainingSample> samples, TabNetWeights w, ModelSnapshot calibrationSnapshot, int numBins = 10)
    {
        if (samples.Count < numBins * 2)
            return ([], [], []);

        var pairs = new (double CalibP, bool IsPositive)[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            double calibP = TabNetCalibProb(samples[i].Features, w, calibrationSnapshot);
            pairs[i] = (calibP, samples[i].Direction > 0);
        }
        Array.Sort(pairs, (a, b) => a.CalibP.CompareTo(b.CalibP));

        int samplesPerBin = Math.Max(1, samples.Count / numBins);
        var conf = new double[numBins];
        var acc = new double[numBins];
        var counts = new int[numBins];

        for (int b = 0; b < numBins; b++)
        {
            int start = b * samplesPerBin;
            int end = b == numBins - 1 ? samples.Count : Math.Min(samples.Count, (b + 1) * samplesPerBin);
            if (start >= end)
                break;

            double sumConf = 0.0;
            int positives = 0;
            for (int i = start; i < end; i++)
            {
                sumConf += pairs[i].CalibP;
                if (pairs[i].IsPositive)
                    positives++;
            }

            int count = end - start;
            conf[b] = sumConf / count;
            acc[b] = (double)positives / count;
            counts[b] = count;
        }

        return (conf, acc, counts);
    }

    private static (double CalibrationLoss, double RefinementLoss) ComputeMurphyDecomposition(
        IReadOnlyList<TrainingSample> samples, TabNetWeights w, ModelSnapshot calibrationSnapshot, int bins = 10)
    {
        if (samples.Count < bins)
            return (0.0, 0.0);

        var binSumP = new double[bins];
        var binSumY = new double[bins];
        var binCount = new int[bins];
        int totalPos = 0;

        foreach (var sample in samples)
        {
            double p = TabNetCalibProb(sample.Features, w, calibrationSnapshot);
            int y = sample.Direction > 0 ? 1 : 0;
            int bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binSumP[bin] += p;
            binSumY[bin] += y;
            binCount[bin]++;
            totalPos += y;
        }

        double baseRate = (double)totalPos / samples.Count;
        double calibrationLoss = 0.0;
        double refinementLoss = 0.0;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0)
                continue;
            double avgP = binSumP[b] / binCount[b];
            double avgY = binSumY[b] / binCount[b];
            double weight = (double)binCount[b] / samples.Count;
            calibrationLoss += (avgP - avgY) * (avgP - avgY) * weight;
            refinementLoss += avgY * Math.Max(0.0, 1.0 - avgY) * weight;
        }

        if (!double.IsFinite(baseRate))
            return (0.0, 0.0);

        return (calibrationLoss, refinementLoss);
    }

    private static double ComputePredictionStabilityScore(
        IReadOnlyList<TrainingSample> samples, TabNetWeights w, ModelSnapshot calibrationSnapshot)
    {
        if (samples.Count == 0)
            return 0.0;

        double sum = 0.0;
        for (int i = 0; i < samples.Count; i++)
            sum += Math.Abs(TabNetCalibProb(samples[i].Features, w, calibrationSnapshot) - 0.5);
        return sum / samples.Count;
    }

    private static (double[] Centroid, double DistanceMean, double DistanceStd, double EntropyThreshold, double UncertaintyThreshold)
        ComputeActivationReferenceStats(IReadOnlyList<TrainingSample> samples, TabNetWeights w)
    {
        int count = Math.Min(samples.Count, 400);
        if (count <= 0)
            return (new double[w.HiddenDim], 0.0, 0.0, 0.0, 0.0);

        var centroid = new double[w.HiddenDim];
        var activations = new double[count][];
        var entropies = new double[count];
        var priorBuf = new double[w.F];
        var attnBuf = new double[w.F];

        for (int i = 0; i < count; i++)
        {
            var fwd = ForwardPass(samples[i].Features, w, priorBuf, attnBuf, false, 0, null);
            activations[i] = (double[])fwd.AggregatedH.Clone();
            for (int h = 0; h < w.HiddenDim; h++)
                centroid[h] += activations[i][h];

            double entropy = 0.0;
            for (int s = 0; s < w.NSteps; s++)
            {
                for (int j = 0; j < w.F && j < fwd.StepAttn[s].Length; j++)
                {
                    double a = fwd.StepAttn[s][j];
                    if (a > Eps)
                        entropy -= a * Math.Log(a);
                }
            }
            entropies[i] = entropy / Math.Max(1, w.NSteps);
        }

        for (int h = 0; h < w.HiddenDim; h++)
            centroid[h] /= count;

        var distances = new double[count];
        var combinedUncertainty = new double[count];
        for (int i = 0; i < count; i++)
        {
            double sq = 0.0;
            for (int h = 0; h < w.HiddenDim; h++)
            {
                double d = activations[i][h] - centroid[h];
                sq += d * d;
            }
            distances[i] = Math.Sqrt(sq);
        }

        double distanceMean = distances.Average();
        double distanceStd = distances.Length > 1 ? StdDev(distances, distanceMean) : 0.0;
        double entropyThreshold = Quantile(entropies, 0.90);
        double distanceScale = distanceMean + 2.0 * Math.Max(distanceStd, 1e-6);
        for (int i = 0; i < count; i++)
        {
            double entropyPart = entropyThreshold > 1e-6 ? Math.Min(1.0, entropies[i] / entropyThreshold) : 0.0;
            double distancePart = distanceScale > 1e-6 ? Math.Min(1.0, distances[i] / distanceScale) : 0.0;
            combinedUncertainty[i] = 0.5 * entropyPart + 0.5 * distancePart;
        }

        return (centroid, distanceMean, distanceStd, entropyThreshold, Quantile(combinedUncertainty, 0.90));
    }

    private static double[] ComputeBnDriftByLayer(TabNetWeights w, List<TrainingSample> fitSet, int ghostBatchSize, int minCalibrationSamples)
    {
        if (fitSet.Count < minCalibrationSamples)
            return [];

        var (batchMeans, batchVars) = ComputeEpochBatchStats(w, fitSet, ghostBatchSize, minCalibrationSamples, new Random(TrainerSeed));
        var drift = new double[w.TotalBnLayers];
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = Math.Min(w.BnMean[b].Length, Math.Min(batchMeans[b].Length, batchVars[b].Length));
            if (dim == 0)
                continue;
            double sum = 0.0;
            for (int j = 0; j < dim; j++)
            {
                double meanDelta = Math.Abs(w.BnMean[b][j] - batchMeans[b][j]);
                double varDelta = Math.Abs(Math.Sqrt(Math.Max(w.BnVar[b][j], 0.0)) - Math.Sqrt(Math.Max(batchVars[b][j], 0.0)));
                sum += 0.5 * (meanDelta + varDelta);
            }
            drift[b] = sum / dim;
        }
        return drift;
    }

    private static double Quantile(double[] values, double q)
    {
        if (values.Length == 0)
            return 0.0;
        q = Math.Clamp(q, 0.0, 1.0);
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return QuantileSorted(copy, q);
    }

    /// <summary>Quantile on a pre-sorted array (avoids clone+sort when caller already sorted).</summary>
    private static double QuantileSorted(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0.0;
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        double t = pos - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * t;
    }

    /// <summary>Quantile using a pre-allocated scratch buffer (avoids heap allocation in hot loops).</summary>
    private static double QuantilePooled(double[] values, double q, double[] scratch)
    {
        if (values.Length == 0) return 0.0;
        q = Math.Clamp(q, 0.0, 1.0);
        int len = Math.Min(values.Length, scratch.Length);
        Array.Copy(values, scratch, len);
        Array.Sort(scratch, 0, len);
        // Compute quantile directly on scratch without allocating
        double pos = q * (len - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi || hi >= len) return scratch[lo];
        double t = pos - lo;
        return scratch[lo] + (scratch[hi] - scratch[lo]) * t;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CONFORMAL / JACKKNIFE / META-LABEL / ABSTENTION / QUANTILE /
    //  BOUNDARY / DURBIN-WATSON / MI / MAGNITUDE REGRESSOR
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, ModelSnapshot calibrationSnapshot, double alpha, int minCalibrationSamples)
    {
        if (calSet.Count < minCalibrationSamples) return 0.5;
        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = TabNetCalibProb(calSet[i].Features, w, calibrationSnapshot);
            int y = calSet[i].Direction > 0 ? 1 : 0;
            scores[i] = 1.0 - (y == 1 ? p : 1.0 - p);
        }
        Array.Sort(scores);
        int qIdx = Math.Clamp((int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1, 0, calSet.Count - 1);
        return scores[qIdx];
    }

    private static (double GlobalQHat, double BuyQHat, double SellQHat) ComputeMondrianConformalQHats(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        double alpha,
        int minCalibrationSamples)
    {
        double globalQHat = ComputeConformalQHat(calSet, w, calibrationSnapshot, alpha, minCalibrationSamples);
        if (calSet.Count < minCalibrationSamples)
            return (globalQHat, globalQHat, globalQHat);

        var buyScores = new List<double>(calSet.Count);
        var sellScores = new List<double>(calSet.Count);
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = TabNetCalibProb(calSet[i].Features, w, calibrationSnapshot);
            if (calSet[i].Direction > 0)
                buyScores.Add(1.0 - p);
            else
                sellScores.Add(p);
        }

        static double ComputeClassQHat(List<double> scores, double alpha, double fallback)
        {
            if (scores.Count == 0)
                return fallback;
            scores.Sort();
            int qIdx = Math.Clamp((int)Math.Ceiling((1.0 - alpha) * (scores.Count + 1)) - 1, 0, scores.Count - 1);
            return scores[qIdx];
        }

        double buyQHat = buyScores.Count >= Math.Max(5, minCalibrationSamples / 2)
            ? ComputeClassQHat(buyScores, alpha, globalQHat)
            : globalQHat;
        double sellQHat = sellScores.Count >= Math.Max(5, minCalibrationSamples / 2)
            ? ComputeClassQHat(sellScores, alpha, globalQHat)
            : globalQHat;
        return (globalQHat, buyQHat, sellQHat);
    }

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        double decisionThreshold,
        int minCalibrationSamples,
        int calibrationEpochs,
        double calibrationLr,
        int[]? sampleIndices = null)
    {
        int n = sampleIndices?.Length ?? calSet.Count;
        if (n < minCalibrationSamples) return ([0.0], 0.0);
        double metaW = 0.0, metaB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0; int t = 0;
        for (int ep = 0; ep < calibrationEpochs; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                int sampleIndex = sampleIndices is null ? i : sampleIndices[i];
                double p = TabNetCalibProb(calSet[sampleIndex].Features, w, calibrationSnapshot);
                int correct = ((p >= decisionThreshold) == (calSet[sampleIndex].Direction > 0)) ? 1 : 0;
                double metaP = Sigmoid(metaW * p + metaB);
                dW += (metaP - correct) * p; dB += metaP - correct;
            }
            t++; double bc1 = 1.0 - Math.Pow(AdamBeta1, t), bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n; vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n; vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            metaW -= calibrationLr * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon);
            metaB -= calibrationLr * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }
        return ([metaW], metaB);
    }

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        IReadOnlyList<TrainingSample> calSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        double decisionThreshold,
        int minCalibrationSamples,
        int calibrationEpochs,
        double calibrationLr,
        int[]? sampleIndices = null)
    {
        int n = sampleIndices?.Length ?? calSet.Count;
        if (n < minCalibrationSamples) return ([0.0], 0.0, 0.5);
        var probs = new double[n];
        for (int i = 0; i < n; i++)
        {
            int sampleIndex = sampleIndices is null ? i : sampleIndices[i];
            probs[i] = TabNetCalibProb(calSet[sampleIndex].Features, w, calibrationSnapshot);
        }
        double absW = 0.0, absB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0; int t = 0;
        for (int ep = 0; ep < calibrationEpochs; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double feat = Math.Abs(probs[i] - decisionThreshold);
                int sampleIndex = sampleIndices is null ? i : sampleIndices[i];
                int correct = ((probs[i] >= decisionThreshold) == (calSet[sampleIndex].Direction > 0)) ? 1 : 0;
                double abstP = Sigmoid(absW * feat + absB);
                dW += (abstP - correct) * feat; dB += abstP - correct;
            }
            t++; double bc1 = 1.0 - Math.Pow(AdamBeta1, t), bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n; vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n; vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            absW -= calibrationLr * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon);
            absB -= calibrationLr * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }
        double bestPrec = 0, bestThresh = 0.1;
        int minCoverage = Math.Max(1, n / 5); // require at least 20% coverage
        for (int ti = 1; ti <= 40; ti++)
        {
            double thresh = ti / 100.0; int tpA = 0, fpA = 0;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(probs[i] - decisionThreshold) < thresh) continue;
                int sampleIndex = sampleIndices is null ? i : sampleIndices[i];
                if ((probs[i] >= decisionThreshold) == (calSet[sampleIndex].Direction > 0)) tpA++; else fpA++;
            }
            if (tpA + fpA < minCoverage) continue;
            double prec = (double)tpA / (tpA + fpA);
            if (prec > bestPrec) { bestPrec = prec; bestThresh = thresh; }
        }
        return ([absW], absB, bestThresh);
    }

    private static AdaptiveHeadCrossFitResult CrossFitAdaptiveHeads(
        IReadOnlyList<TrainingSample> diagnosticsSet,
        TabNetWeights w,
        ModelSnapshot calibrationSnapshot,
        double decisionThreshold,
        int minAdaptiveHeadSamples,
        int calibrationEpochs,
        double calibrationLr)
    {
        var folds = BuildTemporalCrossFitFolds(
            diagnosticsSet.Count,
            Math.Max(5, minAdaptiveHeadSamples / 2),
            maxFolds: 3);
        if (folds.Count < 2)
            return new AdaptiveHeadCrossFitResult(false, [0.0], 0.0, 0.5, [0.0], 0.0, 0.5, 0, [], [], []);

        var metaScores = Enumerable.Repeat(double.NaN, diagnosticsSet.Count).ToArray();
        var abstentionScores = Enumerable.Repeat(double.NaN, diagnosticsSet.Count).ToArray();
        var correctnessLabels = new int[diagnosticsSet.Count];

        foreach (var fold in folds)
        {
            var (foldMetaWeights, foldMetaBias) = FitMetaLabelModel(
                diagnosticsSet,
                w,
                calibrationSnapshot,
                decisionThreshold,
                minAdaptiveHeadSamples,
                calibrationEpochs,
                calibrationLr,
                fold.TrainIndices);
            var (foldAbstentionWeights, foldAbstentionBias, _) = FitAbstentionModel(
                diagnosticsSet,
                w,
                calibrationSnapshot,
                decisionThreshold,
                minAdaptiveHeadSamples,
                calibrationEpochs,
                calibrationLr,
                fold.TrainIndices);

            foreach (int holdoutIndex in fold.HoldoutIndices)
            {
                double p = TabNetCalibProb(diagnosticsSet[holdoutIndex].Features, w, calibrationSnapshot);
                int correct = ((p >= decisionThreshold) == (diagnosticsSet[holdoutIndex].Direction > 0)) ? 1 : 0;
                correctnessLabels[holdoutIndex] = correct;
                metaScores[holdoutIndex] = ComputeMetaLabelProbability(p, foldMetaWeights, foldMetaBias);
                abstentionScores[holdoutIndex] = ComputeAbstentionProbability(p, decisionThreshold, foldAbstentionWeights, foldAbstentionBias);
            }
        }

        double metaThreshold = SelectBinaryProbabilityThreshold(metaScores, correctnessLabels, defaultThreshold: 0.5, preferPrecision: false);
        double abstentionThreshold = SelectBinaryProbabilityThreshold(abstentionScores, correctnessLabels, defaultThreshold: 0.5, preferPrecision: true);

        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            diagnosticsSet,
            w,
            calibrationSnapshot,
            decisionThreshold,
            minAdaptiveHeadSamples,
            calibrationEpochs,
            calibrationLr);
        var (abstentionWeights, abstentionBias, _) = FitAbstentionModel(
            diagnosticsSet,
            w,
            calibrationSnapshot,
            decisionThreshold,
            minAdaptiveHeadSamples,
            calibrationEpochs,
            calibrationLr);

        return new AdaptiveHeadCrossFitResult(
            true,
            metaLabelWeights,
            metaLabelBias,
            metaThreshold,
            abstentionWeights,
            abstentionBias,
            abstentionThreshold,
            folds.Count,
            folds.Select(fold => fold.HoldoutStartIndex).ToArray(),
            folds.Select(fold => fold.HoldoutCount).ToArray(),
            folds.Select(fold => fold.Hash).ToArray());
    }

    private static List<TemporalCrossFitFold> BuildTemporalCrossFitFolds(int totalCount, int minHoldoutCount, int maxFolds)
    {
        var folds = new List<TemporalCrossFitFold>();
        if (totalCount < minHoldoutCount * 2)
            return folds;

        int foldCount = Math.Min(maxFolds, Math.Max(2, totalCount / Math.Max(minHoldoutCount, 1)));
        foldCount = Math.Min(foldCount, Math.Max(2, totalCount / minHoldoutCount));
        if (foldCount < 2)
            return folds;

        int baseFoldSize = totalCount / foldCount;
        int remainder = totalCount % foldCount;
        int start = 0;
        for (int fold = 0; fold < foldCount; fold++)
        {
            int count = baseFoldSize + (fold < remainder ? 1 : 0);
            if (count < minHoldoutCount)
                continue;

            int[] holdout = Enumerable.Range(start, count).ToArray();
            int[] train = Enumerable.Range(0, totalCount)
                .Where(index => index < start || index >= start + count)
                .ToArray();
            if (train.Length < minHoldoutCount)
            {
                start += count;
                continue;
            }

            string hash = ComputeStableHash($"tabnet-crossfit:{totalCount}:{fold}:{start}:{count}");
            folds.Add(new TemporalCrossFitFold(start, count, train, holdout, hash));
            start += count;
        }

        return folds;
    }

    private static double ComputeMetaLabelProbability(double calibP, double[] weights, double bias)
    {
        if (weights.Length == 0)
            return 0.5;
        return Math.Clamp(Sigmoid(weights[0] * calibP + bias), ProbClampMin, 1.0 - ProbClampMin);
    }

    private static double ComputeAbstentionProbability(double calibP, double decisionThreshold, double[] weights, double bias)
    {
        if (weights.Length == 0)
            return 0.5;
        double margin = Math.Abs(calibP - decisionThreshold);
        return Math.Clamp(Sigmoid(weights[0] * margin + bias), ProbClampMin, 1.0 - ProbClampMin);
    }

    private static double SelectBinaryProbabilityThreshold(
        double[] scores,
        int[] labels,
        double defaultThreshold,
        bool preferPrecision)
    {
        var pairs = scores
            .Select((score, index) => (Score: score, Label: labels[index]))
            .Where(pair => double.IsFinite(pair.Score))
            .ToArray();
        if (pairs.Length < 10)
            return defaultThreshold;

        int positives = pairs.Count(pair => pair.Label == 1);
        if (positives == 0)
            return defaultThreshold;

        double bestThreshold = defaultThreshold;
        double bestScore = double.NegativeInfinity;
        for (int step = 25; step <= 85; step++)
        {
            double threshold = step / 100.0;
            int tp = 0, fp = 0, fn = 0, accepted = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].Score >= threshold)
                {
                    accepted++;
                    if (pairs[i].Label == 1) tp++;
                    else fp++;
                }
                else if (pairs[i].Label == 1)
                {
                    fn++;
                }
            }

            if (accepted < Math.Max(5, pairs.Length / 20))
                continue;

            double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0.0;
            double recall = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0.0;
            double f1 = (precision + recall) > 0.0 ? 2.0 * precision * recall / (precision + recall) : 0.0;
            double coverage = (double)accepted / pairs.Length;
            double score = preferPrecision
                ? precision + 0.25 * coverage
                : f1 + 0.10 * precision + 0.05 * coverage;
            if (score > bestScore)
            {
                bestScore = score;
                bestThreshold = threshold;
            }
        }

        return bestScore > double.NegativeInfinity ? bestThreshold : defaultThreshold;
    }

    private static string ComputeStableHash(string payload)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static (double[] Weights, double Bias) FitQuantileRegressor(IReadOnlyList<TrainingSample> trainSet, int F, double tau, int minCalibrationSamples)
    {
        if (trainSet.Count < minCalibrationSamples) return (new double[F], 0.0);
        int n = trainSet.Count; var qw = new double[F]; double b = 0.0;
        for (int ep = 0; ep < 100; ep++)
            for (int i = 0; i < n; i++)
            {
                double pred = b;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) pred += qw[j] * trainSet[i].Features[j];
                double grad = (trainSet[i].Magnitude - pred) >= 0 ? -tau : (1.0 - tau);
                b -= 0.001 * grad;
                b = Math.Clamp(b, -MaxWeightVal, MaxWeightVal);
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                {
                    qw[j] -= 0.001 * grad * trainSet[i].Features[j];
                    qw[j] = Math.Clamp(qw[j], -MaxWeightVal, MaxWeightVal);
                }
            }
        return (qw, b);
    }

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        var distances = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++) distances[i] = Math.Abs(TabNetRawProb(calSet[i].Features, w) - 0.5);
        double mean = distances.Average();
        return (mean, StdDev(distances.ToList(), mean));
    }

    private static double ComputeDurbinWatson(IReadOnlyList<TrainingSample> trainSet, double[] magWeights, double magBias, int F, int minCalibrationSamples)
    {
        if (trainSet.Count < minCalibrationSamples || magWeights.Length == 0) return 2.0;
        int n = trainSet.Count; var residuals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(F, magWeights.Length) && j < trainSet[i].Features.Length; j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) den += residuals[i] * residuals[i];
        for (int i = 1; i < n; i++) { double d = residuals[i] - residuals[i - 1]; num += d * d; }
        return den > Eps ? num / den : 2.0;
    }

    private static string[] ComputeRedundantFeaturePairs(IReadOnlyList<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 30 || F < 2) return [];
        int n = Math.Min(trainSet.Count, MeanAttentionSampleCap), numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));
        var redundant = new List<string>();
        for (int a = 0; a < F; a++)
            for (int b = a + 1; b < F; b++)
            {
                var vA = new double[n]; var vB = new double[n];
                for (int i = 0; i < n; i++) { vA[i] = trainSet[i].Features[a]; vB[i] = trainSet[i].Features[b]; }
                double mi = ComputeMI(vA, vB, numBins), hA = ComputeEntropy(vA, numBins), hB = ComputeEntropy(vB, numBins);
                double norm = Math.Max(hA, hB);
                if (norm > 1e-10 && mi / norm > threshold)
                {
                    string nA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nB = b < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[b] : $"F{b}";
                    redundant.Add($"{nA}\u2194{nB}:{mi / norm:F2}");
                }
            }
        return redundant.ToArray();
    }

    private static (double[] Weights, double Bias) FitLinearRegressor(List<TrainingSample> train, int featureCount, TrainingHyperparams hp, double huberDelta)
    {
        var lw = new double[featureCount]; double b = 0.0;
        bool canEarlyStop = train.Count >= 30;
        int valSize = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var valSet = canEarlyStop ? train[^valSize..] : train;
        var trainSet = canEarlyStop ? train[..^valSize] : train;
        if (trainSet.Count == 0) return (lw, b);
        var mW = new double[featureCount]; var vW = new double[featureCount];
        double mB = 0.0, vB = 0.0, beta1t = 1.0, beta2t = 1.0; int t = 0;
        double bestValLoss = double.MaxValue; var bestW = new double[featureCount]; double bestB = 0.0; int patience = 0;
        int epochs = hp.MaxEpochs; double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.1, l2 = hp.L2Lambda;
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));
            foreach (var s in trainSet)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += lw[j] * s.Features[j];
                double err = pred - s.Magnitude; if (!double.IsFinite(err)) continue;
                double huberGrad = Math.Abs(err) <= huberDelta ? err : huberDelta * Math.Sign(err);
                double bc1 = 1.0 - beta1t, bc2 = 1.0 - beta2t, alphat = alpha * Math.Sqrt(bc2) / bc1;
                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * huberGrad; vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * lw[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g; vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    lw[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0.0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += lw[j] * s.Features[j];
                double err = pred - s.Magnitude; if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= huberDelta ? 0.5 * err * err : huberDelta * Math.Abs(err) - 0.5 * huberDelta * huberDelta; valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;
            if (valLoss < bestValLoss - EarlyStopMinDelta) { bestValLoss = valLoss; Array.Copy(lw, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= hp.EarlyStoppingPatience) break;
        }
        if (canEarlyStop) { lw = bestW; b = bestB; }
        return (lw, b);
    }

    // ── Adversarial validation (CPU) ──────────────────────────────────────────

    private static double ComputeAdversarialAuc(
        List<TrainingSample> trainSet, List<TrainingSample> testSet, int F)
    {
        int n1 = testSet.Count; int n0 = Math.Min(trainSet.Count, n1 * 5); int n = n0 + n1;
        if (n < 20) return 0.5;
        var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;
        var w = new double[F]; double b = 0;
        for (int epoch = 0; epoch < 60; epoch++)
        {
            double dB = 0; var dW = new double[F];
            for (int i = 0; i < n; i++)
            {
                float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
                double label = i < n0 ? 0.0 : 1.0;
                double z = b; for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
                double p = 1.0 / (1.0 + Math.Exp(-z)); double err = p - label;
                dB += err; for (int j = 0; j < F && j < features.Length; j++) dW[j] += err * features[j];
            }
            b -= 0.005 * dB / n; for (int j = 0; j < F; j++) w[j] -= 0.005 * (dW[j] / n + 0.01 * w[j]);
        }
        var scores = new (double Score, int Label)[n];
        for (int i = 0; i < n; i++)
        {
            float[] features = i < n0 ? trainSlice[i].Features : testSet[i - n0].Features;
            double z = b; for (int j = 0; j < F && j < features.Length; j++) z += w[j] * features[j];
            scores[i] = (1.0 / (1.0 + Math.Exp(-z)), i < n0 ? 0 : 1);
        }
        Array.Sort(scores, (a, c) => c.Score.CompareTo(a.Score));
        long tp = 0, aucNum = 0;
        foreach (var (_, lbl) in scores) { if (lbl == 1) tp++; else aucNum += tp; }
        return (n1 > 0 && n0 > 0) ? (double)aucNum / ((long)n1 * n0) : 0.5;
    }
}
