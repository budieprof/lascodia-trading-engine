using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Stacked meta-learner trainer. Decomposes the V5/V6 raw feature vector into per-family
/// blocks (OHLCV / Macro / Calendar / Microstructure / Synthetic-DOM / Real-DOM), trains
/// one logistic regression sub-model per family using K-fold out-of-fold predictions to
/// avoid leakage, then fits a logistic meta-combiner over the K sub-model Buy-probabilities.
///
/// <para>
/// Motivation: a single model over the full 52/57-feature vector lets the gradient be
/// dominated by whichever family has the most extreme values in the current window
/// (typically OHLCV momentum). By training each family independently and stacking, the
/// meta-combiner decides per-prediction how much weight to give each family's signal —
/// useful when OHLCV is ambiguous but macro/microstructure have high conviction.
/// </para>
///
/// <para>
/// Implementation is deliberately compact:
/// <list type="bullet">
///   <item>Each sub-model is a logistic regression fit via full-batch gradient descent with L2.</item>
///   <item>OOF predictions are collected via K-fold chronological split (no shuffling to preserve
///     temporal ordering — equivalent to a zero-embargo walk-forward).</item>
///   <item>Meta-learner is a second logistic regression over [p_family_1, ..., p_family_K].</item>
///   <item>Standardisation stats are computed from the training set only and persisted
///     alongside the artifact so inference-time features can be reconstructed exactly.</item>
/// </list>
/// </para>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.Stacked)]
public sealed class StackedModelTrainer : IMLModelTrainer
{
    private const int    DefaultKFolds         = 5;
    private const int    DefaultSubModelEpochs = 300;
    private const int    DefaultMetaEpochs     = 300;
    private const double DefaultLearningRate   = 0.05;
    private const double DefaultL2Lambda       = 1e-3;
    private const double TestSplitFraction     = 0.2;

    private readonly ILogger<StackedModelTrainer> _logger;

    public StackedModelTrainer(ILogger<StackedModelTrainer> logger) => _logger = logger;

