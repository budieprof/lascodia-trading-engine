using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class RocketModelTrainer
{

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET kernel generation
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[][] Weights, int[] Dilations, bool[] Paddings, int[] Lengths, int[]? ChannelStarts, int[]? ChannelEnds)
        GenerateKernels(int numKernels, int featureCount, Random rng,
            bool useMiniWeights = false, bool multivariate = false)
    {
        var weights   = new double[numKernels][];
        var dilations = new int[numKernels];
        var paddings  = new bool[numKernels];
        var lengths   = new int[numKernels];

        // #2: Multivariate channel-independent kernels
        int[]? channelStarts = null;
        int[]? channelEnds   = null;
        if (multivariate && featureCount >= 6)
        {
            channelStarts = new int[numKernels];
            channelEnds   = new int[numKernels];
            int groupSize = featureCount / 3;
            for (int k = 0; k < numKernels; k++)
            {
                int group = k % 3;
                channelStarts[k] = group * groupSize;
                channelEnds[k]   = group == 2 ? featureCount : (group + 1) * groupSize;
            }
        }

        for (int k = 0; k < numKernels; k++)
        {
            int len    = KernelLengths[rng.Next(KernelLengths.Length)];
            double[] w = new double[len];

            if (useMiniWeights)
            {
                // #1: MiniRocket ternary {-1, 0, 1} kernel weights
                for (int i = 0; i < len; i++) w[i] = rng.Next(3) - 1;
            }
            else
            {
                for (int i = 0; i < len; i++) w[i] = SampleGaussian(rng);
            }

            double wMean = 0;
            for (int i = 0; i < len; i++) wMean += w[i];
            wMean /= len;
            for (int i = 0; i < len; i++) w[i] -= wMean;

            int effectiveFeatureCount = (channelEnds is not null && channelStarts is not null)
                ? channelEnds[k] - channelStarts[k]
                : featureCount;
            double A = len > 1 ? Math.Log2((effectiveFeatureCount - 1.0) / (len - 1) + 1e-6) : 0;
            int dil  = A > 0 ? (int)Math.Floor(Math.Pow(2, rng.NextDouble() * A)) : 1;
            dil = Math.Max(1, dil);

            weights[k]   = w;
            dilations[k] = dil;
            paddings[k]  = rng.NextDouble() < 0.5;
            lengths[k]   = len;
        }

        return (weights, dilations, paddings, lengths, channelStarts, channelEnds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET feature extraction
    // ═══════════════════════════════════════════════════════════════════════════

    private static List<double[]> ExtractRocketFeatures(
        List<TrainingSample> samples,
        double[][] kernelWeights, int[] kernelDilations, bool[] kernelPaddings, int[] kernelLengthArr,
        int numKernels, int[]? channelStarts = null, int[]? channelEnds = null)
    {
        int n = samples.Count;
        int F = samples.Count > 0 ? samples[0].Features.Length : 0;
        var result = new List<double[]>(n);

        for (int i = 0; i < n; i++)
        {
            double[] feat = new double[2 * numKernels];
            float[]  x    = samples[i].Features;

            for (int k = 0; k < numKernels; k++)
            {
                double[] w   = kernelWeights[k];
                int      len = kernelLengthArr[k];
                int      dil = kernelDilations[k];
                bool     pad = kernelPaddings[k];

                // #2: Channel-independent convolution range
                int chStart = channelStarts is not null ? channelStarts[k] : 0;
                int chEnd   = channelEnds is not null ? channelEnds[k] : F;
                int chLen   = chEnd - chStart;

                int padding   = pad ? (len - 1) * dil / 2 : 0;
                int outputLen = chLen + 2 * padding - (len - 1) * dil;

                double maxVal  = double.MinValue;
                int    ppvPos  = 0;
                int    posCount = 0;

                for (int pos = 0; pos < outputLen; pos++)
                {
                    double dot = 0;
                    for (int j = 0; j < len; j++)
                    {
                        int srcIdx = chStart + pos + j * dil - padding;
                        double xVal = (srcIdx >= 0 && srcIdx < F) ? x[srcIdx] : 0;
                        dot += w[j] * xVal;
                    }
                    if (dot > maxVal) maxVal = dot;
                    if (dot > 0) ppvPos++;
                    posCount++;
                }

                feat[k]              = maxVal == double.MinValue ? 0 : maxVal;
                feat[numKernels + k] = posCount > 0 ? (double)ppvPos / posCount : 0;
            }

            result.Add(feat);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET feature standardisation
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Means, double[] Stds) ComputeRocketStandardization(
        List<double[]> features, int dim)
    {
        var rMeans = new double[dim];
        var rStds  = new double[dim];
        int n = features.Count;

        for (int j = 0; j < dim; j++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += features[i][j];
            double mean = sum / n;

            double varSum = 0;
            for (int i = 0; i < n; i++)
            {
                double d = features[i][j] - mean;
                varSum += d * d;
            }
            double std = n > 1 ? Math.Sqrt(varSum / n) : 1.0;

            rMeans[j] = mean;
            rStds[j]  = std < 1e-10 ? 1.0 : std;
        }

        return (rMeans, rStds);
    }

    private static void StandardizeRocketInPlace(
        List<double[]> features, double[] rMeans, double[] rStds, int dim)
    {
        foreach (var f in features)
        {
            for (int j = 0; j < dim; j++)
                f[j] = (f[j] - rMeans[j]) / rStds[j];
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ridge regression with Adam optimiser + cosine LR + early stopping
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, int EarlyStopEpoch, double BestValLoss, int SwaCount) TrainRidgeAdam(
        List<double[]>       features,
        List<TrainingSample> labels,
        int                  dim,
        TrainingHyperparams  hp,
        double[]?            densityWeights,
        ModelSnapshot?       warmStart,
        CancellationToken    ct)
    {
        int n       = features.Count;
        int valSize = Math.Max(20, n / 10);
        int trainN  = n - valSize;

        double l2    = hp.L2Lambda > 0 ? hp.L2Lambda : 0.01;
        double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        int maxEpochs = hp.MaxEpochs > 0 ? hp.MaxEpochs : 200;
        int patience  = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : 20;
        double labelSmoothing = hp.LabelSmoothing;

        // Temporal weights
        var temporalWeights = ComputeTemporalWeights(trainN, hp.TemporalDecayLambda);

        // Blend density-ratio weights
        if (densityWeights is { Length: > 0 } && densityWeights.Length >= trainN)
        {
            var blended = new double[trainN];
            double sum = 0;
            for (int i = 0; i < trainN; i++)
            {
                blended[i] = temporalWeights[i] * densityWeights[i];
                sum += blended[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < trainN; i++) blended[i] /= sum;
            temporalWeights = blended;
        }

        // Initialise weights (warm-start or zeros)
        double[] w;
        double   bias;
        if (warmStart?.Weights is { Length: > 0 } && warmStart.Weights[0].Length == dim)
        {
            w    = [..warmStart.Weights[0]];
            bias = warmStart.Biases is { Length: > 0 } ? warmStart.Biases[0] : 0.0;
        }
        else
        {
            w    = new double[dim];
            bias = 0.0;
        }

        // Adam moment vectors
        var mW = new double[dim];
        var vW = new double[dim];
        double mB = 0, vB = 0;
        double beta1t = 1.0, beta2t = 1.0;
        int t = 0;

        double bestValLoss = double.MaxValue;
        int    patienceCounter = 0;
        int    earlyStopEpoch = 0; // #25: track early stopping epoch
        double[] bestW = [..w];
        double   bestBias = bias;

        // Soft labels
        var softLabels = new double[trainN];
        double posLabel = 1.0 - labelSmoothing;
        double negLabel = labelSmoothing;
        for (int i = 0; i < trainN; i++)
            softLabels[i] = labels[i].Direction > 0 ? posLabel : negLabel;

        // Class weights for balanced training
        double rocketCwBuy = 1.0, rocketCwSell = 1.0;
        if (hp.UseClassWeights)
        {
            int bc = 0; for (int ii = 0; ii < trainN; ii++) if (labels[ii].Direction > 0) bc++;
            int sc = trainN - bc;
            if (bc > 0 && sc > 0) { rocketCwBuy = (double)trainN / (2.0 * bc); rocketCwSell = (double)trainN / (2.0 * sc); }
        }

        // Mixup augmentation (post-ROCKET feature space) — re-sampled each epoch
        bool useMixup = hp.MixupAlpha > 0.0;
        double[][]? mixupFeatures = null;
        double[]?   mixupLabels   = null;
        Random? mixRng = useMixup ? new Random(42) : null;
        if (useMixup)
        {
            mixupFeatures = new double[trainN][];
            mixupLabels   = new double[trainN];
        }

        // SWA state
        bool     useSwa      = hp.SwaStartEpoch > 0 && hp.SwaFrequency > 0;
        double[] swaW        = useSwa ? new double[dim] : [];
        double   swaBias     = 0.0;
        int      swaCount    = 0;

        int batchSize = hp.MiniBatchSize > 1 ? hp.MiniBatchSize : DefaultBatchSize;
        var shuffledIdx = new int[trainN];
        for (int i = 0; i < trainN; i++) shuffledIdx[i] = i;
        var shuffleRng = new Random(trainN ^ dim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            // Fisher-Yates shuffle of training indices each epoch
            for (int i = shuffledIdx.Length - 1; i > 0; i--)
            {
                int swapIdx = shuffleRng.Next(i + 1);
                (shuffledIdx[i], shuffledIdx[swapIdx]) = (shuffledIdx[swapIdx], shuffledIdx[i]);
            }

            // Cosine-annealed learning rate with warmup (#4)
            int warmupEpochs = hp.RocketWarmupEpochs;
            double lr = epoch < warmupEpochs
                ? baseLr * (epoch + 1.0) / warmupEpochs
                : baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * (epoch - warmupEpochs) / (maxEpochs - warmupEpochs)));

            // Re-sample mixup partners and lambdas each epoch for maximum diversity
            if (useMixup)
            {
                for (int i = 0; i < trainN; i++)
                {
                    int j2     = mixRng!.Next(trainN);
                    double lam = SampleBeta(mixRng, hp.MixupAlpha);
                    var mixed  = new double[dim];
                    for (int j = 0; j < dim; j++)
                        mixed[j] = lam * features[i][j] + (1.0 - lam) * features[j2][j];
                    mixupFeatures![i] = mixed;
                    mixupLabels![i]   = lam * softLabels[i] + (1.0 - lam) * softLabels[j2];
                }
            }

            // Mini-batched training pass
            for (int bStart = 0; bStart < trainN; bStart += batchSize)
            {
                if (bStart % (batchSize * 20) == 0 && bStart > 0) ct.ThrowIfCancellationRequested();
                int bEnd = Math.Min(bStart + batchSize, trainN);
                int bLen = bEnd - bStart;

                // Accumulate gradients over the mini-batch
                var gW = new double[dim];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = shuffledIdx[bi];
                    double[] feat = useMixup ? mixupFeatures![si] : features[si];
                    double   yVal = useMixup ? mixupLabels![si]   : softLabels[si];

                    double logit = bias;
                    for (int j = 0; j < dim; j++) logit += w[j] * feat[j];
                    double p   = MLFeatureHelper.Sigmoid(logit);
                    double rcw = labels[si].Direction > 0 ? rocketCwBuy : rocketCwSell;
                    double err = (p - yVal) * temporalWeights[si] * rcw * trainN;

                    for (int j = 0; j < dim; j++)
                        gW[j] += err * feat[j] + l2 * w[j];
                    gBatch += err;
                }

                // Average gradients over the mini-batch
                double invBLen = 1.0 / bLen;
                for (int j = 0; j < dim; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                // #5: Gradient norm clipping
                if (hp.MaxGradNorm > 0)
                {
                    double gradNorm = 0;
                    for (int j = 0; j < dim; j++) gradNorm += gW[j] * gW[j];
                    gradNorm += gBatch * gBatch;
                    gradNorm = Math.Sqrt(gradNorm);
                    if (gradNorm > hp.MaxGradNorm)
                    {
                        double scale = hp.MaxGradNorm / gradNorm;
                        for (int j = 0; j < dim; j++) gW[j] *= scale;
                        gBatch *= scale;
                    }
                }

                // #21: Bias regularization
                if (hp.RocketRegularizeBias)
                    gBatch += l2 * bias;

                // Single Adam step per mini-batch
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;

                for (int j = 0; j < dim; j++)
                {
                    double g = gW[j];
                    mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * g;
                    vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * g * g;
                    double mHat = mW[j] / (1 - beta1t);
                    double vHat = vW[j] / (1 - beta2t);
                    w[j] -= lr * mHat / (Math.Sqrt(vHat) + AdamEpsilon);

                    if (hp.MaxWeightMagnitude > 0)
                        w[j] = Math.Clamp(w[j], -hp.MaxWeightMagnitude, hp.MaxWeightMagnitude);
                }

                mB = AdamBeta1 * mB + (1 - AdamBeta1) * gBatch;
                vB = AdamBeta2 * vB + (1 - AdamBeta2) * gBatch * gBatch;
                double mBHat = mB / (1 - beta1t);
                double vBHat = vB / (1 - beta2t);
                bias -= lr * mBHat / (Math.Sqrt(vBHat) + AdamEpsilon);
            }

            // SWA accumulation phase
            if (useSwa && epoch >= hp.SwaStartEpoch && (epoch - hp.SwaStartEpoch) % hp.SwaFrequency == 0)
            {
                swaCount++;
                for (int j = 0; j < dim; j++)
                    swaW[j] += (w[j] - swaW[j]) / swaCount;
                swaBias += (bias - swaBias) / swaCount;
            }

            // Validation loss (cross-entropy)
            double valLoss = 0;
            for (int i = trainN; i < n; i++)
            {
                double logit = bias;
                for (int j = 0; j < dim; j++) logit += w[j] * features[i][j];
                double p = MLFeatureHelper.Sigmoid(logit);
                double y = labels[i].Direction > 0 ? 1.0 : 0.0;
                valLoss += -(y * Math.Log(p + 1e-10) + (1 - y) * Math.Log(1 - p + 1e-10));
            }
            valLoss /= (n - trainN);

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, dim);
                bestBias = bias;
                patienceCounter = 0;
                earlyStopEpoch = epoch; // #25: track best epoch
            }
            else
            {
                patienceCounter++;
                if (patienceCounter >= patience) break;
            }
        }

        // Use SWA-averaged weights if accumulated enough checkpoints
        if (useSwa && swaCount >= 3)
        {
            // Validate SWA weights are better than best early-stopped weights
            double swaValLoss = 0;
            for (int i = trainN; i < n; i++)
            {
                double logit = swaBias;
                for (int j = 0; j < dim; j++) logit += swaW[j] * features[i][j];
                double p = MLFeatureHelper.Sigmoid(logit);
                double y = labels[i].Direction > 0 ? 1.0 : 0.0;
                swaValLoss += -(y * Math.Log(p + 1e-10) + (1 - y) * Math.Log(1 - p + 1e-10));
            }
            swaValLoss /= (n - trainN);

            if (swaValLoss <= bestValLoss)
            {
                Array.Copy(swaW, bestW, dim);
                bestBias = swaBias;
            }
        }

        return (bestW, bestBias, earlyStopEpoch, bestValLoss, swaCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Walk-forward cross-validation
    // ═══════════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  numKernels,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);

        if (foldSize < 50)
        {
            _logger.LogWarning("ROCKET walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[] foldResults;
        int cvKernelSeed = HashCode.Combine(samples.Count, featureCount, numKernels, samples[0].Direction);
        var rng = new Random(cvKernelSeed);
        var (kWeights, kDilations, kPaddings, kLengths, kChStarts, kChEnds) =
            GenerateKernels(numKernels, featureCount, rng, hp.RocketUseMiniWeights, hp.RocketMultivariate);

        // #8: Generate fold definitions (CPCV or expanding-window)
        var foldDefs = new List<(int TrainEnd, int TestStart, int TestEnd)>();
        if (hp.RocketUseCpcv)
        {
            // Combinatorial purged CV: all pairs of consecutive blocks
            int numBlocks = folds + 1;
            for (int a = 0; a < numBlocks; a++)
            for (int b = a + 1; b < numBlocks; b++)
            {
                int tStart = b * foldSize;
                int tEnd   = Math.Min((b + 1) * foldSize, samples.Count);
                // Train = everything except the test block, with purging
                int trnEnd = Math.Max(0, tStart - embargo - (MLFeatureHelper.LookbackWindow - 1));
                if (trnEnd >= hp.MinSamples && tEnd - tStart >= 20)
                    foldDefs.Add((trnEnd, tStart, tEnd));
            }
            if (foldDefs.Count == 0) // fallback to standard
                for (int f = 0; f < folds; f++)
                    foldDefs.Add((Math.Max(0, (f + 2) * foldSize - foldSize - embargo - (MLFeatureHelper.LookbackWindow - 1)),
                        (f + 2) * foldSize - foldSize, (f + 2) * foldSize));
        }
        else
        {
            for (int f = 0; f < folds; f++)
            {
                int tEnd = (f + 2) * foldSize;
                int tStart = tEnd - foldSize;
                int trnEnd = Math.Max(0, tStart - embargo - (MLFeatureHelper.LookbackWindow - 1));
                foldDefs.Add((trnEnd, tStart, tEnd));
            }
        }

        int actualFolds = foldDefs.Count;
        foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[actualFolds];

        Parallel.For(0, actualFolds, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = hp.RocketDeterministicParallel ? 1 : -1
        }, fold =>
        {
            var (trainEnd, testStart, testEnd) = foldDefs[fold];

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

            // Extract ROCKET features for this fold
            var foldTrainRocket = ExtractRocketFeatures(foldTrain, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);
            var foldTestRocket  = ExtractRocketFeatures(foldTest, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);

            int dim = 2 * numKernels;
            var (rm, rs) = ComputeRocketStandardization(foldTrainRocket, dim);
            StandardizeRocketInPlace(foldTrainRocket, rm, rs, dim);
            StandardizeRocketInPlace(foldTestRocket, rm, rs, dim);

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(50, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(5, hp.EarlyStoppingPatience / 2),
            };

            // #9: Split fold train into 80% train / 20% cal for per-fold Platt
            int foldCalStart = (int)(foldTrain.Count * 0.80);
            var foldTrainActual = foldTrain[..foldCalStart];
            var foldCal = foldTrain[foldCalStart..];

            var foldTrainActualRocket = ExtractRocketFeatures(foldTrainActual, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);
            var foldCalRocket = foldTrainRocket[foldCalStart..];
            foldTrainRocket = foldTrainRocket[..foldCalStart];

            var (rmT, rsT) = ComputeRocketStandardization(foldTrainActualRocket, dim);
            StandardizeRocketInPlace(foldTrainActualRocket, rmT, rsT, dim);
            StandardizeRocketInPlace(foldCalRocket, rmT, rsT, dim);
            // Re-standardize test with train-actual stats
            foldTestRocket = ExtractRocketFeatures(foldTest, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);
            StandardizeRocketInPlace(foldTestRocket, rmT, rsT, dim);

            var (w, b, _, _, _) = TrainRidgeAdam(foldTrainActualRocket, foldTrainActual, dim, cvHp, null, null, ct);

            // Fit Platt scaling on fold calibration slice
            double foldPlattA = 1.0, foldPlattB = 0.0;
            if (foldCal.Count >= 5)
                (foldPlattA, foldPlattB) = FitPlattScaling(foldCalRocket, foldCal, w, b, dim);

            var (mw, mb) = FitLinearRegressor(foldTrainActual, featureCount, ct);
            var m = EvaluateModel(foldTestRocket, foldTest, w, b, mw, mb, foldPlattA, foldPlattB, dim, featureCount);

            // Feature importance proxy: mean absolute weight contribution per original feature
            var foldImp = new double[featureCount];
            // Each kernel operates on the feature vector; approximate importance by
            // aggregating weight magnitudes across kernels that touch each feature position
            for (int j = 0; j < featureCount; j++)
            {
                double sumAbs = 0;
                for (int k = 0; k < numKernels; k++)
                {
                    int len = kLengths[k];
                    int dil = kDilations[k];
                    bool pad = kPaddings[k];
                    int padding = pad ? (len - 1) * dil / 2 : 0;

                    for (int li = 0; li < len; li++)
                    {
                        int srcIdx = j + li * dil - padding;
                        if (srcIdx == j)
                            sumAbs += Math.Abs(w[k]) * Math.Abs(kWeights[k][li]);
                    }
                }
                foldImp[j] = sumAbs;
            }
            double impSum = 0;
            for (int j = 0; j < featureCount; j++) impSum += foldImp[j];
            if (impSum > 1e-10)
                for (int j = 0; j < featureCount; j++) foldImp[j] /= impSum;

            // Equity-curve gate
            var foldPredictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                double logit = b;
                for (int j = 0; j < dim; j++) logit += w[j] * foldTestRocket[pi][j];
                double rawP = MLFeatureHelper.Sigmoid(logit);
                foldPredictions[pi] = (rawP >= 0.5 ? 1 : -1,
                                       foldTest[pi].Direction > 0 ? 1 : -1);
            }

            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldPredictions);

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown) isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe) isBadFold = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

        // Aggregate
        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
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

        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "ROCKET equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "ROCKET Sharpe trend gate: slope={Slope:F3} < threshold. Model rejected.", sharpeTrend);
            equityCurveGateFailed = true;
        }

        // Feature stability scores
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[featureCount];
            int foldCount = foldImportances.Count;
            for (int j = 0; j < featureCount; j++)
            {
                double sumImp = 0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImportances[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp = 0;
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
            AvgAccuracy:           avgAcc,
            StdAccuracy:           stdAcc,
            AvgF1:                 f1List.Average(),
            AvgEV:                 evList.Average(),
            AvgSharpe:             sharpeList.Average(),
            FoldCount:             accList.Count,
            SharpeTrend:           sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Meta-label secondary classifier
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim, int featureCount,
        CancellationToken ct = default)
    {
        int n = calRocket.Count;
        if (n < 10) return ([], 0.0);

        // 80/20 train/val split for early stopping
        int metaTrainN = (int)(n * 0.80);
        int metaValN   = n - metaTrainN;
        if (metaTrainN < 5) return ([], 0.0);

        // Features: [calibP, top-5 original feature values]
        int metaDim = 6;
        var metaW = new double[metaDim];
        double metaB = 0;

        // Adam state (#3: shared helper)
        var metaAdam = AdamState.Create(metaDim);

        int fLimit = Math.Min(5, featureCount);
        const double baseLr = 0.01;
        const int maxEpochs = 100;
        const int patience = 10;
        int batchSize = Math.Min(DefaultBatchSize, metaTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestMetaW = new double[metaDim];
        double bestMetaB = 0;

        var idx = new int[metaTrainN];
        for (int i = 0; i < metaTrainN; i++) idx[i] = i;
        var rng = new Random(metaTrainN ^ metaDim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training
            for (int bStart = 0; bStart < metaTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, metaTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[metaDim];
                double gB = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double rawP = RocketProb(calRocket[si], w, bias, dim);
                    bool predictedUp = rawP >= 0.5;
                    bool actualUp    = calSet[si].Direction == 1;
                    double metaLabel = predictedUp == actualUp ? 1.0 : 0.0;

                    double z = metaB + metaW[0] * rawP;
                    for (int j = 0; j < fLimit; j++)
                        z += metaW[j + 1] * calSet[si].Features[j];

                    double metaP = MLFeatureHelper.Sigmoid(z);
                    double err   = metaP - metaLabel;

                    gW[0] += err * rawP;
                    for (int j = 0; j < fLimit; j++)
                        gW[j + 1] += err * calSet[si].Features[j];
                    gB += err;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < metaDim; j++) gW[j] *= invBLen;
                gB *= invBLen;

                AdamState.AdamStep(ref metaAdam, gW, gB, metaW, ref metaB, lr, metaDim);
            }

            // Validation loss
            double valLoss = 0;
            for (int i = metaTrainN; i < n; i++)
            {
                double rawP = RocketProb(calRocket[i], w, bias, dim);
                bool predictedUp = rawP >= 0.5;
                bool actualUp    = calSet[i].Direction == 1;
                double metaLabel = predictedUp == actualUp ? 1.0 : 0.0;

                double z = metaB + metaW[0] * rawP;
                for (int j = 0; j < fLimit; j++)
                    z += metaW[j + 1] * calSet[i].Features[j];
                double metaP = MLFeatureHelper.Sigmoid(z);
                valLoss += -(metaLabel * Math.Log(metaP + 1e-10) + (1 - metaLabel) * Math.Log(1 - metaP + 1e-10));
            }
            valLoss /= metaValN;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(metaW, bestMetaW, metaDim);
                bestMetaB = metaB;
                patienceCounter = 0;
            }
            else
            {
                patienceCounter++;
                if (patienceCounter >= patience) break;
            }
        }

        return (bestMetaW, bestMetaB);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Abstention gate
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB,
        double[] metaLabelW, double metaLabelB, int dim,
        CancellationToken ct = default,
        int numKernels = 0)
    {
        int n = calRocket.Count;
        if (n < 10) return ([], 0.0, 0.5);

        // 80/20 train/val split for early stopping
        int absTrainN = (int)(n * 0.80);
        int absValN   = n - absTrainN;
        if (absTrainN < 5) return ([], 0.0, 0.5);

        // Features: [calibP, |calibP - 0.5|, metaLabelScore, kernelEntropy, |magPred|] (#13)
        int absDim = 5;
        var absW = new double[absDim];
        double absB = 0;

        // Adam state (#3: shared helper)
        var absAdam = AdamState.Create(absDim);

        const double baseLr = 0.01;
        const int maxEpochs = 100;
        const int patience = 10;
        int batchSize = Math.Min(DefaultBatchSize, absTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestAbsW = new double[absDim];
        double bestAbsB = 0;

        var idx = new int[absTrainN];
        for (int i = 0; i < absTrainN; i++) idx[i] = i;
        var rng = new Random(absTrainN ^ absDim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training
            for (int bStart = 0; bStart < absTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, absTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[absDim];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double calibP = CalibratedProb(calRocket[si], w, bias, plattA, plattB, dim);
                    bool predictedUp = calibP >= 0.5;
                    bool actualUp    = calSet[si].Direction == 1;
                    double label = predictedUp == actualUp ? 1.0 : 0.0;

                    double metaScore = 0;
                    if (metaLabelW.Length > 0)
                    {
                        double rawP = RocketProb(calRocket[si], w, bias, dim);
                        metaScore = metaLabelB + metaLabelW[0] * rawP;
                    }

                    // #13: 5 features — kernel PPV entropy + |logit| as magnitude confidence
                    double kernelEntropy = 0;
                    if (numKernels > 0)
                    {
                        for (int ki = 0; ki < Math.Min(numKernels, calRocket[si].Length - numKernels); ki++)
                        {
                            double ppv = Math.Clamp(calRocket[si][numKernels + ki], 1e-7, 1.0 - 1e-7);
                            kernelEntropy += -(ppv * Math.Log(ppv) + (1.0 - ppv) * Math.Log(1.0 - ppv));
                        }
                        kernelEntropy /= numKernels;
                    }
                    double magConf = Math.Abs(MLFeatureHelper.Logit(Math.Clamp(calibP, 1e-7, 1.0 - 1e-7)));
                    double[] feat = [calibP, Math.Abs(calibP - 0.5), metaScore, kernelEntropy, magConf];
                    double z = absB;
                    for (int j = 0; j < absDim; j++) z += absW[j] * feat[j];
                    double p   = MLFeatureHelper.Sigmoid(z);
                    double err = p - label;

                    for (int j = 0; j < absDim; j++)
                        gW[j] += err * feat[j];
                    gBatch += err;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < absDim; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref absAdam, gW, gBatch, absW, ref absB, lr, absDim);
            }

            // Validation loss
            double valLoss = 0;
            for (int i = absTrainN; i < n; i++)
            {
                double calibP = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
                bool predictedUp = calibP >= 0.5;
                bool actualUp    = calSet[i].Direction == 1;
                double label = predictedUp == actualUp ? 1.0 : 0.0;

                double metaScore = 0;
                if (metaLabelW.Length > 0)
                {
                    double rawP = RocketProb(calRocket[i], w, bias, dim);
                    metaScore = metaLabelB + metaLabelW[0] * rawP;
                }

                double kernelEntropyI = 0;
                if (numKernels > 0)
                {
                    for (int ki = 0; ki < Math.Min(numKernels, calRocket[i].Length - numKernels); ki++)
                    {
                        double ppv = Math.Clamp(calRocket[i][numKernels + ki], 1e-7, 1.0 - 1e-7);
                        kernelEntropyI += -(ppv * Math.Log(ppv) + (1.0 - ppv) * Math.Log(1.0 - ppv));
                    }
                    kernelEntropyI /= numKernels;
                }
                double magConfI = Math.Abs(MLFeatureHelper.Logit(Math.Clamp(calibP, 1e-7, 1.0 - 1e-7)));
                double[] feat = [calibP, Math.Abs(calibP - 0.5), metaScore, kernelEntropyI, magConfI];
                double z = absB;
                for (int j = 0; j < absDim; j++) z += absW[j] * feat[j];
                double p = MLFeatureHelper.Sigmoid(z);
                valLoss += -(label * Math.Log(p + 1e-10) + (1 - label) * Math.Log(1 - p + 1e-10));
            }
            valLoss /= absValN;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(absW, bestAbsW, absDim);
                bestAbsB = absB;
                patienceCounter = 0;
            }
            else
            {
                patienceCounter++;
                if (patienceCounter >= patience) break;
            }
        }

        absW = bestAbsW;
        absB = bestAbsB;

        // Sweep threshold for best accuracy on cal set
        double bestAcc = 0, bestThr = 0.5;
        for (int ti = 30; ti <= 70; ti++)
        {
            double thr = ti / 100.0;
            int correct = 0, total = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
                double metaScore = 0;
                if (metaLabelW.Length > 0)
                {
                    double rawP = RocketProb(calRocket[i], w, bias, dim);
                    metaScore = metaLabelB + metaLabelW[0] * rawP;
                }
                double kernelEntropyI = 0;
                if (numKernels > 0)
                {
                    for (int ki = 0; ki < Math.Min(numKernels, calRocket[i].Length - numKernels); ki++)
                    {
                        double ppv = Math.Clamp(calRocket[i][numKernels + ki], 1e-7, 1.0 - 1e-7);
                        kernelEntropyI += -(ppv * Math.Log(ppv) + (1.0 - ppv) * Math.Log(1.0 - ppv));
                    }
                    kernelEntropyI /= numKernels;
                }
                double magConfI = Math.Abs(MLFeatureHelper.Logit(Math.Clamp(calibP, 1e-7, 1.0 - 1e-7)));
                double[] feat = [calibP, Math.Abs(calibP - 0.5), metaScore, kernelEntropyI, magConfI];
                double z = absB;
                for (int j = 0; j < absDim; j++) z += absW[j] * feat[j];
                double absP = MLFeatureHelper.Sigmoid(z);
                if (absP >= thr)
                {
                    total++;
                    if ((calibP >= 0.5) == (calSet[i].Direction == 1)) correct++;
                }
            }
            double acc = total > 0 ? (double)correct / total : 0;
            if (acc > bestAcc) { bestAcc = acc; bestThr = thr; }
        }

        return (absW, absB, bestThr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET-space magnitude regressor (#14)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) TrainAdamRegressor(
        List<double[]> rocketFeatures, List<TrainingSample> labels, int dim,
        CancellationToken ct = default)
    {
        int n = rocketFeatures.Count;
        if (n < 5) return (new double[dim], 0.0);

        int magTrainN = (int)(n * 0.90);
        int magValN   = n - magTrainN;
        if (magTrainN < 5) magTrainN = n;

        var w = new double[dim];
        double b = 0;
        var adam = AdamState.Create(dim);

        const double baseLr = 0.01;
        const int maxEpochs = 200;
        const int patience = 15;
        const double huberDelta = 1.0;
        int batchSize = Math.Min(DefaultBatchSize, magTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestW = new double[dim];
        double bestB = 0;

        var idx = new int[magTrainN];
        for (int i = 0; i < magTrainN; i++) idx[i] = i;
        var rng = new Random(magTrainN ^ dim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            for (int bStart = 0; bStart < magTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, magTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[dim];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double pred = b;
                    for (int j = 0; j < dim; j++)
                        pred += w[j] * rocketFeatures[si][j];
                    double residual = pred - labels[si].Magnitude;
                    double grad = Math.Abs(residual) <= huberDelta ? residual : huberDelta * Math.Sign(residual);

                    for (int j = 0; j < dim; j++)
                        gW[j] += grad * rocketFeatures[si][j];
                    gBatch += grad;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < dim; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref adam, gW, gBatch, w, ref b, lr, dim);
            }

            if (magValN > 0)
            {
                double valLoss = 0;
                for (int i = magTrainN; i < n; i++)
                {
                    double pred = b;
                    for (int j = 0; j < dim; j++)
                        pred += w[j] * rocketFeatures[i][j];
                    double residual = Math.Abs(pred - labels[i].Magnitude);
                    valLoss += residual <= huberDelta
                        ? 0.5 * residual * residual
                        : huberDelta * (residual - 0.5 * huberDelta);
                }
                valLoss /= magValN;

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    Array.Copy(w, bestW, dim);
                    bestB = b;
                    patienceCounter = 0;
                }
                else
                {
                    patienceCounter++;
                    if (patienceCounter >= patience) break;
                }
            }
        }

        if (magValN > 0 && bestValLoss < double.MaxValue)
            return (bestW, bestB);
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Shared Adam helper (#3)
    // ═══════════════════════════════════════════════════════════════════════════

    private struct AdamState
    {
        public double[] MW;
        public double[] VW;
        public double MB;
        public double VB;
        public double Beta1T;
        public double Beta2T;
        public int T;

        public static AdamState Create(int dim)
            => new()
            {
                MW = new double[dim],
                VW = new double[dim],
                MB = 0, VB = 0,
                Beta1T = 1.0, Beta2T = 1.0,
                T = 0,
            };

        public static void AdamStep(
            ref AdamState state, double[] gradW, double gradB,
            double[] weights, ref double bias, double lr, int dim)
        {
            state.T++;
            state.Beta1T *= AdamBeta1;
            state.Beta2T *= AdamBeta2;

            for (int j = 0; j < dim; j++)
            {
                double g = gradW[j];
                state.MW[j] = AdamBeta1 * state.MW[j] + (1 - AdamBeta1) * g;
                state.VW[j] = AdamBeta2 * state.VW[j] + (1 - AdamBeta2) * g * g;
                double mHat = state.MW[j] / (1 - state.Beta1T);
                double vHat = state.VW[j] / (1 - state.Beta2T);
                weights[j] -= lr * mHat / (Math.Sqrt(vHat) + AdamEpsilon);
            }

            state.MB = AdamBeta1 * state.MB + (1 - AdamBeta1) * gradB;
            state.VB = AdamBeta2 * state.VB + (1 - AdamBeta2) * gradB * gradB;
            double mBHat = state.MB / (1 - state.Beta1T);
            double vBHat = state.VB / (1 - state.Beta2T);
            bias -= lr * mBHat / (Math.Sqrt(vBHat) + AdamEpsilon);
        }
    }
}
