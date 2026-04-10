using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class TcnSnapshotSupport
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 64 };

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames)
    {
        var builder = new StringBuilder("tcn-feature-schema|");
        builder.Append(MLFeatureHelper.FeatureCount).Append('|')
            .Append(MLFeatureHelper.SequenceChannelCount).Append('|')
            .Append(MLFeatureHelper.LookbackWindow).Append('|');

        for (int i = 0; i < featureNames.Length; i++)
            builder.Append(featureNames[i]).Append('|');
        for (int i = 0; i < MLFeatureHelper.SequenceChannelNames.Length; i++)
            builder.Append(MLFeatureHelper.SequenceChannelNames[i]).Append('|');

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(bool[]? activeChannelMask)
    {
        var builder = new StringBuilder("tcn-preprocessing|");
        builder.Append(MLFeatureHelper.LookbackWindow).Append('|')
            .Append(MLFeatureHelper.SequenceChannelCount).Append('|');

        bool[] mask = activeChannelMask is { Length: > 0 }
            ? activeChannelMask
            : Enumerable.Repeat(true, MLFeatureHelper.SequenceChannelCount).ToArray();

        for (int i = 0; i < mask.Length; i++)
            builder.Append(mask[i] ? '1' : '0');

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputeTrainerFingerprint(
        TrainingHyperparams hp,
        int filters,
        int numBlocks,
        bool useLayerNorm,
        bool useAttentionPool,
        TcnActivation activation,
        int attentionHeads)
    {
        string payload = JsonSerializer.Serialize(new
        {
            filters,
            numBlocks,
            useLayerNorm,
            useAttentionPool,
            activation = activation.ToString(),
            attentionHeads,
            hp.LearningRate,
            hp.L2Lambda,
            hp.MaxEpochs,
            hp.EarlyStoppingPatience,
            hp.LabelSmoothing,
            hp.TemporalDecayLambda,
            hp.AgeDecayLambda,
            hp.FitTemperatureScale,
            hp.ConformalCoverage,
            hp.ThresholdSearchMin,
            hp.ThresholdSearchMax,
            hp.ThresholdSearchStepBps,
            hp.TcnUseLayerNorm,
            hp.TcnUseAttentionPooling,
            hp.TcnActivation,
            hp.TcnAttentionHeads,
            hp.TcnWarmupEpochs,
            hp.TcnGradientNoiseStd,
            hp.TcnHeadLrScale,
            hp.TcnAttentionLrScale,
            hp.TcnProgressiveUnfreezeEpochs,
            hp.UseIncrementalUpdate,
            hp.UseCovariateShiftWeights,
            hp.DensityRatioWindowDays,
            hp.BarsPerDay,
        }, JsonOptions);

        return ComputeSha256(payload);
    }

    internal static int ComputeTrainingRandomSeed(
        string featureSchemaFingerprint,
        string trainerFingerprint,
        int sampleCount)
    {
        string payload = $"tcn-seed-v1|{featureSchemaFingerprint}|{trainerFingerprint}|{sampleCount}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        int seed = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return seed == 0 ? 1 : seed;
    }

    internal static string[] ResolveChannelNames(ModelSnapshot snapshot, int channelCount = MLFeatureHelper.SequenceChannelCount)
    {
        var names = new string[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            string fallback = i < MLFeatureHelper.SequenceChannelNames.Length ? MLFeatureHelper.SequenceChannelNames[i] : $"ch{i}";
            names[i] = snapshot.TcnChannelNames is { Length: > 0 } persisted &&
                       i < persisted.Length &&
                       !string.IsNullOrWhiteSpace(persisted[i])
                ? persisted[i]
                : fallback;
        }

        return names;
    }

    internal static bool[] ResolveActiveChannelMask(ModelSnapshot snapshot, int channelCount = MLFeatureHelper.SequenceChannelCount)
    {
        var resolved = new bool[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            resolved[i] =
                snapshot.TcnActiveChannelMask is { Length: > 0 } tcnMask && i < tcnMask.Length ? tcnMask[i] :
                snapshot.ActiveFeatureMask is { Length: > 0 } featureMask && i < featureMask.Length ? featureMask[i] :
                true;
        }

        return resolved;
    }

    internal static double[] ResolveChannelImportanceScores(ModelSnapshot snapshot, int channelCount = MLFeatureHelper.SequenceChannelCount)
    {
        var resolved = new double[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            double value =
                snapshot.TcnChannelImportanceScores is { Length: > 0 } tcnImp && i < tcnImp.Length ? tcnImp[i] :
                snapshot.FeatureImportanceScores is { Length: > 0 } featureImp && i < featureImp.Length ? featureImp[i] :
                0.0;
            resolved[i] = double.IsFinite(value) ? value : 0.0;
        }

        return resolved;
    }

    internal static TcnCalibrationArtifact ResolveCalibrationArtifact(ModelSnapshot snapshot)
    {
        if (snapshot.TcnCalibrationArtifact is not null)
        {
            return new TcnCalibrationArtifact
            {
                SelectedGlobalCalibration = snapshot.TcnCalibrationArtifact.SelectedGlobalCalibration,
                CalibrationSelectionStrategy = snapshot.TcnCalibrationArtifact.CalibrationSelectionStrategy,
                GlobalPlattA = snapshot.TcnCalibrationArtifact.GlobalPlattA,
                GlobalPlattB = snapshot.TcnCalibrationArtifact.GlobalPlattB,
                TemperatureScale = snapshot.TcnCalibrationArtifact.TemperatureScale,
                BuyBranchPlattA = snapshot.TcnCalibrationArtifact.BuyBranchPlattA,
                BuyBranchPlattB = snapshot.TcnCalibrationArtifact.BuyBranchPlattB,
                SellBranchPlattA = snapshot.TcnCalibrationArtifact.SellBranchPlattA,
                SellBranchPlattB = snapshot.TcnCalibrationArtifact.SellBranchPlattB,
                ConditionalRoutingThreshold = snapshot.TcnCalibrationArtifact.ConditionalRoutingThreshold,
                IsotonicBreakpoints = snapshot.TcnCalibrationArtifact.IsotonicBreakpoints?.ToArray() ?? [],
                OptimalThreshold = snapshot.TcnCalibrationArtifact.OptimalThreshold,
                ConformalQHat = snapshot.TcnCalibrationArtifact.ConformalQHat,
                CalibrationSampleCount = snapshot.TcnCalibrationArtifact.CalibrationSampleCount,
                DiagnosticsSampleCount = snapshot.TcnCalibrationArtifact.DiagnosticsSampleCount,
                BuyBranchSampleCount = snapshot.TcnCalibrationArtifact.BuyBranchSampleCount,
                SellBranchSampleCount = snapshot.TcnCalibrationArtifact.SellBranchSampleCount,
                IsotonicSampleCount = snapshot.TcnCalibrationArtifact.IsotonicSampleCount,
                IsotonicBreakpointCount = snapshot.TcnCalibrationArtifact.IsotonicBreakpointCount,
            };
        }

        return new TcnCalibrationArtifact
        {
            SelectedGlobalCalibration = snapshot.TemperatureScale > 0.0 ? "TEMPERATURE" : "PLATT",
            GlobalPlattA = snapshot.PlattA,
            GlobalPlattB = snapshot.PlattB,
            TemperatureScale = snapshot.TemperatureScale,
            BuyBranchPlattA = snapshot.PlattABuy,
            BuyBranchPlattB = snapshot.PlattBBuy,
            SellBranchPlattA = snapshot.PlattASell,
            SellBranchPlattB = snapshot.PlattBSell,
            ConditionalRoutingThreshold = snapshot.ConditionalCalibrationRoutingThreshold,
            IsotonicBreakpoints = snapshot.IsotonicBreakpoints?.ToArray() ?? [],
            OptimalThreshold = snapshot.OptimalThreshold > 0.0 ? snapshot.OptimalThreshold : 0.5,
            ConformalQHat = snapshot.ConformalQHat > 0.0 ? snapshot.ConformalQHat : 1.0,
        };
    }

    internal static (bool IsValid, string[] Issues) ValidateSnapshot(
        ModelSnapshot snapshot,
        TcnModelTrainer.TcnSnapshotWeights? weights = null,
        int expectedTimeSteps = MLFeatureHelper.LookbackWindow,
        int expectedChannelCount = MLFeatureHelper.SequenceChannelCount)
    {
        var issues = new List<string>();

        if (!string.Equals(snapshot.Type, "TCN", StringComparison.OrdinalIgnoreCase))
            issues.Add($"Snapshot type must be TCN, found '{snapshot.Type}'.");
        if (string.IsNullOrWhiteSpace(snapshot.ConvWeightsJson))
            issues.Add("ConvWeightsJson is missing.");
        if (snapshot.SeqMeans.Length != expectedChannelCount)
            issues.Add($"SeqMeans length mismatch: expected {expectedChannelCount}, found {snapshot.SeqMeans.Length}.");
        if (snapshot.SeqStds.Length != expectedChannelCount)
            issues.Add($"SeqStds length mismatch: expected {expectedChannelCount}, found {snapshot.SeqStds.Length}.");
        if (ResolveChannelNames(snapshot, expectedChannelCount).Length != expectedChannelCount)
            issues.Add("Resolved TCN channel names do not match the expected channel count.");
        if (ResolveActiveChannelMask(snapshot, expectedChannelCount).Length != expectedChannelCount)
            issues.Add("Resolved TCN channel mask does not match the expected channel count.");
        if (ResolveChannelImportanceScores(snapshot, expectedChannelCount).Length != expectedChannelCount)
            issues.Add("Resolved TCN channel importance scores do not match the expected channel count.");

        if (weights is null)
            return (issues.Count == 0, issues.ToArray());

        if (weights.ConvW is not { Length: > 0 })
            issues.Add("TCN convolution weights are missing.");
        if (weights.HeadW is null || weights.HeadB is null)
            issues.Add("TCN direction head weights are missing.");

        int filters = weights.Filters > 0 ? weights.Filters : 0;
        if (filters <= 0)
            issues.Add("TCN filter count must be positive.");

        int channelIn = weights.ChannelIn > 0 ? weights.ChannelIn : expectedChannelCount;
        if (channelIn != expectedChannelCount)
            issues.Add($"TCN channel count mismatch: expected {expectedChannelCount}, found {channelIn}.");

        int timeSteps = weights.TimeSteps > 0 ? weights.TimeSteps : expectedTimeSteps;
        if (timeSteps != expectedTimeSteps)
            issues.Add($"TCN timestep count mismatch: expected {expectedTimeSteps}, found {timeSteps}.");

        if (weights.HeadW is { Length: > 0 } && filters > 0 && weights.HeadW.Length < filters * 2)
            issues.Add($"TCN head weight length mismatch: expected at least {filters * 2}, found {weights.HeadW.Length}.");
        if (weights.HeadB is { Length: > 0 } && weights.HeadB.Length < 2)
            issues.Add($"TCN head bias length mismatch: expected at least 2, found {weights.HeadB.Length}.");

        if (weights.ConvW is { Length: > 0 } convW && filters > 0)
        {
            for (int b = 0; b < convW.Length; b++)
            {
                int inC = b == 0 ? channelIn : filters;
                int expectedConvLen = filters * inC * 3;
                if (convW[b] is null || convW[b].Length != expectedConvLen)
                    issues.Add($"ConvW[{b}] length mismatch: expected {expectedConvLen}, found {convW[b]?.Length ?? 0}.");

                if (weights.ResW is { Length: > 0 } resW &&
                    b < resW.Length &&
                    resW[b] is { Length: > 0 } residual &&
                    inC != filters &&
                    residual.Length != filters * inC)
                {
                    issues.Add($"ResW[{b}] length mismatch: expected {filters * inC}, found {residual.Length}.");
                }
            }
        }

        if (weights.UseLayerNorm)
        {
            if (weights.LayerNormGamma is null || weights.LayerNormBeta is null || weights.ConvW is null)
            {
                issues.Add("LayerNorm is enabled but LayerNorm parameters are missing.");
            }
            else
            {
                for (int b = 0; b < weights.ConvW.Length; b++)
                {
                    if (b >= weights.LayerNormGamma.Length || weights.LayerNormGamma[b]?.Length != filters)
                        issues.Add($"LayerNormGamma[{b}] length mismatch: expected {filters}, found {weights.LayerNormGamma.ElementAtOrDefault(b)?.Length ?? 0}.");
                    if (b >= weights.LayerNormBeta.Length || weights.LayerNormBeta[b]?.Length != filters)
                        issues.Add($"LayerNormBeta[{b}] length mismatch: expected {filters}, found {weights.LayerNormBeta.ElementAtOrDefault(b)?.Length ?? 0}.");
                }
            }
        }

        if (weights.UseAttentionPooling)
        {
            if (weights.AttentionHeads <= 0)
                issues.Add("AttentionHeads must be positive when attention pooling is enabled.");
            int expectedAttnLen = filters > 0 ? filters * filters : 0;
            if (weights.AttnQueryW?.Length != expectedAttnLen)
                issues.Add($"AttnQueryW length mismatch: expected {expectedAttnLen}, found {weights.AttnQueryW?.Length ?? 0}.");
            if (weights.AttnKeyW?.Length != expectedAttnLen)
                issues.Add($"AttnKeyW length mismatch: expected {expectedAttnLen}, found {weights.AttnKeyW?.Length ?? 0}.");
            if (weights.AttnValueW?.Length != expectedAttnLen)
                issues.Add($"AttnValueW length mismatch: expected {expectedAttnLen}, found {weights.AttnValueW?.Length ?? 0}.");
        }

        return (issues.Count == 0, issues.ToArray());
    }

    private static string ComputeSha256(string payload)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }
}