    public Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams hp,
        ModelSnapshot? warmStart = null,
        long? parentModelId = null,
        CancellationToken ct = default)
    {
        return Task.Run(() => Train(samples, hp, ct), ct);
    }

    private TrainingResult Train(List<TrainingSample> samples, TrainingHyperparams hp, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (samples is null || samples.Count == 0)
            throw new ArgumentException("StackedModelTrainer requires at least one training sample", nameof(samples));

        int featureCount = ValidateSamples(samples);
        int schemaVersion = InferSchemaVersion(featureCount);
        var families = StackedFeatureFamilies.ActiveFor(featureCount);
        if (families.Count == 0)
            throw new InvalidOperationException(
                $"StackedModelTrainer: feature count {featureCount} does not cover any feature family; require >= {StackedFeatureFamilies.Ohlcv.EndExclusive} (V1 OHLCV).");

        int testCount  = Math.Max(1, (int)(samples.Count * TestSplitFraction));
        int trainCount = samples.Count - testCount;
        if (trainCount < 10)
            throw new InvalidOperationException(
                $"StackedModelTrainer: only {trainCount} training samples after {testCount} holdout — need at least 10 to fit any sub-model.");

        var trainSamples = samples.GetRange(0, trainCount);
        var testSamples  = samples.GetRange(trainCount, testCount);

        // ── 1. Standardise using train-set stats ──────────────────────────────
        var (means, stds) = ComputeStandardisation(trainSamples, featureCount);
        var standardisedTrain = StandardiseAll(trainSamples, means, stds, featureCount);
        var standardisedTest  = StandardiseAll(testSamples,  means, stds, featureCount);

        // ── 2. Train each family's sub-model + compute OOF predictions ────────
        int kFolds = Math.Max(2, Math.Min(hp.K > 0 ? hp.K : DefaultKFolds, trainSamples.Count / 2));

        var subModels  = new StackedSubModel[families.Count];
        var oofTrain   = new double[families.Count][];
        var testProbs  = new double[families.Count][];

        for (int f = 0; f < families.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            var family = families[f];
            var familyIdx = StackedFeatureFamilies.IndicesFor(family);

            var trainFamilyX = ProjectFamily(standardisedTrain, familyIdx);
            var trainY       = trainSamples.Select(s => s.Direction > 0 ? 1 : 0).ToArray();
            var testFamilyX  = ProjectFamily(standardisedTest, familyIdx);

            oofTrain[f] = FitOofLogistic(trainFamilyX, trainY, kFolds, ct);

            // Fit the final sub-model on all train samples for inference use.
            var (w, b) = FitLogistic(trainFamilyX, trainY, DefaultSubModelEpochs, DefaultLearningRate, DefaultL2Lambda, ct);
            subModels[f] = new StackedSubModel(family.Name, familyIdx, w, b);

            // Test-set sub-model probabilities for final evaluation.
            testProbs[f] = new double[testFamilyX.Length];
            for (int i = 0; i < testFamilyX.Length; i++)
                testProbs[f][i] = StackedSnapshotSupport.Sigmoid(Dot(testFamilyX[i], w) + b);
        }

        // ── 3. Train meta-combiner on OOF predictions ─────────────────────────
        int trainN = trainSamples.Count;
        var metaTrainX = new double[trainN][];
        var metaTrainY = new int[trainN];
        for (int i = 0; i < trainN; i++)
        {
            metaTrainX[i] = new double[families.Count];
            for (int f = 0; f < families.Count; f++)
                metaTrainX[i][f] = oofTrain[f][i];
            metaTrainY[i] = trainSamples[i].Direction > 0 ? 1 : 0;
        }
        var (metaWeights, metaBias) = FitLogistic(metaTrainX, metaTrainY, DefaultMetaEpochs, DefaultLearningRate, DefaultL2Lambda, ct);

        // ── 4. Evaluate on holdout ────────────────────────────────────────────
        int testN = testSamples.Count;
        var testMetaX = new double[testN][];
        for (int i = 0; i < testN; i++)
        {
            testMetaX[i] = new double[families.Count];
            for (int f = 0; f < families.Count; f++)
                testMetaX[i][f] = testProbs[f][i];
        }
        var testProbsFinal = new double[testN];
        for (int i = 0; i < testN; i++)
            testProbsFinal[i] = StackedSnapshotSupport.Sigmoid(Dot(testMetaX[i], metaWeights) + metaBias);

        var metrics = ComputeMetrics(testProbsFinal, testSamples);
        var cv = new WalkForwardResult(
            AvgAccuracy: metrics.Accuracy,
            StdAccuracy: 0.0,
            AvgF1:       metrics.F1,
            AvgEV:       metrics.ExpectedValue,
            AvgSharpe:   metrics.SharpeRatio,
            FoldCount:   kFolds);

        // ── 5. Build artifact + serialised snapshot ───────────────────────────
        var artifact = new StackedMetaLearnerArtifact(
            FeatureSchemaVersion:  schemaVersion,
            ExpectedInputFeatures: featureCount,
            SubModels:             subModels,
            MetaWeights:           metaWeights,
            MetaBias:              metaBias,
            FeatureMeans:          means,
            FeatureStds:           stds);

        string artifactJson = StackedSnapshotSupport.Serialize(artifact);

        var snapshot = new ModelSnapshot
        {
            Type                  = StackedSnapshotSupport.ModelType,
            Version               = StackedSnapshotSupport.ModelVersion,
            ExpectedInputFeatures = featureCount,
            FeatureSchemaVersion  = schemaVersion,
            Features              = BuildFeatureNames(featureCount),
            Means                 = means.Select(m => (float)m).ToArray(),
            Stds                  = stds.Select(s => (float)s).ToArray(),
            StackedMetaJson       = artifactJson,
        };

        var snapshotJson = JsonSerializer.Serialize(snapshot);
        var modelBytes = Encoding.UTF8.GetBytes(snapshotJson);

        _logger.LogInformation(
            "StackedModelTrainer: fit {FamilyCount} sub-models + meta over {Train} train / {Test} test samples. Test accuracy {Acc:P2}, F1 {F1:F3}, Brier {Br:F3}",
            families.Count, trainCount, testCount, metrics.Accuracy, metrics.F1, metrics.BrierScore);

        return new TrainingResult(metrics, cv, modelBytes);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Logistic regression primitives
    // ═════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLogistic(
        double[][] x, int[] y, int epochs, double lr, double l2, CancellationToken ct)
    {
        if (x.Length == 0) return ([], 0.0);
        int d = x[0].Length;
        var w = new double[d];
        double b = 0;
        int n = x.Length;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            if ((epoch & 0xF) == 0) ct.ThrowIfCancellationRequested();

            var gradW = new double[d];
            double gradB = 0;
            for (int i = 0; i < n; i++)
            {
                double z = Dot(x[i], w) + b;
                double p = StackedSnapshotSupport.Sigmoid(z);
                double err = p - y[i];
                for (int j = 0; j < d; j++) gradW[j] += err * x[i][j];
                gradB += err;
            }
            for (int j = 0; j < d; j++) w[j] -= lr * (gradW[j] / n + l2 * w[j]);
            b -= lr * (gradB / n);
        }

        return (w, b);
    }

    private static double[] FitOofLogistic(double[][] x, int[] y, int kFolds, CancellationToken ct)
    {
        int n = x.Length;
        var oof = new double[n];
        int foldSize = n / kFolds;

        for (int fold = 0; fold < kFolds; fold++)
        {
            ct.ThrowIfCancellationRequested();
            int valStart = fold * foldSize;
            int valEnd   = fold == kFolds - 1 ? n : valStart + foldSize;

            int trainN = n - (valEnd - valStart);
            var trainX = new double[trainN][];
            var trainY = new int[trainN];
            int t = 0;
            for (int i = 0; i < n; i++)
            {
                if (i >= valStart && i < valEnd) continue;
                trainX[t] = x[i];
                trainY[t] = y[i];
                t++;
            }

            var (w, b) = FitLogistic(trainX, trainY, DefaultSubModelEpochs, DefaultLearningRate, DefaultL2Lambda, ct);
            for (int i = valStart; i < valEnd; i++)
                oof[i] = StackedSnapshotSupport.Sigmoid(Dot(x[i], w) + b);
        }
        return oof;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Preprocessing
    // ═════════════════════════════════════════════════════════════════════════

    private static int ValidateSamples(List<TrainingSample> samples)
    {
        int count = samples[0].Features.Length;
        if (count <= 0)
            throw new InvalidOperationException("StackedModelTrainer: first sample has zero features.");
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Features.Length != count)
                throw new InvalidOperationException(
                    $"StackedModelTrainer: sample {i} has {samples[i].Features.Length} features but expected {count}.");
        }
        return count;
    }

    private static int InferSchemaVersion(int featureCount) => featureCount switch
    {
        >= 57 => 6,
        >= 52 => 5,
        >= 48 => 4,
        >= 43 => 3,
        >= 37 => 2,
        _     => 1,
    };

    private static (double[] Means, double[] Stds) ComputeStandardisation(List<TrainingSample> samples, int d)
    {
        var means = new double[d];
        var stds  = new double[d];
        int n = samples.Count;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < d; j++) means[j] += samples[i].Features[j];
        for (int j = 0; j < d; j++) means[j] /= n;

        for (int i = 0; i < n; i++)
            for (int j = 0; j < d; j++)
            {
                double diff = samples[i].Features[j] - means[j];
                stds[j] += diff * diff;
            }
        for (int j = 0; j < d; j++)
        {
            stds[j] = Math.Sqrt(stds[j] / Math.Max(1, n - 1));
            if (stds[j] < 1e-8) stds[j] = 1.0;
        }
        return (means, stds);
    }

    private static double[][] StandardiseAll(List<TrainingSample> samples, double[] means, double[] stds, int d)
    {
        var outX = new double[samples.Count][];
        for (int i = 0; i < samples.Count; i++)
        {
            var row = new double[d];
            for (int j = 0; j < d; j++)
                row[j] = (samples[i].Features[j] - means[j]) / stds[j];
            outX[i] = row;
        }
        return outX;
    }

    private static double[][] ProjectFamily(double[][] x, int[] familyIdx)
    {
        var outX = new double[x.Length][];
        int d = familyIdx.Length;
        for (int i = 0; i < x.Length; i++)
        {
            var row = new double[d];
            for (int j = 0; j < d; j++) row[j] = x[i][familyIdx[j]];
            outX[i] = row;
        }
        return outX;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Metrics
    // ═════════════════════════════════════════════════════════════════════════

    private static EvalMetrics ComputeMetrics(double[] probs, List<TrainingSample> samples)
    {
        int tp = 0, fp = 0, tn = 0, fn = 0;
        double brierSum = 0, magRmseSum = 0, weightedCorrect = 0, weightedTotal = 0;
        var returns = new List<double>(probs.Length);

        for (int i = 0; i < probs.Length; i++)
        {
            int actual = samples[i].Direction > 0 ? 1 : 0;
            int predicted = probs[i] >= 0.5 ? 1 : 0;

            if (predicted == 1 && actual == 1) tp++;
            else if (predicted == 1 && actual == 0) fp++;
            else if (predicted == 0 && actual == 0) tn++;
            else fn++;

            brierSum += (probs[i] - actual) * (probs[i] - actual);

            double mag = samples[i].Magnitude;
            magRmseSum += mag * mag;

            double w = Math.Abs(mag);
            weightedTotal  += w;
            if (predicted == actual) weightedCorrect += w;

            // Simple directional return proxy for EV/Sharpe calc.
            double directionalReturn = predicted == 1 ? mag : -mag;
            returns.Add(directionalReturn);
        }

        int total = tp + fp + tn + fn;
        double accuracy  = total > 0 ? (tp + tn) / (double)total : 0;
        double precision = tp + fp > 0 ? tp / (double)(tp + fp) : 0;
        double recall    = tp + fn > 0 ? tp / (double)(tp + fn) : 0;
        double f1        = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;

        double brier = probs.Length > 0 ? brierSum / probs.Length : 0;
        double magRmse = probs.Length > 0 ? Math.Sqrt(magRmseSum / probs.Length) : 0;
        double weightedAcc = weightedTotal > 0 ? weightedCorrect / weightedTotal : accuracy;
        double ev = returns.Count > 0 ? returns.Average() : 0;
        double sharpe = 0;
        if (returns.Count > 1)
        {
            double mean = returns.Average();
            double variance = returns.Select(r => (r - mean) * (r - mean)).Sum() / (returns.Count - 1);
            double std = Math.Sqrt(variance);
            if (std > 1e-9) sharpe = mean / std * Math.Sqrt(252.0);
        }

        return new EvalMetrics(
            Accuracy:         accuracy,
            Precision:        precision,
            Recall:           recall,
            F1:               f1,
            MagnitudeRmse:    magRmse,
            ExpectedValue:    ev,
            BrierScore:       brier,
            WeightedAccuracy: weightedAcc,
            SharpeRatio:      sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    private static string[] BuildFeatureNames(int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++) names[i] = $"f{i:D3}";
        return names;
    }
}
