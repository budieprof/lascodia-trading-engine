using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Measures how separable the candidate encoder's embeddings are from the prior's on the
/// same holdout data. Uses a cheap Fisher linear discriminant (mean-difference projection)
/// and reports the leave-one-group-out AUC. AUC near 0.5 means the two encoders produce
/// interchangeable representations; AUC approaching 1.0 means the candidate has drifted so
/// far from the prior that inference continuity is broken even if the pointwise gates
/// individually pass.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICpcAdversarialValidationScorer))]
public sealed class CpcAdversarialValidationScorer : ICpcAdversarialValidationScorer
{
    private readonly ICpcEncoderProjection _projection;

    public CpcAdversarialValidationScorer(ICpcEncoderProjection projection)
    {
        _projection = projection;
    }

    public CpcAdversarialValidationResult Score(
        MLCpcEncoder candidate,
        MLCpcEncoder? prior,
        IReadOnlyList<float[][]> validationSequences,
        int minSamplesPerClass)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(validationSequences);

        if (prior is null || prior.EmbeddingDim != candidate.EmbeddingDim)
            return CpcAdversarialValidationResult.PriorUnavailable;

        var candidateEmbeddings = Project(candidate, validationSequences);
        var priorEmbeddings     = Project(prior, validationSequences);

        if (candidateEmbeddings.Count < minSamplesPerClass || priorEmbeddings.Count < minSamplesPerClass)
            return CpcAdversarialValidationResult.InsufficientSamples;

        int dim = candidate.EmbeddingDim;
        var meanCandidate = Mean(candidateEmbeddings, dim);
        var meanPrior     = Mean(priorEmbeddings, dim);

        var direction = new double[dim];
        for (int i = 0; i < dim; i++)
            direction[i] = meanCandidate[i] - meanPrior[i];

        double norm = 0.0;
        for (int i = 0; i < dim; i++) norm += direction[i] * direction[i];
        if (norm < 1e-20)
            return new CpcAdversarialValidationResult(
                Evaluated: true,
                Reason: "adversarial_validation_evaluated",
                Auc: 0.5,
                PositiveSamples: candidateEmbeddings.Count,
                NegativeSamples: priorEmbeddings.Count);

        double normInv = 1.0 / Math.Sqrt(norm);
        for (int i = 0; i < dim; i++) direction[i] *= normInv;

        var positiveScores = Project1d(candidateEmbeddings, direction);
        var negativeScores = Project1d(priorEmbeddings, direction);

        double auc = ComputeAuc(positiveScores, negativeScores);

        return new CpcAdversarialValidationResult(
            Evaluated: true,
            Reason: "adversarial_validation_evaluated",
            Auc: auc,
            PositiveSamples: candidateEmbeddings.Count,
            NegativeSamples: priorEmbeddings.Count);
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

    private static double[] Mean(IReadOnlyList<float[]> rows, int dim)
    {
        var sum = new double[dim];
        foreach (var r in rows)
            for (int i = 0; i < dim; i++)
                sum[i] += r[i];
        for (int i = 0; i < dim; i++)
            sum[i] /= rows.Count;
        return sum;
    }

    private static double[] Project1d(IReadOnlyList<float[]> rows, double[] direction)
    {
        var scores = new double[rows.Count];
        for (int n = 0; n < rows.Count; n++)
        {
            double s = 0.0;
            for (int i = 0; i < direction.Length; i++)
                s += rows[n][i] * direction[i];
            scores[n] = s;
        }
        return scores;
    }

    /// <summary>
    /// Mann-Whitney U-statistic based AUC. Returns the probability a random positive scores
    /// higher than a random negative. Values in [0.5, 1.0]; we symmetrise below 0.5 because
    /// a reversed direction is just as separable.
    /// </summary>
    private static double ComputeAuc(double[] positive, double[] negative)
    {
        Array.Sort(positive);
        Array.Sort(negative);

        long wins = 0;
        long ties = 0;
        int j = 0;
        foreach (var p in positive)
        {
            while (j < negative.Length && negative[j] < p) j++;
            int less = j;
            int equal = 0;
            int k = j;
            while (k < negative.Length && negative[k] == p) { equal++; k++; }
            wins += less;
            ties += equal;
        }
        double auc = (wins + 0.5 * ties) / ((double)positive.Length * negative.Length);
        return Math.Max(auc, 1.0 - auc);
    }
}
