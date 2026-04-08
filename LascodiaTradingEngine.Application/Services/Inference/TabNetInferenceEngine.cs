using System.Buffers;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for TabNet v3 (true TabNet: shared + step-specific Feature Transformer
/// with GLU, Attentive Transformer with Sparsemax, Batch Normalization).
/// Supports backward compatibility with v2 snapshots (simple attention-weighted linear model).
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class TabNetInferenceEngine : IModelInferenceEngine
{
    private const double BnEpsilon = 1e-5;

    public bool CanHandle(ModelSnapshot snapshot)
    {
        if (!TabNetSnapshotSupport.IsTabNet(snapshot))
            return false;

        var normalized = TabNetSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = TabNetSnapshotSupport.ValidateNormalizedSnapshot(normalized, allowLegacyV2: true);
        if (!validation.IsValid &&
            !(normalized.Weights is { Length: > 0 } && normalized.TabNetSharedWeights is not { Length: > 0 }))
        {
            return false;
        }

        return normalized.TabNetSharedWeights is { Length: > 0 } || normalized.Weights is { Length: > 0 };
    }

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var normalized = TabNetSnapshotSupport.NormalizeSnapshotCopy(snapshot);

        // ── v3 inference (true TabNet architecture) ───────────────────────
        if (normalized.Version == "3.0" && normalized.TabNetSharedWeights is { Length: > 0 })
            return RunV3Inference(features, featureCount, normalized);

        // ── v2 fallback (legacy simple attention model) ──────────────────
        return RunV2Inference(features, featureCount, normalized);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  V3 INFERENCE — True TabNet architecture
    //  Shared FC→BN→GLU + Step-specific FC→BN→GLU + Sparsemax attention
    // ═══════════════════════════════════════════════════════════════════════

    private static InferenceResult? RunV3Inference(float[] features, int featureCount, ModelSnapshot snapshot)
    {
        var validation = TabNetSnapshotSupport.ValidateNormalizedSnapshot(snapshot, allowLegacyV2: false);
        if (!validation.IsValid)
            return null;

        int F = Math.Min(featureCount, features.Length);
        int nSteps = snapshot.BaseLearnersK > 0 ? snapshot.BaseLearnersK : 3;
        int hiddenDim = snapshot.TabNetHiddenDim > 0 ? snapshot.TabNetHiddenDim : 24;
        double gamma = snapshot.TabNetRelaxationGamma > 0 ? snapshot.TabNetRelaxationGamma : 1.5;
        bool useSparsemax = snapshot.TabNetUseSparsemax;
        bool useGlu = snapshot.TabNetUseGlu;

        var sharedW  = snapshot.TabNetSharedWeights;
        var sharedB  = snapshot.TabNetSharedBiases;
        var sharedGW = snapshot.TabNetSharedGateWeights;
        var sharedGB = snapshot.TabNetSharedGateBiases;
        var stepW    = snapshot.TabNetStepFcWeights;
        var stepB    = snapshot.TabNetStepFcBiases;
        var stepGW   = snapshot.TabNetStepGateWeights;
        var stepGB   = snapshot.TabNetStepGateBiases;
        var attnFcW  = snapshot.TabNetAttentionFcWeights;
        var attnFcB  = snapshot.TabNetAttentionFcBiases;
        var bnGamma  = snapshot.TabNetBnGammas;
        var bnBeta   = snapshot.TabNetBnBetas;
        var bnMean   = snapshot.TabNetBnRunningMeans;
        var bnVar    = snapshot.TabNetBnRunningVars;
        var outputW  = snapshot.TabNetOutputHeadWeights;
        var initialBnFcW = snapshot.TabNetInitialBnFcW;
        var initialBnFcB = snapshot.TabNetInitialBnFcB;
        double outputB = snapshot.TabNetOutputHeadBias;

        if (sharedW is null || sharedB is null || sharedGW is null || sharedGB is null ||
            stepW is null || stepB is null || stepGW is null || stepGB is null ||
            attnFcW is null || attnFcB is null || bnGamma is null || bnBeta is null ||
            bnMean is null || bnVar is null || outputW is null)
            return null;

        int sharedLayers = sharedW.Length;
        int stepLayers = stepW.Length > 0 && stepW[0] is not null ? stepW[0].Length : 0;

        var pool = ArrayPool<double>.Shared;
        double[] priorScales = pool.Rent(Math.Max(1, F));
        double[] aggregatedH = pool.Rent(Math.Max(1, hiddenDim));
        double[] hPrev = pool.Rent(Math.Max(1, hiddenDim));
        double[] attnInput = pool.Rent(Math.Max(1, F));
        double[] attnLogits = pool.Rent(Math.Max(1, F));
        double[] masked = pool.Rent(Math.Max(1, F));
        double[] sparseScratch = pool.Rent(Math.Max(1, F));

        Array.Fill(priorScales, 1.0, 0, F);
        Array.Clear(aggregatedH, 0, hiddenDim);
        Array.Clear(hPrev, 0, hiddenDim);

        double meanStepEntropy = 0.0;

        try
        {
            for (int s = 0; s < nSteps; s++)
            {
                Array.Clear(attnInput, 0, F);

                if (s == 0)
                {
                    if (initialBnFcW is { Length: > 0 } &&
                        initialBnFcB is { Length: > 0 } &&
                        initialBnFcW.Length == F &&
                        initialBnFcB.Length >= F)
                    {
                        for (int j = 0; j < F; j++)
                        {
                            double val = initialBnFcB[j];
                            for (int k = 0; k < F && k < initialBnFcW[j].Length; k++)
                                val += initialBnFcW[j][k] * features[k];
                            attnInput[j] = val;
                        }
                    }
                    else
                    {
                        for (int j = 0; j < F; j++)
                            attnInput[j] = features[j];
                    }
                }
                else if (s < attnFcW.Length && attnFcW[s] is not null)
                {
                    for (int j = 0; j < F && j < attnFcW[s].Length; j++)
                    {
                        double val = s < attnFcB.Length ? attnFcB[s][j] : 0.0;
                        for (int k = 0; k < hiddenDim && k < attnFcW[s][j].Length; k++)
                            val += attnFcW[s][j][k] * hPrev[k];
                        attnInput[j] = val;
                    }
                }

                int bnIdx = s;
                if (bnIdx < bnGamma.Length)
                    ApplyBatchNormInPlace(attnInput, F, bnGamma[bnIdx], bnBeta[bnIdx], bnMean[bnIdx], bnVar[bnIdx]);

                for (int j = 0; j < F; j++)
                    attnLogits[j] = priorScales[j] * attnInput[j];

                if (useSparsemax)
                    SparsemaxInPlace(attnLogits, F, sparseScratch);
                else
                    SoftmaxInPlace(attnLogits, F);

                double stepEntropy = 0.0;
                for (int j = 0; j < F; j++)
                {
                    double att = attnLogits[j];
                    priorScales[j] = Math.Max(1e-6, priorScales[j] * (gamma - att));
                    masked[j] = features[j] * att;
                    if (att > 1e-12)
                        stepEntropy -= att * Math.Log(att);
                }
                meanStepEntropy += stepEntropy / Math.Max(1, nSteps);

                double[] h = masked;
                int inputDim = F;

                for (int l = 0; l < sharedLayers; l++)
                {
                    int bnSIdx = nSteps + l;
                    var hNew = FcBnGlu(h, inputDim, hiddenDim,
                        sharedW[l], sharedB[l], sharedGW[l], sharedGB[l],
                        bnSIdx < bnGamma.Length ? bnGamma[bnSIdx] : null,
                        bnSIdx < bnBeta.Length ? bnBeta[bnSIdx] : null,
                        bnSIdx < bnMean.Length ? bnMean[bnSIdx] : null,
                        bnSIdx < bnVar.Length ? bnVar[bnSIdx] : null,
                        useGlu);

                    if (l > 0 && h.Length == hiddenDim)
                        for (int j = 0; j < hiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;

                    h = hNew;
                    inputDim = hiddenDim;
                }

                for (int l = 0; l < stepLayers; l++)
                {
                    int bnStIdx = nSteps + sharedLayers + s * stepLayers + l;
                    var hNew = FcBnGlu(h, hiddenDim, hiddenDim,
                        stepW[s][l], stepB[s][l], stepGW[s][l], stepGB[s][l],
                        bnStIdx < bnGamma.Length ? bnGamma[bnStIdx] : null,
                        bnStIdx < bnBeta.Length ? bnBeta[bnStIdx] : null,
                        bnStIdx < bnMean.Length ? bnMean[bnStIdx] : null,
                        bnStIdx < bnVar.Length ? bnVar[bnStIdx] : null,
                        useGlu);

                    if (l > 0)
                        for (int j = 0; j < hiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;

                    h = hNew;
                }

                Array.Clear(hPrev, 0, hiddenDim);
                for (int j = 0; j < hiddenDim && j < h.Length; j++)
                {
                    aggregatedH[j] += Math.Max(h[j], 0.0);
                    hPrev[j] = h[j];
                }
            }

            double logit = outputB;
            for (int j = 0; j < hiddenDim && j < outputW.Length; j++)
                logit += outputW[j] * aggregatedH[j];

            double rawProb = MLFeatureHelper.Sigmoid(logit);
            double activationDistance = 0.0;
            if (snapshot.TabNetActivationCentroid is { Length: > 0 } centroid)
            {
                double sq = 0.0;
                for (int j = 0; j < hiddenDim && j < centroid.Length; j++)
                {
                    double d = aggregatedH[j] - centroid[j];
                    sq += d * d;
                }
                activationDistance = Math.Sqrt(sq);
            }

            double distanceScale = snapshot.TabNetActivationDistanceMean +
                                   2.0 * Math.Max(snapshot.TabNetActivationDistanceStd, 1e-6);
            double distanceUncertainty = distanceScale > 1e-6
                ? Math.Min(1.0, activationDistance / distanceScale)
                : 0.0;
            double entropyUncertainty = snapshot.TabNetAttentionEntropyThreshold > 1e-6
                ? Math.Min(1.0, meanStepEntropy / snapshot.TabNetAttentionEntropyThreshold)
                : 0.0;
            double marginProxy = 1.0 - Math.Min(1.0, 2.0 * Math.Abs(rawProb - 0.5));
            double calibrationUncertainty = snapshot.TabNetCalibrationResidualThreshold > 1e-6
                ? Math.Min(1.0, marginProxy / snapshot.TabNetCalibrationResidualThreshold)
                : marginProxy;
            double uncertainty = Math.Clamp(
                0.40 * distanceUncertainty +
                0.35 * entropyUncertainty +
                0.25 * calibrationUncertainty,
                0.0, 1.0);

            return new InferenceResult(rawProb, uncertainty);
        }
        finally
        {
            pool.Return(priorScales);
            pool.Return(aggregatedH);
            pool.Return(hPrev);
            pool.Return(attnInput);
            pool.Return(attnLogits);
            pool.Return(masked);
            pool.Return(sparseScratch);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  V2 FALLBACK — Legacy simple attention model
    // ═══════════════════════════════════════════════════════════════════════

    private static InferenceResult? RunV2Inference(float[] features, int featureCount, ModelSnapshot snapshot)
    {
        int nSteps = snapshot.Weights?.Length ?? 0;
        if (nSteps == 0 || snapshot.Weights is null) return null;

        int F = Math.Min(featureCount, features.Length);
        var priorScales = new double[F];
        Array.Fill(priorScales, 1.0);
        double stepOut = 0;

        for (int s = 0; s < nSteps; s++)
        {
            var attLogits = new double[F];
            for (int j = 0; j < F; j++)
            {
                double aw = s < snapshot.Weights.Length && j < snapshot.Weights[s].Length
                    ? snapshot.Weights[s][j] : 0.0;
                attLogits[j] = priorScales[j] * aw * features[j];
            }

            // Softmax
            double maxLogit = double.MinValue;
            for (int j = 0; j < F; j++)
                if (attLogits[j] > maxLogit) maxLogit = attLogits[j];
            double sumExp = 0;
            var attn = new double[F];
            for (int j = 0; j < F; j++)
            {
                attn[j] = Math.Exp(attLogits[j] - maxLogit);
                sumExp += attn[j];
            }
            if (sumExp > 1e-10)
                for (int j = 0; j < F; j++) attn[j] /= sumExp;

            for (int j = 0; j < F; j++)
                priorScales[j] = Math.Max(1e-6, priorScales[j] * (1.0 - attn[j]));

            double z = 0;
            for (int j = 0; j < F; j++)
            {
                double fw = s < snapshot.Weights.Length && j < snapshot.Weights[s].Length
                    ? snapshot.Weights[s][j] : 0.0;
                z += fw * features[j] * attn[j];
            }
            stepOut += Math.Max(z, 0.0);
        }

        return new InferenceResult(MLFeatureHelper.Sigmoid(stepOut), 0.0);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FC → BN → GLU BLOCK (inference mode)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FcBnGlu(
        double[] input, int inDim, int outDim,
        double[][] fcW, double[] fcB, double[][] gateW, double[] gateB,
        double[]? bnGamma, double[]? bnBeta, double[]? bnMean, double[]? bnVar, bool useGlu)
    {
        // Linear transform
        var linear = new double[outDim];
        for (int i = 0; i < outDim && i < fcW.Length; i++)
        {
            linear[i] = i < fcB.Length ? fcB[i] : 0;
            for (int j = 0; j < inDim && j < fcW[i].Length; j++)
                linear[i] += fcW[i][j] * input[j];
        }

        // BN (inference mode)
        if (bnGamma is not null && bnBeta is not null && bnMean is not null && bnVar is not null)
            linear = ApplyBatchNorm(linear, outDim, bnGamma, bnBeta, bnMean, bnVar);

        var gate = new double[outDim];
        if (useGlu)
        {
            for (int i = 0; i < outDim && i < gateW.Length; i++)
            {
                gate[i] = i < gateB.Length ? gateB[i] : 0;
                for (int j = 0; j < inDim && j < gateW[i].Length; j++)
                    gate[i] += gateW[i][j] * input[j];
                gate[i] = 1.0 / (1.0 + Math.Exp(-Math.Clamp(gate[i], -50, 50)));
            }
        }
        else
        {
            Array.Fill(gate, 1.0);
        }

        // GLU: linear ⊙ sigmoid(gate)
        var output = new double[outDim];
        for (int i = 0; i < outDim; i++)
            output[i] = linear[i] * gate[i];

        return output;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BATCH NORMALIZATION (inference mode — running stats)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ApplyBatchNorm(
        double[] input, int dim,
        double[] gamma, double[] beta, double[] runningMean, double[] runningVar)
    {
        var output = new double[dim];
        for (int i = 0; i < dim; i++)
        {
            double mean = i < runningMean.Length ? runningMean[i] : 0.0;
            double var_ = i < runningVar.Length ? runningVar[i] : 1.0;
            double g = i < gamma.Length ? gamma[i] : 1.0;
            double b = i < beta.Length ? beta[i] : 0.0;
            double xNorm = (input[i] - mean) / Math.Sqrt(var_ + BnEpsilon);
            output[i] = g * xNorm + b;
        }
        return output;
    }

    private static void ApplyBatchNormInPlace(
        double[] input, int dim,
        double[] gamma, double[] beta, double[] runningMean, double[] runningVar)
    {
        for (int i = 0; i < dim; i++)
        {
            double mean = i < runningMean.Length ? runningMean[i] : 0.0;
            double var_ = i < runningVar.Length ? runningVar[i] : 1.0;
            double g = i < gamma.Length ? gamma[i] : 1.0;
            double b = i < beta.Length ? beta[i] : 0.0;
            double xNorm = (input[i] - mean) / Math.Sqrt(var_ + BnEpsilon);
            input[i] = g * xNorm + b;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SPARSEMAX (Martins & Astudillo 2016)
    // ═══════════════════════════════════════════════════════════════════════

    private static void SparsemaxInPlace(double[] z, int len, double[] scratch)
    {
        for (int i = 0; i < len; i++) scratch[i] = z[i];
        Array.Sort(scratch, 0, len);
        Array.Reverse(scratch, 0, len);

        double cumSum = 0;
        int k = 0;
        for (int i = 0; i < len; i++)
        {
            cumSum += scratch[i];
            if (scratch[i] > (cumSum - 1.0) / (i + 1))
                k = i + 1;
            else
                break;
        }

        cumSum = 0;
        for (int i = 0; i < k; i++) cumSum += scratch[i];
        double tau = (cumSum - 1.0) / k;

        for (int i = 0; i < len; i++)
            z[i] = Math.Max(0, z[i] - tau);
    }

    private static void SoftmaxInPlace(double[] logits, int len)
    {
        double maxLogit = double.NegativeInfinity;
        for (int i = 0; i < len; i++)
            if (logits[i] > maxLogit) maxLogit = logits[i];

        double sumExp = 0.0;
        for (int i = 0; i < len; i++)
        {
            logits[i] = Math.Exp(logits[i] - maxLogit);
            sumExp += logits[i];
        }

        if (sumExp <= 1e-12)
            return;

        for (int i = 0; i < len; i++)
            logits[i] /= sumExp;
    }
}
