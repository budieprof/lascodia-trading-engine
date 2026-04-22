using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
// A LascodiaTradingEngine.Application.MarketRegime namespace exists and shadows the
// simple name, so reference the enum via an explicit alias.
using CandleMarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Evaluates whether a trained CPC encoder is safe and useful enough to promote.
/// </summary>
public interface ICpcEncoderGateEvaluator
{
    Task<CpcEncoderGateResult> EvaluateAsync(
        DbContext readCtx,
        CpcEncoderGateRequest request,
        CancellationToken ct);
}

/// <summary>
/// Measures centroid-distance and mean-PSI drift between a candidate CPC encoder and the
/// currently active prior encoder on the same holdout data. Guards against "same loss, same
/// representation" promotions that add churn without changing inference behaviour.
/// </summary>
public interface ICpcRepresentationDriftScorer
{
    CpcRepresentationDriftResult Score(
        MLCpcEncoder candidate,
        MLCpcEncoder? prior,
        IReadOnlyList<float[][]> validationSequences);
}

/// <summary>
/// Runs the downstream-proxy linear direction probe against a chosen encoder on a shared
/// (training, validation) split. Extracted so both the standard downstream-probe gate and
/// the anti-forgetting cross-architecture gate can reuse it.
/// </summary>
public interface ICpcDownstreamProbeRunner
{
    CpcDownstreamProbeScore Evaluate(
        MLCpcEncoder encoder,
        IReadOnlyList<float[][]> trainingSequences,
        IReadOnlyList<float[][]> validationSequences,
        int predictionSteps,
        int minSamples);
}

/// <summary>
/// Fits a cheap linear classifier on {candidate, prior} embeddings and returns the
/// separability AUC. Near-1.0 values indicate pathological representation drift.
/// </summary>
public interface ICpcAdversarialValidationScorer
{
    CpcAdversarialValidationResult Score(
        MLCpcEncoder candidate,
        MLCpcEncoder? prior,
        IReadOnlyList<float[][]> validationSequences,
        int minSamplesPerClass);
}

/// <summary>
/// Computes deterministic holdout contrastive quality for a trained CPC encoder.
/// </summary>
public interface ICpcContrastiveValidationScorer
{
    CpcEncoderValidationScore Score(
        MLCpcEncoder encoder,
        IReadOnlyList<float[][]> validationSequences,
        int predictionSteps);
}

/// <summary>
/// Evaluates whether CPC embeddings preserve enough directional signal to justify promotion.
/// </summary>
public interface ICpcDownstreamProbeEvaluator
{
    Task<CpcDownstreamProbeResult> EvaluateAsync(
        DbContext readCtx,
        CpcEncoderGateRequest request,
        CancellationToken ct);
}

public sealed record CpcEncoderGateRequest(
    string Symbol,
    Timeframe Timeframe,
    CandleMarketRegime? Regime,
    long? PriorEncoderId,
    double? PriorInfoNceLoss,
    MLCpcEncoder Encoder,
    IReadOnlyList<float[][]> TrainingSequences,
    IReadOnlyList<float[][]> ValidationSequences,
    CpcEncoderGateOptions Options);

public sealed record CpcEncoderGateOptions(
    int EmbeddingBlockSize,
    int PredictionSteps,
    double MaxValidationLoss,
    double MinValidationEmbeddingL2Norm,
    double MinValidationEmbeddingVariance,
    bool EnableDownstreamProbeGate,
    int MinDownstreamProbeSamples,
    double MinDownstreamProbeBalancedAccuracy,
    double MinDownstreamProbeImprovement,
    double MinImprovement,
    bool EnableRepresentationDriftGate,
    double MinCentroidCosineDistance,
    double MaxRepresentationMeanPsi,
    bool EnableArchitectureSwitchGate,
    double MaxArchitectureSwitchAccuracyRegression,
    bool EnableAdversarialValidationGate,
    double MaxAdversarialValidationAuc,
    int MinAdversarialValidationSamples);

public sealed record CpcEncoderGateResult(
    bool Passed,
    string Reason,
    double? ValidationInfoNceLoss,
    CpcEncoderValidationScore? ValidationScore,
    CpcDownstreamProbeResult DownstreamProbe,
    CpcRepresentationDriftResult RepresentationDrift,
    CpcArchitectureSwitchResult ArchitectureSwitch,
    CpcAdversarialValidationResult AdversarialValidation,
    IReadOnlyDictionary<string, object?> Diagnostics);

public sealed record CpcEncoderValidationScore(
    double InfoNceLoss,
    double MeanL2Norm,
    double MeanDimensionVariance);

public sealed record CpcDownstreamProbeResult(
    bool Passed,
    string Reason,
    double? CandidateBalancedAccuracy,
    double? PriorBalancedAccuracy)
{
    public static readonly CpcDownstreamProbeResult Disabled =
        new(true, "downstream_probe_disabled", null, null);
}

public sealed record CpcDownstreamProbeScore(
    bool Evaluable,
    string Reason,
    double? BalancedAccuracy)
{
    public static readonly CpcDownstreamProbeScore NotEvaluableInsufficientSamples =
        new(false, "downstream_probe_insufficient_samples", null);

    public static readonly CpcDownstreamProbeScore NotEvaluableInsufficientLabels =
        new(false, "downstream_probe_insufficient_labels", null);

    public static CpcDownstreamProbeScore Evaluated(double balancedAccuracy)
        => new(true, "ok", balancedAccuracy);
}

public sealed record CpcRepresentationDriftResult(
    bool Evaluable,
    string Reason,
    double? CentroidCosineDistance,
    double? MeanPsi)
{
    public static readonly CpcRepresentationDriftResult PriorUnavailable =
        new(false, "representation_drift_prior_unavailable", null, null);

    public static readonly CpcRepresentationDriftResult Disabled =
        new(false, "representation_drift_gate_disabled", null, null);
}

public sealed record CpcArchitectureSwitchResult(
    bool Evaluated,
    string Reason,
    double? CandidateBalancedAccuracy,
    double? CrossArchPriorBalancedAccuracy,
    CpcEncoderType? CrossArchPriorEncoderType)
{
    public static readonly CpcArchitectureSwitchResult NotApplicable =
        new(false, "architecture_switch_not_applicable", null, null, null);

    public static readonly CpcArchitectureSwitchResult Disabled =
        new(false, "architecture_switch_gate_disabled", null, null, null);
}

public sealed record CpcAdversarialValidationResult(
    bool Evaluated,
    string Reason,
    double? Auc,
    int? PositiveSamples,
    int? NegativeSamples)
{
    public static readonly CpcAdversarialValidationResult PriorUnavailable =
        new(false, "adversarial_validation_prior_unavailable", null, null, null);

    public static readonly CpcAdversarialValidationResult InsufficientSamples =
        new(false, "adversarial_validation_insufficient_samples", null, null, null);

    public static readonly CpcAdversarialValidationResult Disabled =
        new(false, "adversarial_validation_gate_disabled", null, null, null);
}
