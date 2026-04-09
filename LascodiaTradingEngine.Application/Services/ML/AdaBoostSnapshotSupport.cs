using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class AdaBoostSnapshotSupport
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 64 };

    internal static string[] ResolveFeatureNames(int featureCount)
    {
        var names = new string[featureCount];
        for (int i = 0; i < featureCount; i++)
            names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
        return names;
    }

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames)
    {
        var builder = new StringBuilder("adaboost-feature-schema|");
        builder.Append(featureNames.Length).Append('|');
        for (int i = 0; i < featureNames.Length; i++)
            builder.Append(featureNames[i]).Append('|');
        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(int featureCount, bool[]? activeMask)
    {
        var builder = new StringBuilder("adaboost-preprocessing|");
        builder.Append(featureCount).Append('|');
        if (activeMask is { Length: > 0 })
        {
            for (int i = 0; i < activeMask.Length; i++)
                builder.Append(activeMask[i] ? '1' : '0');
        }
        else
        {
            builder.Append("identity");
        }

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputeTrainerFingerprint(
        TrainingHyperparams hp,
        int featureCount,
        int numRounds,
        int treeDepth)
    {
        string payload = JsonSerializer.Serialize(new
        {
            featureCount,
            numRounds,
            treeDepth,
            hp.LabelSmoothing,
            hp.TemporalDecayLambda,
            hp.AgeDecayLambda,
            hp.ThresholdSearchStepBps,
            hp.ThresholdSearchMin,
            hp.ThresholdSearchMax,
            hp.BarsPerDay,
            hp.AdaBoostAlphaShrinkage,
            hp.AdaBoostMaxTreeDepth,
            hp.AdaBoostWarmStartRoundsFraction,
            hp.UseSammeR,
            hp.UseJointDepth2Search,
            hp.MagnitudeQuantileTau,
            hp.UseAdaptiveLabelSmoothing,
            hp.FitTemperatureScale,
            hp.ConformalCoverage,
            hp.DensityRatioWindowDays,
            hp.UseCovariateShiftWeights,
            hp.LearningRate,
            hp.L2Lambda,
            hp.MaxEpochs,
            hp.EarlyStoppingPatience,
        }, JsonOptions);

        return ComputeSha256(payload);
    }

    internal static bool IsWarmStartCompatible(
        ModelSnapshot snapshot,
        string[] expectedFeatureNames,
        string expectedFeatureSchemaFingerprint,
        string expectedPreprocessingFingerprint,
        string expectedTrainerFingerprint,
        int expectedFeatureCount,
        out string incompatibilityReason)
    {
        const string ModelType = "AdaBoost";

        if (!string.Equals(snapshot.Type, ModelType, StringComparison.OrdinalIgnoreCase))
        {
            incompatibilityReason = $"Warm-start snapshot type mismatch: expected {ModelType}, found {snapshot.Type}.";
            return false;
        }

        if (snapshot.Features is not { Length: > 0 } || snapshot.Features.Length != expectedFeatureCount)
        {
            incompatibilityReason = "Warm-start feature names do not match the current feature count.";
            return false;
        }

        if (snapshot.Means is not { Length: > 0 } || snapshot.Stds is not { Length: > 0 } ||
            snapshot.Means.Length != expectedFeatureCount || snapshot.Stds.Length != expectedFeatureCount)
        {
            incompatibilityReason = "Warm-start standardization vectors do not match the current feature count.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint) &&
            !string.Equals(snapshot.FeatureSchemaFingerprint, expectedFeatureSchemaFingerprint, StringComparison.Ordinal))
        {
            incompatibilityReason = "Warm-start feature schema fingerprint mismatch.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint) &&
            !string.Equals(snapshot.PreprocessingFingerprint, expectedPreprocessingFingerprint, StringComparison.Ordinal))
        {
            incompatibilityReason = "Warm-start preprocessing fingerprint mismatch.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.TrainerFingerprint) &&
            !string.Equals(snapshot.TrainerFingerprint, expectedTrainerFingerprint, StringComparison.Ordinal))
        {
            incompatibilityReason = "Warm-start trainer fingerprint mismatch.";
            return false;
        }

        if (!snapshot.Features.SequenceEqual(expectedFeatureNames))
        {
            incompatibilityReason = "Warm-start feature names do not match the current feature layout.";
            return false;
        }

        incompatibilityReason = string.Empty;
        return true;
    }

    private static string ComputeSha256(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
