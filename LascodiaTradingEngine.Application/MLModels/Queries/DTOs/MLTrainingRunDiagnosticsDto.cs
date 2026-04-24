using AutoMapper;
using Microsoft.AspNetCore.Http;
using Lascodia.Trading.Engine.SharedLibrary.Mappings;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.DTOs;

/// <summary>
/// Diagnostic view of an <see cref="MLTrainingRun"/>, exposing the advanced metrics,
/// hyperparameter config, dataset stats, and feature-flag audit trail the standard
/// <see cref="MLTrainingRunDto"/> omits. Consumed by the admin UI "Training run → Diagnostics"
/// drill-down so operators can investigate why a run succeeded, failed, or produced a
/// model with unexpected characteristics without opening the DB directly.
/// </summary>
public class MLTrainingRunDiagnosticsDto : IMapFrom<MLTrainingRun>
{
    // ── Identity / lifecycle ─────────────────────────────────────────────────

    /// <summary>Database ID of the training run.</summary>
    public long        Id                        { get; set; }

    /// <summary>Instrument symbol the run was trained on.</summary>
    public string?     Symbol                    { get; set; }

    /// <summary>Chart timeframe for the training data.</summary>
    public Timeframe   Timeframe                 { get; set; }

    /// <summary>What initiated this run — Scheduled, Manual, etc.</summary>
    public TriggerType TriggerType               { get; set; }

    /// <summary>Current run status (Queued, Running, Completed, Failed).</summary>
    public RunStatus   Status                    { get; set; }

    /// <summary>Queue priority (0 emergency … 5 scheduled).</summary>
    public int         Priority                  { get; set; }

    /// <summary>Start of the training data window.</summary>
    public DateTime    FromDate                  { get; set; }

    /// <summary>End of the training data window.</summary>
    public DateTime    ToDate                    { get; set; }

    /// <summary>Total number of samples in the training dataset.</summary>
    public int         TotalSamples              { get; set; }

    /// <summary>ID of the MLModel produced by this run, if completed successfully.</summary>
    public long?       MLModelId                 { get; set; }

    /// <summary>Error message if the run failed.</summary>
    public string?     ErrorMessage              { get; set; }

    /// <summary>UTC time when the run was queued.</summary>
    public DateTime    StartedAt                 { get; set; }

    /// <summary>UTC time when a worker claimed the run.</summary>
    public DateTime?   PickedUpAt                { get; set; }

    /// <summary>UTC time when the run completed (success or failure).</summary>
    public DateTime?   CompletedAt               { get; set; }

    /// <summary>Wall-clock training time in milliseconds (claim → completion).</summary>
    public long?       TrainingDurationMs        { get; set; }

    /// <summary>Number of attempts made (including the current one).</summary>
    public int         AttemptCount              { get; set; }

    // ── Core evaluation metrics ──────────────────────────────────────────────

    /// <summary>Direction prediction accuracy on the held-out evaluation set (0.0–1.0).</summary>
    public decimal?    DirectionAccuracy         { get; set; }

    /// <summary>Magnitude RMSE in pips on the evaluation set.</summary>
    public decimal?    MagnitudeRMSE             { get; set; }

    /// <summary>F1 score on the held-out evaluation set.</summary>
    public decimal?    F1Score                   { get; set; }

    /// <summary>Brier score — calibration quality (0–1, lower is better).</summary>
    public decimal?    BrierScore                { get; set; }

    /// <summary>Simulated Sharpe ratio on the evaluation set using ATR-normalised returns.</summary>
    public decimal?    SharpeRatio               { get; set; }

    /// <summary>
    /// Expected value per signal: mean(|magnitude| when correct) minus mean(|magnitude| when wrong).
    /// Positive means the model has a profitable edge.
    /// </summary>
    public decimal?    ExpectedValue             { get; set; }

    /// <summary>Fraction of evaluation signals where the abstention gate fired (0.0–1.0).</summary>
    public decimal?    AbstentionRate            { get; set; }

    /// <summary>Precision of the abstention gate — how many abstained trades would have lost.</summary>
    public decimal?    AbstentionPrecision       { get; set; }

    // ── Dataset / label quality ──────────────────────────────────────────────

    /// <summary>Fraction of samples labelled Buy (0.0–1.0). 0.5 = balanced.</summary>
    public decimal?    LabelImbalanceRatio       { get; set; }

