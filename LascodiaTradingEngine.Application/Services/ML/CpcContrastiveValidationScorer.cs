using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Scores holdout CPC embeddings with a deterministic InfoNCE-style objective plus
/// representation-collapse diagnostics.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICpcContrastiveValidationScorer))]
public sealed class CpcContrastiveValidationScorer : ICpcContrastiveValidationScorer
{
    private const int Negatives = 9;

    private readonly ICpcEncoderProjection _projection;

    public CpcContrastiveValidationScorer(ICpcEncoderProjection projection)
    {
        _projection = projection;
    }

    public CpcEncoderValidationScore Score(
        MLCpcEncoder encoder,
        IReadOnlyList<float[][]> validationSequences,
        int predictionSteps)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(validationSequences);

        if (validationSequences.Count == 0)
            return InvalidScore();

        var projected = validationSequences
            .Select(seq => _projection.ProjectSequence(encoder, seq))
            .Where(seq => seq.Length > predictionSteps + 1)
            .ToArray();
        if (projected.Length == 0)
            return InvalidScore();

        var embeddingQuality = ComputeEmbeddingQuality(projected, encoder.EmbeddingDim);
        double totalLoss = 0.0;
        int samples = 0;

        for (int s = 0; s < projected.Length; s++)
        {
            var seq = projected[s];
            int t = Math.Max(0, (seq.Length - predictionSteps - 1) / 2);

            for (int k = 1; k <= predictionSteps && t + k < seq.Length; k++)
            {
                var context = seq[t];
                var positive = seq[t + k];
                if (context.Length != encoder.EmbeddingDim || positive.Length != encoder.EmbeddingDim)
                    return InvalidScore();

                double sPos = Dot(context, positive);
                if (!double.IsFinite(sPos))
                    return InvalidScore();

                var sNeg = new double[Negatives];
                for (int j = 0; j < Negatives; j++)
                {
                    var negSeq = projected[(s + j + 1) % projected.Length];
                    var neg = negSeq[Math.Min(negSeq.Length - 1, t + k)];
                    if (neg.Length != encoder.EmbeddingDim)
                        return InvalidScore();

                    sNeg[j] = Dot(context, neg);
                    if (!double.IsFinite(sNeg[j]))
                        return InvalidScore();
                }

                double maxScore = Math.Max(sPos, sNeg.Max());
                double sumExp = Math.Exp(sPos - maxScore);
                for (int j = 0; j < Negatives; j++)
                    sumExp += Math.Exp(sNeg[j] - maxScore);

                totalLoss += Math.Log(sumExp) + maxScore - sPos;
                samples++;
            }
        }

        return new CpcEncoderValidationScore(
            samples > 0 ? totalLoss / samples : double.NaN,
            embeddingQuality.MeanL2Norm,
            embeddingQuality.MeanDimensionVariance);
    }

    private static CpcEncoderValidationScore InvalidScore()
        => new(double.NaN, double.NaN, double.NaN);

    private static EmbeddingQuality ComputeEmbeddingQuality(
        IReadOnlyList<float[][]> projectedSequences,
        int embeddingDim)
    {
        if (projectedSequences.Count == 0 || embeddingDim <= 0)
            return new EmbeddingQuality(double.NaN, double.NaN);

        long count = 0;
        double normTotal = 0.0;
        var sum = new double[embeddingDim];
        var sumSq = new double[embeddingDim];

        foreach (var sequence in projectedSequences)
        {
            foreach (var embedding in sequence)
            {
                if (embedding.Length != embeddingDim)
                    return new EmbeddingQuality(double.NaN, double.NaN);

                double normSq = 0.0;
                for (int i = 0; i < embeddingDim; i++)
                {
                    var value = embedding[i];
                    if (!float.IsFinite(value))
                        return new EmbeddingQuality(double.NaN, double.NaN);

                    normSq += value * value;
                    sum[i] += value;
                    sumSq[i] += value * value;
                }

                normTotal += Math.Sqrt(normSq);
                count++;
            }
        }

        if (count == 0)
            return new EmbeddingQuality(double.NaN, double.NaN);

        double varianceTotal = 0.0;
        for (int i = 0; i < embeddingDim; i++)
        {
            double mean = sum[i] / count;
            double variance = (sumSq[i] / count) - (mean * mean);
            varianceTotal += Math.Max(0.0, variance);
        }

        return new EmbeddingQuality(
            MeanL2Norm: normTotal / count,
            MeanDimensionVariance: varianceTotal / embeddingDim);
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0.0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private sealed record EmbeddingQuality(
        double MeanL2Norm,
        double MeanDimensionVariance);
}
