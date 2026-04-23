using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Runs promotion-quality gates for a freshly trained CPC encoder. The evaluator is pure
/// orchestration: each primitive (smoke-test, contrastive scorer, downstream probe,
/// representation-drift scorer, architecture-switch probe, adversarial-validation scorer)
/// lives in its own service for testability. Gates are applied in increasing cost order so
/// a cheap rejection stops the pipeline early.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ICpcEncoderGateEvaluator))]
public sealed class CpcEncoderGateEvaluator : ICpcEncoderGateEvaluator
{
    private const int SmokeTestSampleCount = 3;

    private readonly ICpcEncoderProjection _projection;
    private readonly ICpcContrastiveValidationScorer _validationScorer;
    private readonly ICpcDownstreamProbeEvaluator _downstreamProbeEvaluator;
    private readonly ICpcDownstreamProbeRunner _downstreamProbeRunner;
    private readonly ICpcRepresentationDriftScorer _representationDriftScorer;
    private readonly ICpcAdversarialValidationScorer _adversarialValidationScorer;

    public CpcEncoderGateEvaluator(
        ICpcEncoderProjection projection,
        ICpcContrastiveValidationScorer validationScorer,
        ICpcDownstreamProbeEvaluator downstreamProbeEvaluator,
        ICpcDownstreamProbeRunner downstreamProbeRunner,
        ICpcRepresentationDriftScorer representationDriftScorer,
        ICpcAdversarialValidationScorer adversarialValidationScorer)
    {
        _projection = projection;
        _validationScorer = validationScorer;
        _downstreamProbeEvaluator = downstreamProbeEvaluator;
        _downstreamProbeRunner = downstreamProbeRunner;
        _representationDriftScorer = representationDriftScorer;
        _adversarialValidationScorer = adversarialValidationScorer;
    }

    public async Task<CpcEncoderGateResult> EvaluateAsync(
        DbContext readCtx,
        CpcEncoderGateRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readCtx);
        ArgumentNullException.ThrowIfNull(request);

        var options = request.Options;

        // Broadened projection smoke-test — first, middle, last (+ second-and-penultimate
        // when available). One flaky row would previously sneak past the single-sample check.
        foreach (var sample in PickSmokeSamples(request.ValidationSequences))
        {
            var projected = _projection.ProjectLatest(request.Encoder, sample);
            if (projected.Length != options.EmbeddingBlockSize ||
                projected.Any(v => !float.IsFinite(v)))
            {
                return Rejected("projection_invalid");
            }
        }

        var validationScore = _validationScorer.Score(
            request.Encoder,
            request.ValidationSequences,
            options.PredictionSteps);

        if (!double.IsFinite(validationScore.InfoNceLoss) ||
            validationScore.InfoNceLoss > options.MaxValidationLoss)
        {
            return Rejected(
                "validation_loss_out_of_bounds",
                validationScore);
        }

        if (!double.IsFinite(validationScore.MeanL2Norm) ||
            !double.IsFinite(validationScore.MeanDimensionVariance) ||
            validationScore.MeanL2Norm < options.MinValidationEmbeddingL2Norm ||
            validationScore.MeanDimensionVariance < options.MinValidationEmbeddingVariance)
        {
            return Rejected(
                "embedding_collapsed",
                validationScore);
        }

        var downstreamProbe = await _downstreamProbeEvaluator.EvaluateAsync(readCtx, request, ct);
        if (options.EnableDownstreamProbeGate && !downstreamProbe.Passed)
            return Rejected(downstreamProbe.Reason, validationScore, downstreamProbe);

        if (request.PriorInfoNceLoss is { } prior)
        {
            double threshold = prior * (1.0 - options.MinImprovement);
            if (validationScore.InfoNceLoss >= threshold)
                return Rejected("no_improvement", validationScore, downstreamProbe);
        }

