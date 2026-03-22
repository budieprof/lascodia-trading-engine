using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for TCN (Temporal Convolutional Network) models.
/// Deserialises TCN snapshot weights, builds sequence features, runs the
/// causal dilated conv forward pass, and supports MC-Dropout uncertainty.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class TcnInferenceEngine : IModelInferenceEngine
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const double DropoutRate = 0.1;
    private const int MaxMcDropoutSamples = 10;

    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "TCN"
        && !string.IsNullOrEmpty(snapshot.ConvWeightsJson)
        && string.Compare(snapshot.Version, "5.0", StringComparison.Ordinal) >= 0;

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var tcnResult = RunTcnForwardPass(snapshot, candleWindow);
        if (tcnResult is null)
            return null;

        var (rawProb, tcnSnap, seqStd) = tcnResult.Value;
        double ensembleStd = 0.0;

        decimal? mcMean = null, mcVar = null;
        if (mcDropoutSamples > 0)
        {
            int samples = Math.Min(mcDropoutSamples, MaxMcDropoutSamples);
            (mcMean, mcVar) = ComputeTcnMcDropout(seqStd, tcnSnap, samples, mcDropoutSeed);
            if (mcVar.HasValue)
                ensembleStd = Math.Sqrt((double)mcVar.Value);
        }

        return new InferenceResult(rawProb, ensembleStd, mcMean, mcVar);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TCN forward pass
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (double Probability, TcnModelTrainer.TcnSnapshotWeights TcnSnap, float[][] SeqStd)?
        RunTcnForwardPass(ModelSnapshot snap, List<Candle> candleWindow)
    {
        var tcnSnap = JsonSerializer.Deserialize<TcnModelTrainer.TcnSnapshotWeights>(
            snap.ConvWeightsJson!, JsonOptions);

        if (tcnSnap?.ConvW is null || tcnSnap.HeadW is null || tcnSnap.HeadB is null)
            return null;

        var seqRaw = MLFeatureHelper.BuildSequenceFeatures(candleWindow);
        var seqStd = snap.SeqMeans.Length > 0 && snap.SeqStds.Length > 0
            ? MLFeatureHelper.StandardizeSequence(seqRaw, snap.SeqMeans, snap.SeqStds)
            : seqRaw;

        int channelIn = tcnSnap.ChannelIn > 0 ? tcnSnap.ChannelIn : seqStd[0].Length;
        int numBlocks = tcnSnap.ConvW.Length;
        int filters = tcnSnap.Filters > 0 ? tcnSnap.Filters : 32;
        var blockInC = new int[numBlocks];
        for (int b = 0; b < numBlocks; b++) blockInC[b] = b == 0 ? channelIn : filters;

        var resW = tcnSnap.ResW ?? new double[]?[numBlocks];
        var dilations = TcnModelTrainer.BuildDilations(numBlocks);
        var activation = (TcnActivation)tcnSnap.Activation;

        bool useAttn = tcnSnap.UseAttentionPooling
                       && tcnSnap.AttnQueryW?.Length > 0
                       && tcnSnap.AttnKeyW?.Length > 0
                       && tcnSnap.AttnValueW?.Length > 0;

        double[] h = useAttn
            ? TcnModelTrainer.CausalConvForwardWithAttention(
                seqStd, tcnSnap.ConvW, tcnSnap.ConvB, resW, blockInC,
                filters, numBlocks, dilations,
                tcnSnap.UseLayerNorm, tcnSnap.LayerNormGamma, tcnSnap.LayerNormBeta, activation,
                tcnSnap.AttnQueryW!, tcnSnap.AttnKeyW!, tcnSnap.AttnValueW!)
            : TcnModelTrainer.CausalConvForwardFull(
                seqStd, tcnSnap.ConvW, tcnSnap.ConvB, resW, blockInC,
                filters, numBlocks, dilations,
                tcnSnap.UseLayerNorm, tcnSnap.LayerNormGamma, tcnSnap.LayerNormBeta, activation);

        double prob = TcnHeadProbability(h, tcnSnap.HeadW, tcnSnap.HeadB, filters);
        return (prob, tcnSnap, seqStd);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TCN softmax head
    // ═══════════════════════════════════════════════════════════════════════════

    private static double TcnHeadProbability(double[] h, double[] headW, double[] headB, int filters)
    {
        double logit0 = headB[0], logit1 = headB[1];
        for (int fi = 0; fi < filters && fi < h.Length; fi++)
        {
            logit0 += headW[fi] * h[fi];
            logit1 += headW[filters + fi] * h[fi];
        }
        double maxL = Math.Max(logit0, logit1);
        double e0 = Math.Exp(logit0 - maxL), e1 = Math.Exp(logit1 - maxL);
        return e1 / (e0 + e1);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TCN MC-Dropout
    // ═══════════════════════════════════════════════════════════════════════════

    private static (decimal Mean, decimal Variance) ComputeTcnMcDropout(
        float[][] seqStd, TcnModelTrainer.TcnSnapshotWeights tcnSnap,
        int numSamples, int seed)
    {
        var rng = new Random(seed);
        double scale = 1.0 / (1.0 - DropoutRate);

        int seqT = seqStd.Length;
        int channelIn = tcnSnap.ChannelIn > 0 ? tcnSnap.ChannelIn : seqStd[0].Length;
        int numBlocks = tcnSnap.ConvW!.Length;
        int filters = tcnSnap.Filters > 0 ? tcnSnap.Filters : 32;
        var blockInC = new int[numBlocks];
        for (int b = 0; b < numBlocks; b++) blockInC[b] = b == 0 ? channelIn : filters;
        var resW = tcnSnap.ResW ?? new double[]?[numBlocks];
        var dilations = TcnModelTrainer.BuildDilations(numBlocks);
        var activation = (TcnActivation)tcnSnap.Activation;
        bool useAttn = tcnSnap.UseAttentionPooling
                       && tcnSnap.AttnQueryW?.Length > 0
                       && tcnSnap.AttnKeyW?.Length > 0
                       && tcnSnap.AttnValueW?.Length > 0;

        var samples = new double[numSamples];
        for (int s = 0; s < numSamples; s++)
        {
            var maskedSeq = new float[seqT][];
            for (int t = 0; t < seqT; t++)
            {
                maskedSeq[t] = new float[seqStd[t].Length];
                for (int c = 0; c < seqStd[t].Length; c++)
                    maskedSeq[t][c] = rng.NextDouble() >= DropoutRate
                        ? (float)(seqStd[t][c] * scale)
                        : 0f;
            }

            double[] h = useAttn
                ? TcnModelTrainer.CausalConvForwardWithAttention(
                    maskedSeq, tcnSnap.ConvW, tcnSnap.ConvB, resW, blockInC,
                    filters, numBlocks, dilations,
                    tcnSnap.UseLayerNorm, tcnSnap.LayerNormGamma, tcnSnap.LayerNormBeta, activation,
                    tcnSnap.AttnQueryW!, tcnSnap.AttnKeyW!, tcnSnap.AttnValueW!)
                : TcnModelTrainer.CausalConvForwardFull(
                    maskedSeq, tcnSnap.ConvW, tcnSnap.ConvB, resW, blockInC,
                    filters, numBlocks, dilations,
                    tcnSnap.UseLayerNorm, tcnSnap.LayerNormGamma, tcnSnap.LayerNormBeta, activation);

            samples[s] = TcnHeadProbability(h, tcnSnap.HeadW!, tcnSnap.HeadB!, filters);
        }

        double mean = samples.Average();
        double variance = 0.0;
        for (int s = 0; s < numSamples; s++)
        {
            double d = samples[s] - mean;
            variance += d * d;
        }
        variance /= numSamples > 1 ? numSamples - 1 : 1;

        return ((decimal)mean, (decimal)variance);
    }
}
