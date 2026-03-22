using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for TabNet (attention-based tabular) models.
/// Implements the sequential attention-mask decision steps: for each step,
/// compute a sparsemax-like softmax attention over features, mask the input,
/// and accumulate ReLU-gated step outputs into a final logit.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class TabNetInferenceEngine : IModelInferenceEngine
{
    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "TABNET"
        && snapshot.Weights is { Length: > 0 }
        && snapshot.Biases is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        int nSteps = snapshot.Weights.Length;
        if (nSteps == 0) return null;

        // Weights[s] = attention weights for step s (length F)
        // Biases[s]  = step FC weight for step s
        // The final output weight and bias are derived from the snapshot:
        // wOut is embedded as the per-step Biases, and the global
        // output is accumulated from step contributions.
        int F = Math.Min(featureCount, features.Length);

        double rawProb = TabNetRawProb(features, snapshot.Weights, snapshot.Weights, F, nSteps);

        return new InferenceResult(rawProb, 0.0);
    }

    private static double TabNetRawProb(
        float[] x, double[][] wAttention, double[][] wf, int F, int nSteps)
    {
        var priorScales = new double[F];
        Array.Fill(priorScales, 1.0);
        double stepOut = 0;

        for (int s = 0; s < nSteps; s++)
        {
            // Compute attention logits
            var attLogits = new double[F];
            for (int j = 0; j < F; j++)
            {
                double aw = s < wAttention.Length && j < wAttention[s].Length ? wAttention[s][j] : 0.0;
                attLogits[j] = priorScales[j] * aw * x[j];
            }

            // Softmax mask
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

            // Update prior scales
            for (int j = 0; j < F; j++)
                priorScales[j] = Math.Max(1e-6, priorScales[j] * (1.0 - attn[j]));

            // Step FC: z = Σ wf[s][j] × x[j] × attn[j]
            double z = 0;
            for (int j = 0; j < F; j++)
            {
                double fw = s < wf.Length && j < wf[s].Length ? wf[s][j] : 0.0;
                z += fw * x[j] * attn[j];
            }

            // ReLU accumulation
            stepOut += Math.Max(z, 0.0);
        }

        return MLFeatureHelper.Sigmoid(stepOut);
    }
}
