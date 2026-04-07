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

    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "TABNET"
        && (snapshot.TabNetSharedWeights is { Length: > 0 }  // v3
            || snapshot.Weights is { Length: > 0 });          // v2 backward compat

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        // ── v3 inference (true TabNet architecture) ───────────────────────
        if (snapshot.Version == "3.0" && snapshot.TabNetSharedWeights is { Length: > 0 })
            return RunV3Inference(features, featureCount, snapshot);

        // ── v2 fallback (legacy simple attention model) ──────────────────
        return RunV2Inference(features, featureCount, snapshot);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  V3 INFERENCE — True TabNet architecture
    //  Shared FC→BN→GLU + Step-specific FC→BN→GLU + Sparsemax attention
    // ═══════════════════════════════════════════════════════════════════════

    private static InferenceResult? RunV3Inference(float[] features, int featureCount, ModelSnapshot snapshot)
    {
        int F = Math.Min(featureCount, features.Length);
        int nSteps = snapshot.BaseLearnersK > 0 ? snapshot.BaseLearnersK : 3;
        int hiddenDim = snapshot.TabNetHiddenDim > 0 ? snapshot.TabNetHiddenDim : 24;
        double gamma = snapshot.TabNetRelaxationGamma > 0 ? snapshot.TabNetRelaxationGamma : 1.5;

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
        double outputB = snapshot.TabNetOutputHeadBias;

        if (sharedW is null || sharedB is null || sharedGW is null || sharedGB is null ||
            stepW is null || stepB is null || stepGW is null || stepGB is null ||
            attnFcW is null || attnFcB is null || bnGamma is null || bnBeta is null ||
            bnMean is null || bnVar is null || outputW is null)
            return null;

        int sharedLayers = sharedW.Length;
        int stepLayers = stepW.Length > 0 && stepW[0] is not null ? stepW[0].Length : 0;

        var priorScales = new double[F];
        Array.Fill(priorScales, 1.0);
        var aggregatedH = new double[hiddenDim];
        var hPrev = new double[hiddenDim];

        for (int s = 0; s < nSteps; s++)
        {
            // ── 1. Attentive Transformer: FC → BN → Sparsemax ────────
            var attnInput = new double[F];
            if (s == 0)
            {
                for (int j = 0; j < F; j++) attnInput[j] = features[j];
            }
            else if (s < attnFcW.Length && attnFcW[s] is not null)
            {
                for (int j = 0; j < F && j < attnFcW[s].Length; j++)
                {
                    double val = s < attnFcB.Length ? attnFcB[s][j] : 0;
                    for (int k = 0; k < hiddenDim && k < attnFcW[s][j].Length; k++)
                        val += attnFcW[s][j][k] * hPrev[k];
                    attnInput[j] = val;
                }
            }

            // BN (inference mode: use running stats)
            int bnIdx = s;
            if (bnIdx < bnGamma.Length)
                attnInput = ApplyBatchNorm(attnInput, F, bnGamma[bnIdx], bnBeta[bnIdx],
                    bnMean[bnIdx], bnVar[bnIdx]);

            // Apply prior scales
            var attnLogits = new double[F];
            for (int j = 0; j < F; j++)
                attnLogits[j] = priorScales[j] * attnInput[j];

            // Sparsemax
            double[] attn = Sparsemax(attnLogits, F);

            // ── 2. Prior scale update ────────────────────────────────
            for (int j = 0; j < F; j++)
                priorScales[j] = Math.Max(1e-6, priorScales[j] * (gamma - attn[j]));

            // ── 3. Mask input ────────────────────────────────────────
            var masked = new double[F];
            for (int j = 0; j < F; j++)
                masked[j] = features[j] * attn[j];

            // ── 4. Shared FC→BN→GLU blocks ──────────────────────────
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
                    bnSIdx < bnVar.Length ? bnVar[bnSIdx] : null);

                if (l > 0 && h.Length == hiddenDim)
                    for (int j = 0; j < hiddenDim; j++)
                        hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;

                h = hNew;
                inputDim = hiddenDim;
            }

            // ── 5. Step-specific FC→BN→GLU blocks ───────────────────
            for (int l = 0; l < stepLayers; l++)
            {
                int bnStIdx = nSteps + sharedLayers + s * stepLayers + l;
                var hNew = FcBnGlu(h, hiddenDim, hiddenDim,
                    stepW[s][l], stepB[s][l], stepGW[s][l], stepGB[s][l],
                    bnStIdx < bnGamma.Length ? bnGamma[bnStIdx] : null,
                    bnStIdx < bnBeta.Length ? bnBeta[bnStIdx] : null,
                    bnStIdx < bnMean.Length ? bnMean[bnStIdx] : null,
                    bnStIdx < bnVar.Length ? bnVar[bnStIdx] : null);

                if (l > 0)
                    for (int j = 0; j < hiddenDim; j++)
                        hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;

                h = hNew;
            }

            // ── 6. ReLU gate and aggregate ───────────────────────────
            for (int j = 0; j < hiddenDim && j < h.Length; j++)
                aggregatedH[j] += Math.Max(h[j], 0.0);

            hPrev = h;
        }

        // ── 7. Output head: FC → sigmoid ─────────────────────────────
        double logit = outputB;
        for (int j = 0; j < hiddenDim && j < outputW.Length; j++)
            logit += outputW[j] * aggregatedH[j];

        double rawProb = MLFeatureHelper.Sigmoid(logit);
        return new InferenceResult(rawProb, 0.0);
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
        double[]? bnGamma, double[]? bnBeta, double[]? bnMean, double[]? bnVar)
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

        // Gate transform → sigmoid
        var gate = new double[outDim];
        for (int i = 0; i < outDim && i < gateW.Length; i++)
        {
            gate[i] = i < gateB.Length ? gateB[i] : 0;
            for (int j = 0; j < inDim && j < gateW[i].Length; j++)
                gate[i] += gateW[i][j] * input[j];
            gate[i] = 1.0 / (1.0 + Math.Exp(-Math.Clamp(gate[i], -50, 50)));
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

    // ═══════════════════════════════════════════════════════════════════════
    //  SPARSEMAX (Martins & Astudillo 2016)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] Sparsemax(double[] z, int len)
    {
        var sorted = new double[len];
        for (int i = 0; i < len; i++) sorted[i] = z[i];
        Array.Sort(sorted);
        Array.Reverse(sorted);

        double cumSum = 0;
        int k = 0;
        for (int i = 0; i < len; i++)
        {
            cumSum += sorted[i];
            if (sorted[i] > (cumSum - 1.0) / (i + 1))
                k = i + 1;
            else
                break;
        }

        cumSum = 0;
        for (int i = 0; i < k; i++) cumSum += sorted[i];
        double tau = (cumSum - 1.0) / k;

        var output = new double[len];
        for (int i = 0; i < len; i++)
            output[i] = Math.Max(0, z[i] - tau);

        return output;
    }
}