    /// <summary>JSON snapshot of buy/sell counts, candle range, feature means/stds.</summary>
    public string?     TrainingDatasetStatsJson  { get; set; }

    /// <summary>SHA-256 hash of the sorted training feature matrix bytes (reproducibility).</summary>
    public string?     DatasetHash               { get; set; }

    /// <summary>Inclusive start candle ID used to build the training set.</summary>
    public long?       CandleIdRangeStart        { get; set; }

    /// <summary>Inclusive end candle ID used to build the training set.</summary>
    public long?       CandleIdRangeEnd          { get; set; }

    // ── Architecture / hyperparameters ───────────────────────────────────────

    /// <summary>The learner architecture used for this run.</summary>
    public LearnerArchitecture LearnerArchitecture { get; set; }

    /// <summary>JSON object of hyperparameter overrides (K, LearningRate, L2Lambda, …).</summary>
    public string?     HyperparamConfigJson      { get; set; }

    /// <summary>Per-fold log-loss scores from time-series expanding-window CV.</summary>
    public string?     CvFoldScoresJson          { get; set; }

    // ── Drift context ────────────────────────────────────────────────────────

    /// <summary>
    /// Drift signal that triggered this run (AccuracyDrift, CovariateShift, SharpeDrift,
    /// DisagreementDrift, MultiSignal). Null when the run was scheduled or manual.
    /// </summary>
    public string?     DriftTriggerType          { get; set; }

    /// <summary>JSON of the drift metrics that triggered this run. Null when no drift trigger.</summary>
    public string?     DriftMetadataJson         { get; set; }

    // ── Training feature-flag audit trail ────────────────────────────────────
    //
    // These flags record which optional training-time techniques were applied so
    // operators can compare runs where, e.g., SMOTE was or wasn't used.

    /// <summary>True if this run was the masked-autoencoder pre-training phase.</summary>
    public bool        IsPretrainingRun          { get; set; }

    /// <summary>True if this run distilled a teacher ensemble into a compact student.</summary>
    public bool        IsDistillationRun         { get; set; }

    /// <summary>True if this run was triggered by a Bai-Perron structural break.</summary>
    public bool        IsEmergencyRetrain        { get; set; }

    /// <summary>True if this run is a MAML meta-learning job across multiple tasks.</summary>
    public bool        IsMamlRun                 { get; set; }

    /// <summary>Inner-loop gradient steps used per task during MAML meta-training.</summary>
    public int?        MamlInnerSteps            { get; set; }

    /// <summary>True if SMOTE was applied to balance the minority class.</summary>
    public bool        SmoteApplied              { get; set; }

    /// <summary>True if FGSM adversarial samples were mixed into training.</summary>
    public bool        AdversarialAugmentApplied { get; set; }

    /// <summary>True if mixup data augmentation was applied.</summary>
    public bool        MixupApplied              { get; set; }

    /// <summary>True if curriculum learning ordering was applied.</summary>
    public bool        CurriculumApplied         { get; set; }

    /// <summary>Hardest sample difficulty in the final curriculum epoch.</summary>
    public decimal?    CurriculumFinalDifficulty { get; set; }

    /// <summary>True if Noise Contrastive Estimation loss was used instead of BCE.</summary>
    public bool        NceLossUsed               { get; set; }

    /// <summary>True if importance sampling up-weighted rare high-magnitude events.</summary>
    public bool        RareEventWeightingApplied { get; set; }

    /// <summary>Exponential-decay half-life (days) used to weight samples by recency.</summary>
    public double?     TemporalDecayHalfLifeDays { get; set; }

    /// <summary>Estimated fraction of mislabelled samples removed by Confident Learning (0–100).</summary>
    public double?     LabelNoiseRatePercent     { get; set; }

    /// <summary>Fraction of weights zeroed by magnitude pruning (0–100).</summary>
    public double?     SparsityPercent           { get; set; }

    /// <summary>Fraction of samples retained after coreset selection (0–1).</summary>
    public double?     CoresetSelectionRatio     { get; set; }

    public void Mapping(Profile profile, IHttpContextAccessor httpContextAccessor)
    {
        profile.CreateMap<MLTrainingRun, MLTrainingRunDiagnosticsDto>();
    }
}
