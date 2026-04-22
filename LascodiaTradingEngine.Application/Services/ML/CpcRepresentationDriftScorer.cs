using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Computes representation drift between a candidate CPC encoder and its prior on the same
/// holdout data. Emits both the cosine distance between per-dim centroids (small = near-duplicate
/// representation) and the mean per-dimension Population Stability Index (large = incompatible
/// distribution). The worker-level gate rejects on "too similar" (wasted churn) AND
/// "too dissimilar" (inference discontinuity).
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICpcRepresentationDriftScorer))]
public sealed class CpcRepresentationDriftScorer : ICpcRepresentationDriftScorer
{
    private const int PsiBuckets = 10;
    private const double PsiFloor = 1e-6;

    private readonly ICpcEncoderProjection _projection;

    public CpcRepresentationDriftScorer(ICpcEncoderProjection projection)
    {
        _projection = projection;
    }

    public CpcRepresentationDriftResult Score(
        MLCpcEncoder candidate,
        MLCpcEncoder? prior,
        IReadOnlyList<float[][]> validationSequences)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(validationSequences);

        if (prior is null)
            return CpcRepresentationDriftResult.PriorUnavailable;

        if (prior.EmbeddingDim != candidate.EmbeddingDim)
            return CpcRepresentationDriftResult.PriorUnavailable;

        var candidateSamples = Project(candidate, validationSequences);
        var priorSamples     = Project(prior, validationSequences);
        if (candidateSamples.Count == 0 || priorSamples.Count == 0)
            return CpcRepresentationDriftResult.PriorUnavailable;

        int dim = candidate.EmbeddingDim;
        var candidateCentroid = ComputeCentroid(candidateSamples, dim);
        var priorCentroid     = ComputeCentroid(priorSamples, dim);
        double centroidCosineDistance = 1.0 - CosineSimilarity(candidateCentroid, priorCentroid);

        double meanPsi = ComputeMeanPsi(candidateSamples, priorSamples, dim);

        return new CpcRepresentationDriftResult(
            Evaluable: true,
            Reason: "representation_drift_evaluated",
            CentroidCosineDistance: centroidCosineDistance,
            MeanPsi: meanPsi);
    }

    private List<float[]> Project(MLCpcEncoder encoder, IReadOnlyList<float[][]> sequences)
    {
        var list = new List<float[]>(sequences.Count);
        foreach (var seq in sequences)
        {
            if (seq.Length == 0) continue;
            var row = _projection.ProjectLatest(encoder, seq);
            if (row.Length != encoder.EmbeddingDim) continue;
            if (row.Any(v => !float.IsFinite(v))) continue;
            list.Add(row);
        }
        return list;
    }

    private static double[] ComputeCentroid(IReadOnlyList<float[]> samples, int dim)
    {
        var centroid = new double[dim];
        foreach (var s in samples)
            for (int i = 0; i < dim; i++)
                centroid[i] += s[i];
        for (int i = 0; i < dim; i++)
            centroid[i] /= samples.Count;
        return centroid;
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0.0, na = 0.0, nb = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        if (na <= 0.0 || nb <= 0.0) return 0.0;
        double sim = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        return Math.Clamp(sim, -1.0, 1.0);
    }

    private static double ComputeMeanPsi(
        IReadOnlyList<float[]> candidate,
        IReadOnlyList<float[]> prior,
        int dim)
    {
        double sum = 0.0;
        int counted = 0;
        for (int i = 0; i < dim; i++)
        {
            var (min, max) = GetCombinedRange(candidate, prior, i);
            if (max - min < 1e-12) continue;

            var candidateHist = Histogram(candidate, i, min, max);
            var priorHist     = Histogram(prior, i, min, max);

            double psi = 0.0;
            for (int b = 0; b < PsiBuckets; b++)
            {
                double p = Math.Max(candidateHist[b], PsiFloor);
                double q = Math.Max(priorHist[b],     PsiFloor);
                psi += (p - q) * Math.Log(p / q);
            }
            if (double.IsFinite(psi))
            {
                sum += Math.Abs(psi);
                counted++;
            }
        }
        return counted > 0 ? sum / counted : 0.0;
    }

    private static (double Min, double Max) GetCombinedRange(
        IReadOnlyList<float[]> a, IReadOnlyList<float[]> b, int dim)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var row in a)
        {
            var v = row[dim];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        foreach (var row in b)
        {
            var v = row[dim];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }

    private static double[] Histogram(IReadOnlyList<float[]> rows, int dim, double min, double max)
    {
        var counts = new double[PsiBuckets];
        double width = (max - min) / PsiBuckets;
        foreach (var row in rows)
        {
            double v = row[dim];
            int bucket = width > 0.0
                ? Math.Clamp((int)((v - min) / width), 0, PsiBuckets - 1)
                : 0;
            counts[bucket]++;
        }
        for (int i = 0; i < PsiBuckets; i++)
            counts[i] /= Math.Max(1, rows.Count);
        return counts;
    }
}
