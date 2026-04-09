using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services;

internal static class FeaturePipelineTransformSupport
{
    internal const string GroupSumInPlaceTransform = "FEATURE_GROUP_SUM_IN_PLACE_V1";
    internal const string GroupSumInPlaceOperation = "GROUP_SUM_IN_PLACE";

    internal static FeatureTransformDescriptor BuildGroupSumInPlaceDescriptor(
        int inputFeatureCount,
        int[][] sourceIndexGroups,
        string kind = GroupSumInPlaceTransform,
        string version = "1.0")
    {
        return new FeatureTransformDescriptor
        {
            Kind = kind,
            Version = version,
            Operation = GroupSumInPlaceOperation,
            InputFeatureCount = inputFeatureCount,
            OutputStartIndex = 0,
            OutputCount = inputFeatureCount,
            SourceIndexGroups = sourceIndexGroups.Select(g => (int[])g.Clone()).ToArray(),
        };
    }

    internal static bool TryApplyInPlace(float[] features, FeatureTransformDescriptor descriptor)
    {
        if (!string.Equals(descriptor.Operation, GroupSumInPlaceOperation, StringComparison.OrdinalIgnoreCase))
            return false;

        if (features.Length == 0 || descriptor.SourceIndexGroups.Length == 0)
            return true;

        var original = (float[])features.Clone();
        Array.Copy(original, features, features.Length);

        foreach (var group in descriptor.SourceIndexGroups)
        {
            if (group.Length == 0)
                continue;

            int target = -1;
            float sum = 0f;
            foreach (int sourceIndex in group)
            {
                if (sourceIndex < 0 || sourceIndex >= features.Length)
                    continue;

                if (target < 0)
                    target = sourceIndex;

                sum += original[sourceIndex];
            }

            if (target < 0)
                continue;

            features[target] = sum;
            foreach (int sourceIndex in group)
            {
                if (sourceIndex < 0 || sourceIndex >= features.Length || sourceIndex == target)
                    continue;

                features[sourceIndex] = 0f;
            }
        }

        return true;
    }
}