        // Load the same-architecture prior once for drift + adversarial gates. Tracked
        // lookup by PriorEncoderId first (exact match the worker had in hand), falling back
        // to the currently-active row by architecture if for some reason the passed-in ID
        // has rotated out.
        MLCpcEncoder? sameArchPrior = null;
        if (request.PriorEncoderId is { } priorId)
        {
            sameArchPrior = await readCtx.Set<MLCpcEncoder>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == priorId && !e.IsDeleted, ct);
            if (sameArchPrior is { EncoderType: var pt } && pt != request.Encoder.EncoderType)
                sameArchPrior = null;
        }

        CpcRepresentationDriftResult drift;
        if (options.EnableRepresentationDriftGate)
        {
            drift = _representationDriftScorer.Score(
                request.Encoder,
                sameArchPrior,
                request.ValidationSequences);

            if (drift.Evaluable)
            {
                if (drift.CentroidCosineDistance is { } cosineDist &&
                    cosineDist < options.MinCentroidCosineDistance)
                {
                    return Rejected(
                        "representation_drift_insufficient",
                        validationScore,
                        downstreamProbe,
                        drift);
                }
                if (drift.MeanPsi is { } meanPsi &&
                    meanPsi > options.MaxRepresentationMeanPsi)
                {
                    return Rejected(
                        "representation_drift_excessive",
                        validationScore,
                        downstreamProbe,
                        drift);
                }
            }
        }
        else
        {
            drift = CpcRepresentationDriftResult.Disabled;
        }

        // Cross-architecture anti-forgetting gate. Only fires when a different-EncoderType
        // active encoder exists for the tuple (e.g. flipping Linear→Tcn). If the candidate
        // loses more than MaxArchitectureSwitchAccuracyRegression vs the incumbent, we block.
        CpcArchitectureSwitchResult architectureSwitch = options.EnableArchitectureSwitchGate
            ? await EvaluateArchitectureSwitchAsync(readCtx, request, options, ct)
            : CpcArchitectureSwitchResult.Disabled;

        if (architectureSwitch.Evaluated &&
            architectureSwitch.CandidateBalancedAccuracy is { } candAcc &&
            architectureSwitch.CrossArchPriorBalancedAccuracy is { } priorAcc &&
            candAcc + 1e-12 < priorAcc - options.MaxArchitectureSwitchAccuracyRegression)
        {
            return Rejected(
                "architecture_switch_regression",
                validationScore,
                downstreamProbe,
                drift,
                architectureSwitch);
        }

        // Adversarial validation — linear classifier AUC between candidate and prior
        // embeddings. Reuses the same prior row we already loaded for the drift gate.
        CpcAdversarialValidationResult adversarial = options.EnableAdversarialValidationGate
            ? _adversarialValidationScorer.Score(
                request.Encoder,
                sameArchPrior,
                request.ValidationSequences,
                options.MinAdversarialValidationSamples)
            : CpcAdversarialValidationResult.Disabled;

        if (adversarial.Evaluated &&
            adversarial.Auc is { } auc &&
            auc > options.MaxAdversarialValidationAuc)
        {
            return Rejected(
                "adversarial_validation_failed",
                validationScore,
                downstreamProbe,
                drift,
                architectureSwitch,
                adversarial);
        }

        return new CpcEncoderGateResult(
            Passed: true,
            Reason: "accepted",
            ValidationInfoNceLoss: validationScore.InfoNceLoss,
            ValidationScore: validationScore,
            DownstreamProbe: downstreamProbe,
            RepresentationDrift: drift,
            ArchitectureSwitch: architectureSwitch,
            AdversarialValidation: adversarial,
            Diagnostics: BuildDiagnostics(
                validationScore, downstreamProbe, drift, architectureSwitch, adversarial));
    }

    private async Task<CpcArchitectureSwitchResult> EvaluateArchitectureSwitchAsync(
        DbContext readCtx,
        CpcEncoderGateRequest request,
        CpcEncoderGateOptions options,
        CancellationToken ct)
    {
        var query = readCtx.Set<MLCpcEncoder>()
            .AsNoTracking()
            .Where(e => e.Symbol == request.Symbol
                     && e.Timeframe == request.Timeframe
                     && e.IsActive
                     && !e.IsDeleted
                     && e.EncoderType != request.Encoder.EncoderType);
        query = request.Regime is null
            ? query.Where(e => e.Regime == null)
            : query.Where(e => e.Regime == request.Regime.Value);

        var crossArchPrior = await query
            .OrderByDescending(e => e.TrainedAt)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);

        if (crossArchPrior is null)
            return CpcArchitectureSwitchResult.NotApplicable;

        var candidateProbe = _downstreamProbeRunner.Evaluate(
            request.Encoder,
            request.TrainingSequences,
            request.ValidationSequences,
            options.PredictionSteps,
            options.MinDownstreamProbeSamples);

        if (!candidateProbe.Evaluable)
            return new CpcArchitectureSwitchResult(
                Evaluated: false,
                Reason: $"architecture_switch_not_evaluable:{candidateProbe.Reason}",
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                CrossArchPriorBalancedAccuracy: null,
                CrossArchPriorEncoderType: crossArchPrior.EncoderType);

        var priorProbe = _downstreamProbeRunner.Evaluate(
            crossArchPrior,
            request.TrainingSequences,
            request.ValidationSequences,
            options.PredictionSteps,
            options.MinDownstreamProbeSamples);

        if (!priorProbe.Evaluable)
            return new CpcArchitectureSwitchResult(
                Evaluated: false,
                Reason: $"architecture_switch_prior_not_evaluable:{priorProbe.Reason}",
                CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
                CrossArchPriorBalancedAccuracy: priorProbe.BalancedAccuracy,
                CrossArchPriorEncoderType: crossArchPrior.EncoderType);

        return new CpcArchitectureSwitchResult(
            Evaluated: true,
            Reason: "architecture_switch_evaluated",
            CandidateBalancedAccuracy: candidateProbe.BalancedAccuracy,
            CrossArchPriorBalancedAccuracy: priorProbe.BalancedAccuracy,
            CrossArchPriorEncoderType: crossArchPrior.EncoderType);
    }

    private static IEnumerable<float[][]> PickSmokeSamples(IReadOnlyList<float[][]> sequences)
    {
        if (sequences.Count == 0) yield break;
        yield return sequences[0];
        if (sequences.Count == 1) yield break;
        yield return sequences[^1];
        if (sequences.Count > 2)
            yield return sequences[sequences.Count / 2];
        if (sequences.Count > 4 && SmokeTestSampleCount > 3)
        {
            yield return sequences[1];
            yield return sequences[^2];
        }
    }

    private static CpcEncoderGateResult Rejected(
        string reason,
        CpcEncoderValidationScore? validationScore = null,
        CpcDownstreamProbeResult? downstreamProbe = null,
        CpcRepresentationDriftResult? drift = null,
        CpcArchitectureSwitchResult? architectureSwitch = null,
        CpcAdversarialValidationResult? adversarial = null)
    {
        downstreamProbe ??= CpcDownstreamProbeResult.Disabled;
        drift ??= CpcRepresentationDriftResult.Disabled;
        architectureSwitch ??= CpcArchitectureSwitchResult.Disabled;
        adversarial ??= CpcAdversarialValidationResult.Disabled;

        var diagnostics = validationScore is null
            ? new Dictionary<string, object?>()
            : BuildDiagnostics(validationScore, downstreamProbe, drift, architectureSwitch, adversarial);

        return new CpcEncoderGateResult(
            Passed: false,
            Reason: reason,
            ValidationInfoNceLoss: validationScore?.InfoNceLoss,
            ValidationScore: validationScore,
            DownstreamProbe: downstreamProbe,
            RepresentationDrift: drift,
            ArchitectureSwitch: architectureSwitch,
            AdversarialValidation: adversarial,
            Diagnostics: diagnostics);
    }

    private static Dictionary<string, object?> BuildDiagnostics(
        CpcEncoderValidationScore validationScore,
        CpcDownstreamProbeResult downstreamProbe,
        CpcRepresentationDriftResult drift,
        CpcArchitectureSwitchResult architectureSwitch,
        CpcAdversarialValidationResult adversarial)
        => new()
        {
            ["ValidationMeanEmbeddingL2Norm"] = FiniteOrNull(validationScore.MeanL2Norm),
            ["ValidationMeanEmbeddingVariance"] = FiniteOrNull(validationScore.MeanDimensionVariance),
            ["DownstreamProbePassed"] = downstreamProbe.Passed,
            ["DownstreamProbeReason"] = downstreamProbe.Reason,
            ["DownstreamProbeCandidateBalancedAccuracy"] = FiniteOrNull(downstreamProbe.CandidateBalancedAccuracy),
            ["DownstreamProbePriorBalancedAccuracy"] = FiniteOrNull(downstreamProbe.PriorBalancedAccuracy),
            ["RepresentationDriftEvaluable"] = drift.Evaluable,
            ["RepresentationDriftReason"] = drift.Reason,
            ["RepresentationCentroidCosineDistance"] = FiniteOrNull(drift.CentroidCosineDistance),
            ["RepresentationMeanPsi"] = FiniteOrNull(drift.MeanPsi),
            ["ArchitectureSwitchEvaluated"] = architectureSwitch.Evaluated,
            ["ArchitectureSwitchReason"] = architectureSwitch.Reason,
            ["ArchitectureSwitchCandidateBalancedAccuracy"] = FiniteOrNull(architectureSwitch.CandidateBalancedAccuracy),
            ["ArchitectureSwitchPriorBalancedAccuracy"] = FiniteOrNull(architectureSwitch.CrossArchPriorBalancedAccuracy),
            ["ArchitectureSwitchPriorEncoderType"] = architectureSwitch.CrossArchPriorEncoderType?.ToString(),
            ["AdversarialValidationEvaluated"] = adversarial.Evaluated,
            ["AdversarialValidationReason"] = adversarial.Reason,
            ["AdversarialValidationAuc"] = FiniteOrNull(adversarial.Auc),
            ["AdversarialValidationPositiveSamples"] = adversarial.PositiveSamples,
            ["AdversarialValidationNegativeSamples"] = adversarial.NegativeSamples,
        };

    private static double? FiniteOrNull(double value)
        => double.IsFinite(value) ? value : null;

    private static double? FiniteOrNull(double? value)
        => value is { } v && double.IsFinite(v) ? v : null;
}
