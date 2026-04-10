using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    /// <summary>
    /// Attempts to migrate a warm-start snapshot from an older model version to the current version.
    /// Returns null if migration is not possible (e.g., incompatible structural changes).
    /// </summary>
    private static ModelSnapshot? MigrateWarmStartSnapshot(
        ModelSnapshot snapshot,
        ILogger       logger)
    {
        if (snapshot.Version == ModelVersion)
            return snapshot;

        try
        {
            // v2.0 → v2.1: added ConditionalCalibrationRoutingThreshold
            if (snapshot.ConditionalCalibrationRoutingThreshold == 0.0)
                snapshot.ConditionalCalibrationRoutingThreshold = DefaultConditionalRoutingThreshold;

            // v2.1 → v2.2: added FeatureSchemaFingerprint, PreprocessingFingerprint, TrainerFingerprint
            // These are non-critical for warm-start — the compatibility check handles mismatches.

            // Normalize and validate after migration
            var normalized = AdaBoostSnapshotSupport.NormalizeSnapshotCopy(snapshot);
            var validation = AdaBoostSnapshotSupport.ValidateSnapshot(normalized, allowLegacy: true);

            if (!validation.IsValid)
            {
                logger.LogWarning(
                    "AdaBoost warm-start migration from v{Old} to v{New} failed validation: {Issues}",
                    snapshot.Version, ModelVersion, string.Join("; ", validation.Issues));
                return null;
            }

            logger.LogInformation(
                "AdaBoost warm-start snapshot migrated from v{Old} to v{New}.",
                snapshot.Version, ModelVersion);
            return normalized;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "AdaBoost warm-start migration failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Builds a structured warm-start load report capturing what was reused, skipped, or rejected.
    /// </summary>
    private static AdaBoostWarmStartArtifact BuildWarmStartArtifact(
        bool     attempted,
        bool     compatible,
        int      reusedStumpCount,
        int      totalParentStumps,
        bool     weightReplayApplied,
        bool     weightReplaySkippedDueToRegimeChange,
        string[] compatibilityIssues)
    {
        return new AdaBoostWarmStartArtifact
        {
            Attempted                          = attempted,
            Compatible                         = compatible,
            ReusedStumpCount                   = reusedStumpCount,
            SkippedStumpCount                  = Math.Max(0, totalParentStumps - reusedStumpCount),
            TotalParentStumps                  = totalParentStumps,
            ReuseRatio                         = totalParentStumps > 0
                                                     ? (double)reusedStumpCount / totalParentStumps
                                                     : 0.0,
            WeightReplayApplied                = weightReplayApplied,
            WeightReplaySkippedDueToRegimeChange = weightReplaySkippedDueToRegimeChange,
            CompatibilityIssues                = compatibilityIssues,
        };
    }

    /// <summary>
    /// Applies winsorization to feature columns using quantiles from the training portion.
    /// Clips outlier values to [lo, hi] per feature to reduce sensitivity to extreme values.
    /// </summary>
    private static void WinsorizeFeatures(
        List<TrainingSample> samples,
        int                  F,
        int                  trainBound,
        double               percentile)
    {
        if (percentile <= 0.0 || trainBound <= 0) return;

        for (int j = 0; j < F; j++)
        {
            int bound = Math.Min(trainBound, samples.Count);
            var vals = new float[bound];
            for (int i = 0; i < bound; i++) vals[i] = samples[i].Features[j];
            Array.Sort(vals);
            int loIdx = Math.Clamp((int)(percentile * bound), 0, bound - 1);
            int hiIdx = Math.Clamp((int)((1.0 - percentile) * bound), 0, bound - 1);
            float lo = vals[loIdx];
            float hi = vals[hiIdx];
            if (lo >= hi) continue;
            for (int i = 0; i < samples.Count; i++)
                samples[i].Features[j] = Math.Clamp(samples[i].Features[j], lo, hi);
        }
    }
}
