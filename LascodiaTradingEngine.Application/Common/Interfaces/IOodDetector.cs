using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Detects out-of-distribution (OOD) feature vectors by measuring Mahalanobis distance
/// from the training distribution (Rec #23).
/// </summary>
public interface IOodDetector
{
    /// <summary>
    /// Computes the Mahalanobis distance of <paramref name="features"/> from the
    /// training distribution described by <paramref name="snapshot"/>.
    /// </summary>
    /// <returns>
    /// A tuple of (distance, isOod).
    /// <c>isOod</c> is <c>true</c> when distance exceeds <paramref name="thresholdSigma"/>.
    /// </returns>
    (double Distance, bool IsOod) Detect(
        float[]       features,
        ModelSnapshot snapshot,
        double        thresholdSigma = 3.0);
}
