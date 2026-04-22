using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Fits a cheap directional linear probe on projected CPC embeddings and returns the
/// out-of-sample balanced accuracy. Extracted as a standalone service so both the
/// <see cref="CpcDownstreamProbeEvaluator"/> and the anti-forgetting architecture-switch
/// gate can score candidate and prior encoders without duplicating code.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICpcDownstreamProbeRunner))]
public sealed class CpcDownstreamProbeRunner : ICpcDownstreamProbeRunner
{
    private readonly ICpcEncoderProjection _projection;

    public CpcDownstreamProbeRunner(ICpcEncoderProjection projection)
    {
        _projection = projection;
    }

    public CpcDownstreamProbeScore Evaluate(
        MLCpcEncoder encoder,
        IReadOnlyList<float[][]> trainingSequences,
        IReadOnlyList<float[][]> validationSequences,
        int predictionSteps,
        int minSamples)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(trainingSequences);
        ArgumentNullException.ThrowIfNull(validationSequences);

        var train = BuildSamples(encoder, trainingSequences, predictionSteps);
        var validation = BuildSamples(encoder, validationSequences, predictionSteps);
        if (train.Count < minSamples || validation.Count < minSamples)
            return CpcDownstreamProbeScore.NotEvaluableInsufficientSamples;

        int trainPos = train.Count(s => s.Label);
        int trainNeg = train.Count - trainPos;
        int valPos = validation.Count(s => s.Label);
        int valNeg = validation.Count - valPos;
        if (trainPos == 0 || trainNeg == 0 || valPos == 0 || valNeg == 0)
            return CpcDownstreamProbeScore.NotEvaluableInsufficientLabels;

        var direction = new double[encoder.EmbeddingDim];
        var midpoint  = new double[encoder.EmbeddingDim];
        foreach (var s in train)
        {
            double sign = s.Label ? 1.0 : -1.0;
            for (int i = 0; i < direction.Length; i++)
            {
                direction[i] += sign * s.Embedding[i];
                midpoint[i]  += s.Embedding[i];
            }
        }
        for (int i = 0; i < direction.Length; i++)
        {
            direction[i] /= train.Count;
            midpoint[i]  /= train.Count;
        }

        int tp = 0, tn = 0, fp = 0, fn = 0;
        foreach (var s in validation)
        {
            double score = 0.0;
            for (int i = 0; i < direction.Length; i++)
                score += (s.Embedding[i] - midpoint[i]) * direction[i];

            bool predicted = score >= 0.0;
            if      (predicted && s.Label)     tp++;
            else if (!predicted && !s.Label)   tn++;
            else if (predicted)                fp++;
            else                               fn++;
        }

        double tpr = (tp + fn) > 0 ? tp / (double)(tp + fn) : 0.0;
        double tnr = (tn + fp) > 0 ? tn / (double)(tn + fp) : 0.0;
        return CpcDownstreamProbeScore.Evaluated((tpr + tnr) / 2.0);
    }

    private List<Sample> BuildSamples(
        MLCpcEncoder encoder,
        IReadOnlyList<float[][]> sequences,
        int predictionSteps)
    {
        var samples = new List<Sample>(sequences.Count);
        foreach (var sequence in sequences)
        {
            if (sequence.Length <= predictionSteps + 1)
                continue;

            var projected = _projection.ProjectSequence(encoder, sequence);
            if (projected.Length != sequence.Length)
                continue;

            int t = Math.Max(0, (sequence.Length - predictionSteps - 1) / 2);
            if (projected[t].Length != encoder.EmbeddingDim)
                continue;

            double futureReturn = 0.0;
            for (int k = 1; k <= predictionSteps && t + k < sequence.Length; k++)
                futureReturn += sequence[t + k].Length > 3 ? sequence[t + k][3] : 0.0;

            if (Math.Abs(futureReturn) < 1e-12)
                continue;

            var embedding = projected[t];
            if (embedding.Any(v => !float.IsFinite(v)))
                continue;

            samples.Add(new Sample(embedding, futureReturn > 0.0));
        }
        return samples;
    }

    private sealed record Sample(float[] Embedding, bool Label);
}
