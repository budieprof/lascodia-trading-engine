using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Detects out-of-distribution feature vectors using diagonal Mahalanobis distance
/// from the training distribution stored in <see cref="ModelSnapshot"/> (Rec #23).
/// </summary>
/// <remarks>
/// The diagonal approximation treats each feature as independent:
///   d²(x) = Σ_j ((x_j − μ_j) / σ_j)²
/// This is equivalent to the squared Mahalanobis distance under the assumption
/// that the feature covariance is diagonal (i.e. Σ = diag(σ_1², ..., σ_F²)).
/// The Euclidean z-score sum follows a χ²(F) distribution, so the expected value
/// is F and the threshold at 3σ beyond the mean is F + 3√(2F).
/// </remarks>
public sealed class OodDetector : IOodDetector
{
    /// <inheritdoc/>
    public (double Distance, bool IsOod) Detect(
        float[]       features,
        ModelSnapshot snapshot,
        double        thresholdSigma = 3.0)
    {
        if (snapshot.Means == null || snapshot.Stds == null)
            return (0, false);

        int F    = Math.Min(features.Length, Math.Min(snapshot.Means.Length, snapshot.Stds.Length));
        double d = 0;
        for (int i = 0; i < F; i++)
        {
            double sigma = snapshot.Stds[i] > 1e-8f ? snapshot.Stds[i] : 1e-8f;
            double z     = (features[i] - snapshot.Means[i]) / sigma;
            d += z * z;
        }
        // Normalise: expected d² = F under null; threshold = F + thresholdSigma × √(2F)
        double expected   = F;
        double stdOfChiSq = Math.Sqrt(2.0 * F);
        double threshold  = expected + thresholdSigma * stdOfChiSq;
        return (d, d > threshold);
    }
}
