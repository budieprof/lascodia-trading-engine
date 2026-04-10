using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class ElmModelTrainer
{
    private static ModelSnapshot? MigrateElmWarmStartSnapshot(
        ModelSnapshot snapshot,
        ILogger       logger)
    {
        if (snapshot.Version == ModelVersion)
            return snapshot;

        try
        {
            var normalized = ElmSnapshotSupport.NormalizeSnapshotCopy(snapshot);
            var validation = ElmSnapshotSupport.ValidateNormalizedSnapshot(normalized, allowLegacy: true);

            if (!validation.IsValid)
            {
                logger.LogWarning(
                    "ELM warm-start migration from v{Old} to v{New} failed validation: {Issues}",
                    snapshot.Version, ModelVersion, string.Join("; ", validation.Issues));
                return null;
            }

            logger.LogInformation(
                "ELM warm-start snapshot migrated from v{Old} to v{New}.",
                snapshot.Version, ModelVersion);
            return normalized;
        }
        catch (Exception ex)
        {
            logger.LogWarning("ELM warm-start migration failed: {Message}", ex.Message);
            return null;
        }
    }

    private static ElmWarmStartArtifact BuildElmWarmStartArtifact(
        bool     attempted,
        bool     compatible,
        int      reusedLearnerCount,
        int      totalParentLearners,
        bool     inputWeightsTransferred,
        bool     pruningRemapped,
        string[] compatibilityIssues)
    {
        return new ElmWarmStartArtifact
        {
            Attempted               = attempted,
            Compatible              = compatible,
            ReusedLearnerCount      = reusedLearnerCount,
            TotalParentLearners     = totalParentLearners,
            ReuseRatio              = totalParentLearners > 0
                                          ? (double)reusedLearnerCount / totalParentLearners
                                          : 0.0,
            InputWeightsTransferred = inputWeightsTransferred,
            PruningRemapped         = pruningRemapped,
            CompatibilityIssues     = compatibilityIssues,
        };
    }
}
