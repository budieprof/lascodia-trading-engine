using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for SVGP (Sparse Variational Gaussian Process) models.
/// Computes the posterior predictive mean using the pre-computed alpha vector
/// (K_mm^{-1} m) stored in <see cref="ModelSnapshot.Weights"/>[0] and the
/// inducing points stored in <see cref="ModelSnapshot.SvgpInducingPoints"/>.
/// Uses the ARD RBF kernel with per-feature length scales.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class SvgpInferenceEngine : IModelInferenceEngine
{
    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "svgp"
        && snapshot.SvgpInducingPoints is { Length: > 0 }
        && snapshot.SvgpArdLengthScales is { Length: > 0 }
        && snapshot.Weights is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        double[] alpha = snapshot.Weights[0]; // K_mm^{-1} m
        double[][] Z   = snapshot.SvgpInducingPoints!;
        double[] ls    = snapshot.SvgpArdLengthScales!;
        double sf2     = snapshot.SvgpSignalVariance;

        int M = Z.Length;
        int F = ls.Length;

        // Compute K(x*, Z): kernel vector between test point and M inducing points
        // ARD RBF: k(x, z) = σ_f² × exp(−½ Σ_d (x_d − z_d)² / l_d²)
        var kxz = new double[M];
        for (int m = 0; m < M; m++)
        {
            double sumSqDist = 0;
            for (int d = 0; d < F; d++)
            {
                double xd = d < featureCount && d < features.Length ? features[d] : 0.0;
                double zd = d < Z[m].Length ? Z[m][d] : 0.0;
                double lengthScale = ls[d] > 1e-8 ? ls[d] : 1.0;
                double diff = (xd - zd) / lengthScale;
                sumSqDist += diff * diff;
            }
            kxz[m] = sf2 * Math.Exp(-0.5 * sumSqDist);
        }

        // Posterior predictive mean: μ* = K(x*, Z) · α
        double mean = 0;
        for (int m = 0; m < M && m < alpha.Length; m++)
            mean += kxz[m] * alpha[m];

        double rawProb = MLFeatureHelper.Sigmoid(mean);

        return new InferenceResult(rawProb, 0.0);
    }
}
