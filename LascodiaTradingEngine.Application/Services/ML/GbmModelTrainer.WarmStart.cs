using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    private static ModelSnapshot? MigrateGbmWarmStartSnapshot(ModelSnapshot snapshot, ILogger logger)
    {
        if (snapshot.Version == ModelVersion) return snapshot;
        try
        {
            var validation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: true);
            if (!validation.IsValid)
            {
                logger.LogWarning("GBM warm-start migration from v{Old} to v{New} failed: {Issues}",
                    snapshot.Version, ModelVersion, string.Join("; ", validation.Issues));
                return null;
            }
            logger.LogInformation("GBM warm-start snapshot accepted from v{Old}.", snapshot.Version);
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning("GBM warm-start migration failed: {Message}", ex.Message);
            return null;
        }
    }

    private static GbmWarmStartArtifact BuildGbmWarmStartArtifact(
        bool attempted, bool compatible, int reusedTreeCount, int totalParentTrees,
        bool preprocessingReused, bool featureLayoutInherited, bool oobReplayApplied,
        string[] issues)
    {
        return new GbmWarmStartArtifact
        {
            Attempted              = attempted,
            Compatible             = compatible,
            ReusedTreeCount        = reusedTreeCount,
            TotalParentTrees       = totalParentTrees,
            ReuseRatio             = totalParentTrees > 0 ? (double)reusedTreeCount / totalParentTrees : 0.0,
            PreprocessingReused    = preprocessingReused,
            FeatureLayoutInherited = featureLayoutInherited,
            OobReplayApplied       = oobReplayApplied,
            CompatibilityIssues    = issues,
        };
    }
}
