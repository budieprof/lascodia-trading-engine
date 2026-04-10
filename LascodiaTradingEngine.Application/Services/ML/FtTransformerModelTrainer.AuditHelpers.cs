using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class FtTransformerModelTrainer
{
    internal static double? ComputeRawProbabilityFromSnapshotForAudit(float[] features, ModelSnapshot snapshot)
    {
        if (!TryBuildModelFromSnapshotForAudit(snapshot, out var normalized, out var model))
            return null;

        int featureCount = normalized.Features.Length;
        var buf = new InferenceBuffers(featureCount, model.EmbedDim, model.NumHeads, model.FfnDim);
        return ForwardPass(features, model, featureCount, buf);
    }

    internal static double[]? ComputeOutputWeightGradientFromSnapshotForAudit(float[] features, ModelSnapshot snapshot, double label)
    {
        if (!TryComputeGradientFromSnapshotForAudit(features, snapshot, label, out _, out _, out var grad))
            return null;
        return (double[])grad.dWOut.Clone();
    }

    internal static double? ComputePosBiasGradientFromSnapshotForAudit(
        float[] features,
        ModelSnapshot snapshot,
        double label,
        int layerIndex,
        int headIndex,
        int offset)
    {
        if (!TryComputeGradientFromSnapshotForAudit(features, snapshot, label, out _, out var model, out var grad))
            return null;

        if (!model.UsePositionalBias ||
            layerIndex < 0 ||
            layerIndex >= model.NumLayers ||
            model.Layers[layerIndex].PosBias is null ||
            headIndex < 0 ||
            headIndex >= model.Layers[layerIndex].PosBias!.Length ||
            offset < 0 ||
            offset >= model.Layers[layerIndex].PosBias[headIndex].Length)
        {
            return null;
        }

        return grad.LayerGrads[layerIndex].dPosBias is { Length: > 0 }
            ? grad.LayerGrads[layerIndex].dPosBias[headIndex][offset]
            : null;
    }

    internal static double? ComputeFinalLayerNormGammaGradientFromSnapshotForAudit(
        float[] features,
        ModelSnapshot snapshot,
        double label,
        int index)
    {
        if (!TryComputeGradientFromSnapshotForAudit(features, snapshot, label, out _, out _, out var grad) ||
            index < 0 ||
            index >= grad.dGammaFinal.Length)
        {
            return null;
        }

        return grad.dGammaFinal[index];
    }

    internal static double? ComputeAttentionValueWeightGradientFromSnapshotForAudit(
        float[] features,
        ModelSnapshot snapshot,
        double label,
        int layerIndex,
        int rowIndex,
        int columnIndex)
    {
        if (!TryComputeGradientFromSnapshotForAudit(features, snapshot, label, out _, out var model, out var grad) ||
            layerIndex < 0 ||
            layerIndex >= model.NumLayers ||
            rowIndex < 0 ||
            rowIndex >= model.EmbedDim ||
            columnIndex < 0 ||
            columnIndex >= model.EmbedDim)
        {
            return null;
        }

        return grad.LayerGrads[layerIndex].dWv[rowIndex][columnIndex];
    }

    internal static double? ComputeFfnFirstLayerWeightGradientFromSnapshotForAudit(
        float[] features,
        ModelSnapshot snapshot,
        double label,
        int layerIndex,
        int rowIndex,
        int columnIndex)
    {
        if (!TryComputeGradientFromSnapshotForAudit(features, snapshot, label, out _, out var model, out var grad) ||
            layerIndex < 0 ||
            layerIndex >= model.NumLayers ||
            rowIndex < 0 ||
            rowIndex >= model.EmbedDim ||
            columnIndex < 0 ||
            columnIndex >= model.FfnDim)
        {
            return null;
        }

        return grad.LayerGrads[layerIndex].dWff1[rowIndex][columnIndex];
    }

    private static bool TryComputeGradientFromSnapshotForAudit(
        float[] features,
        ModelSnapshot snapshot,
        double label,
        out ModelSnapshot normalized,
        out TransformerModel model,
        out TransformerGrad grad)
    {
        if (!TryBuildModelFromSnapshotForAudit(snapshot, out normalized, out model))
        {
            grad = null!;
            return false;
        }

        int featureCount = normalized.Features.Length;
        var fwdBuf = new ForwardBuffers(featureCount, model.EmbedDim, model.NumHeads, model.FfnDim, model.NumLayers);
        grad = CreateAuditGradientBuffers(featureCount, model);
        double rawProb = ForwardPassTraining(features, model, featureCount, fwdBuf, new Random(1), 0.0);
        double err = rawProb - Math.Clamp(label, 0.0, 1.0);
        BackwardPass(err, model, featureCount, fwdBuf, features, grad, 0.0);
        return true;
    }

    private static TransformerGrad CreateAuditGradientBuffers(int featureCount, TransformerModel model)
    {
        var grad = new TransformerGrad(featureCount, model.EmbedDim, model.FfnDim, model.NumLayers);
        if (!model.UsePositionalBias)
            return grad;

        int seqSq = model.SeqLen * model.SeqLen;
        for (int index = 0; index < model.NumLayers; index++)
        {
            grad.LayerGrads[index].dPosBias = new double[model.NumHeads][];
            for (int head = 0; head < model.NumHeads; head++)
                grad.LayerGrads[index].dPosBias[head] = new double[seqSq];
        }

        return grad;
    }

    private static bool TryBuildModelFromSnapshotForAudit(
        ModelSnapshot snapshot,
        out ModelSnapshot normalized,
        out TransformerModel model)
    {
        normalized = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = FtTransformerSnapshotSupport.ValidateNormalizedSnapshot(normalized);
        if (!validation.IsValid)
        {
            model = null!;
            return false;
        }

        int featureCount = normalized.Features.Length;
        int embedDim = normalized.FtTransformerEmbedDim;
        int numHeads = normalized.FtTransformerNumHeads > 0 ? normalized.FtTransformerNumHeads : 1;
        int ffnDim = normalized.FtTransformerFfnDim > 0 ? normalized.FtTransformerFfnDim : embedDim * 4;
        int numLayers = normalized.FtTransformerNumLayers > 0 ? normalized.FtTransformerNumLayers : 1;
        bool usePositionalBias = normalized.FtTransformerPosBias is { Length: > 0 };

        model = new TransformerModel(featureCount, embedDim, numHeads, ffnDim, numLayers)
        {
            UsePositionalBias = usePositionalBias
        };

        if (usePositionalBias)
        {
            int seqSq = model.SeqLen * model.SeqLen;
            for (int layerIndex = 0; layerIndex < model.NumLayers; layerIndex++)
            {
                model.Layers[layerIndex].PosBias = new double[model.NumHeads][];
                for (int headIndex = 0; headIndex < model.NumHeads; headIndex++)
                    model.Layers[layerIndex].PosBias[headIndex] = new double[seqSq];
            }
        }

        LoadWarmStartWeights(
            model,
            normalized,
            featureCount,
            embedDim,
            ffnDim,
            new Random(normalized.TrainingRandomSeed > 0 ? normalized.TrainingRandomSeed : 1));
        return true;
    }
}
