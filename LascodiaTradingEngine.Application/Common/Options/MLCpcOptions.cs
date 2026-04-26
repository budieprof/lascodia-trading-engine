using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configuration defaults and bounds for the Contrastive Predictive Coding (CPC) encoder
/// pre-training worker. CPC encoders learn per-(symbol, timeframe) temporal context
/// representations from unlabelled candle sequences; downstream supervised learners can
/// consume the E-dim embedding as additional features (V7 feature vector).
/// </summary>
public class MLCpcOptions : ConfigurationOption<MLCpcOptions>
{
    /// <summary>How often the worker evaluates which (symbol, timeframe) pairs need retraining.</summary>
    public int PollIntervalSeconds { get; set; } = 3600;

    /// <summary>How old an active encoder may get before it is retrained.</summary>
    public int RetrainIntervalHours { get; set; } = 168;

    /// <summary>Maximum number of (symbol, timeframe) pairs trained per cycle.</summary>
    public int MaxPairsPerCycle { get; set; } = 2;

    /// <summary>
    /// Dimensionality of the learned context embedding. Must match
    /// <see cref="LascodiaTradingEngine.Application.MLModels.Shared.MLFeatureHelper.CpcEmbeddingBlockSize"/>
    /// because V7 feature vectors append this block at a fixed offset and trainers pin on length.
    /// </summary>
    public int EmbeddingDim { get; set; } = 16;

    /// <summary>Number of future steps predicted during CPC pre-training (InfoNCE positive offset).</summary>
    public int PredictionSteps { get; set; } = 3;

    /// <summary>Length of each sliding-window sequence fed to the encoder.</summary>
    public int SequenceLength { get; set; } = 60;

    /// <summary>Stride between sliding windows when building training sequences.</summary>
    public int SequenceStride { get; set; } = 16;

    /// <summary>Upper bound on the number of training sequences per (symbol, timeframe) to bound CPU cost.</summary>
    public int MaxSequences { get; set; } = 10_000;

    /// <summary>Maximum candles loaded per pair for sequence construction.</summary>
    public int TrainingCandles { get; set; } = 5_000;

    /// <summary>Minimum usable candles before a pair can be trained.</summary>
    public int MinCandles { get; set; } = 1_000;

    /// <summary>
    /// Minimum fractional InfoNCE-loss improvement required over the previous encoder to promote
    /// a new one (0.02 = 2% lower loss). Guards against regression.
    /// </summary>
    public double MinImprovement { get; set; } = 0.02;

    /// <summary>
    /// Absolute upper bound on acceptable InfoNCE loss for a first-ever encoder on a pair. Rejects
    /// pathological training runs (NaN, exploded weights) without needing a prior baseline.
    /// </summary>
    public double MaxAcceptableLoss { get; set; } = 10.0;

    /// <summary>
    /// Fraction of the newest CPC sequences held out for post-training validation. The worker
    /// trains on the older split and promotes only when the fitted encoder also behaves
    /// sanely on the unseen tail window.
    /// </summary>
    public double ValidationSplit { get; set; } = 0.20;

    /// <summary>Minimum sequences required in the validation tail before the holdout gate runs.</summary>
    public int MinValidationSequences { get; set; } = 20;

    /// <summary>
    /// Absolute upper bound for the deterministic holdout contrastive loss computed by
    /// <c>CpcPretrainerWorker</c> after training. The score uses projected embeddings only,
    /// so it validates the representation consumed by inference rather than the trainer's
    /// internal prediction head.
    /// </summary>
    public double MaxValidationLoss { get; set; } = 10.0;

    /// <summary>
    /// Minimum average L2 norm required across holdout embeddings. Rejects collapsed
    /// encoders that project every validation window to an all-zero or near-zero vector.
    /// </summary>
    public double MinValidationEmbeddingL2Norm { get; set; } = 1e-6;

    /// <summary>
    /// Minimum mean per-dimension variance required across holdout embeddings. This is a
    /// representation-quality smoke test: CPC features must move with the validation data,
    /// not just deserialize and return a constant vector. Tightened from 1e-10 so near-zero
    /// drift no longer slips through the variance gate.
    /// </summary>
    public double MinValidationEmbeddingVariance { get; set; } = 1e-4;

    /// <summary>
    /// When true, promotion also requires a cheap downstream-proxy linear probe over CPC
    /// holdout embeddings. The probe predicts future candle direction from embeddings, so
    /// promotion is not based only on the internal contrastive objective.
    /// </summary>
    public bool EnableDownstreamProbeGate { get; set; } = true;

    /// <summary>Minimum labelled train/validation embedding samples required for the probe gate.</summary>
    public int MinDownstreamProbeSamples { get; set; } = 40;

    /// <summary>
    /// Minimum balanced accuracy required for the downstream-proxy probe. Default 0.52 —
    /// explicitly better than random rather than merely at-random. Lower during rollout if
    /// you want the gate to audit without blocking borderline pairs.
    /// </summary>
    public double MinDownstreamProbeBalancedAccuracy { get; set; } = 0.52;

    /// <summary>
    /// Minimum balanced-accuracy improvement required over the prior active encoder when one
    /// exists and can be evaluated on the same probe split. Default 0.02 (two percentage
    /// points) so single-sample noise does not clear the bar.
    /// </summary>
    public double MinDownstreamProbeImprovement { get; set; } = 0.02;

    /// <summary>
    /// Active encoders older than this many hours raise/update a stale-encoder alert while
    /// they are waiting for a successful replacement.
    /// </summary>
    public int StaleEncoderAlertHours { get; set; } = 336;

    /// <summary>When false, the worker loops but performs no training work.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Consecutive cycle failures per pair before a DataQualityIssue alert is raised.</summary>
    public int ConsecutiveFailAlertThreshold { get; set; } = 3;

    /// <summary>
    /// Maximum seconds to wait for the per-(symbol,timeframe,regime,encoder-type) distributed
    /// lock. Default zero means "try once"; if another worker is already training that tuple,
    /// this worker skips it and lets the next poll reconsider.
    /// </summary>
    public int LockTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// When true, the worker trains one encoder per <c>(symbol, timeframe, regime)</c> triple
    /// using <see cref="LascodiaTradingEngine.Domain.Entities.MarketRegimeSnapshot"/> to partition
    /// candles by regime. Default false — under the global regime fallback, a single encoder
    /// per pair is sufficient and much cheaper.
    /// </summary>
    public bool TrainPerRegime { get; set; } = false;

    /// <summary>
    /// Minimum candles required per regime before a regime-specific encoder will be trained.
    /// Only applies when <see cref="TrainPerRegime"/> is true — a regime with too few candles
    /// is skipped and the global-fallback lookup keeps its pair scoring.
    /// </summary>
    public int MinCandlesPerRegime { get; set; } = 500;

    /// <summary>
    /// Multiplier applied to <see cref="TrainingCandles"/> when loading candidate candles for
    /// regime-specific training. Sparse regimes often sit outside the latest global tail, so
    /// per-regime candidates can scan deeper history while still staying bounded.
    /// </summary>
    public int RegimeCandleBackfillMultiplier { get; set; } = 8;

    /// <summary>
    /// Encoder architecture the worker will produce. <see cref="CpcEncoderType.Linear"/> is
    /// the default — single-step <c>ReLU(W_e · x)</c>. Switch to <see cref="CpcEncoderType.Tcn"/>
    /// once live data justifies the extra capacity — the TCN captures a ~7-step receptive
    /// field of past context and is measurably slower to train but preserves train/inference
    /// parity the same way the linear encoder does.
    /// </summary>
    public CpcEncoderType EncoderType { get; set; } = CpcEncoderType.Linear;

    // ── Representation-drift novelty gate ──────────────────────────────────────

    /// <summary>
    /// When true, promotion requires the candidate encoder's holdout embedding centroid to
    /// differ from the prior active encoder's centroid by at least
    /// <see cref="MinCentroidCosineDistance"/> (and/or the mean per-dim PSI to be under
    /// <see cref="MaxRepresentationMeanPsi"/>). Prevents "same loss, same representation"
    /// promotions that add DB churn without changing inference.
    /// </summary>
    public bool EnableRepresentationDriftGate { get; set; } = true;

    /// <summary>
    /// Minimum 1 − cosine(candidate centroid, prior centroid) required on the shared holdout
    /// embeddings before a candidate is treated as a meaningful representation update. Skipped
    /// when there is no prior active encoder or the prior cannot be re-projected.
    /// </summary>
    public double MinCentroidCosineDistance { get; set; } = 1e-3;

    /// <summary>
    /// Upper bound on the mean per-dimension Population Stability Index between candidate and
    /// prior holdout embeddings. A PSI above this ceiling means the two encoders carve the
    /// embedding space so differently that inference continuity cannot be trusted.
    /// </summary>
    public double MaxRepresentationMeanPsi { get; set; } = 2.0;

    // ── Anti-forgetting gate (cross-architecture switch) ───────────────────────

    /// <summary>
    /// When true, switching configured <see cref="EncoderType"/> (e.g. Linear→Tcn) requires
    /// the new architecture to keep the downstream-proxy balanced accuracy within
    /// <see cref="MaxArchitectureSwitchAccuracyRegression"/> of the currently active encoder
    /// of the other architecture. Protects against catastrophic representation regression
    /// during architecture migrations.
    /// </summary>
    public bool EnableArchitectureSwitchGate { get; set; } = true;

    /// <summary>
    /// Maximum allowed balanced-accuracy drop (as a ratio, e.g. 0.05 = 5 pp) when switching
    /// <see cref="EncoderType"/>. Evaluated on the same holdout data as the downstream probe.
    /// </summary>
    public double MaxArchitectureSwitchAccuracyRegression { get; set; } = 0.05;

    // ── Adversarial-validation gate ────────────────────────────────────────────

    /// <summary>
    /// When true, promotion rejects a candidate whose embeddings are trivially separable
    /// from the prior encoder's embeddings — measured by a cheap linear classifier AUC above
    /// <see cref="MaxAdversarialValidationAuc"/>. Catches pathological representation drift
    /// even when loss-based gates pass.
    /// </summary>
    public bool EnableAdversarialValidationGate { get; set; } = true;

    /// <summary>
    /// Upper bound on the candidate-vs-prior separability AUC. Default 0.85 leaves
    /// meaningful-but-not-catastrophic drift through while flagging near-perfect separability.
    /// </summary>
    public double MaxAdversarialValidationAuc { get; set; } = 0.85;

    /// <summary>
    /// Minimum labelled embedding samples (per class) required before the adversarial-validation
    /// gate runs. Gate passes silently when either side cannot contribute enough samples.
    /// </summary>
    public int MinAdversarialValidationSamples { get; set; } = 40;

    // ── Silent-skip operator alerts ────────────────────────────────────────────

    /// <summary>
    /// Consecutive cycles a single silent-skip condition (embedding-dim mismatch, no matching
    /// pretrainer, or prolonged systemic pause) may persist before a
    /// <c>ConfigurationDrift</c> alert is raised. Keeps transient misconfiguration quiet but
    /// surfaces sustained drift.
    /// </summary>
    public int ConfigurationDriftAlertCycles { get; set; } = 3;

    /// <summary>
    /// Continuous hours the systemic-pause flag may stay active before a
    /// <c>ConfigurationDrift</c> alert is raised. Defaults to one day so extended freezes do
    /// not pass unnoticed.
    /// </summary>
    public int SystemicPauseAlertHours { get; set; } = 24;

    /// <summary>
    /// Maximum random delay (in seconds) added to <see cref="PollIntervalSeconds"/> after each
    /// cycle. Prevents two replicas that started at the same moment from polling in lockstep
    /// and racing on every per-candidate distributed lock. Default 300s spreads pollers
    /// across a 5-minute window. Set to 0 to disable.
    /// </summary>
    public int PollJitterSeconds { get; set; } = 300;

    /// <summary>
    /// When the cycle throws, the next poll interval grows by
    /// <c>2^min(consecutiveFailures, FailureBackoffCapShift)</c>. Caps the maximum doubling
    /// so a long outage still polls at a finite rate. Default 6 → 64× the base interval at
    /// most. Set to 0 to disable backoff entirely.
    /// </summary>
    public int FailureBackoffCapShift { get; set; } = 6;

    /// <summary>
    /// When <c>true</c>, the worker acquires a singleton distributed lock at the start of
    /// each cycle so only one replica performs candidate selection + candle loading +
    /// per-candidate work per cycle. Other replicas skip and retry on the next poll.
    /// Default <c>true</c> — without it, multiple replicas redundantly load candles and
    /// race on per-candidate locks. Disable only if you have exactly one replica.
    /// </summary>
    public bool UseCycleLock { get; set; } = true;

    /// <summary>
    /// Maximum seconds to wait for the cycle-level distributed lock when
    /// <see cref="UseCycleLock"/> is true. Default 0 — try once and skip the cycle if
    /// another replica holds the lock; the next poll re-attempts after jitter.
    /// </summary>
    public int CycleLockTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Number of consecutive cycles in which every attempted candidate fails (or is
    /// rejected) — despite candidates being available — before the worker raises an
    /// aggregate <c>SystemicMLDegradation</c> alert. The per-pair consecutive-failure
    /// alert exists for individual offenders; this fires when fleet-wide training is
    /// failing, which usually points to data, infrastructure, or trainer regressions.
    /// </summary>
    public int FleetSystemicConsecutiveZeroPromotionCycles { get; set; } = 6;

    /// <summary>
    /// When <c>true</c>, the worker reads per-context override keys from <c>EngineConfig</c>
    /// before each cycle and uses them in place of the global defaults for these training
    /// knobs: <c>MinCandles</c>, <c>MaxAcceptableLoss</c>, <c>MinImprovement</c>,
    /// <c>MaxValidationLoss</c>, <c>MinValidationSequences</c>. The override hierarchy is
    /// checked in this order (first hit wins):
    /// <list type="number">
    ///   <item><c>MLCpc:Override:Symbol:{symbol}:Timeframe:{timeframe}:Regime:{regime}:{Knob}</c></item>
    ///   <item><c>MLCpc:Override:Symbol:{symbol}:Timeframe:{timeframe}:{Knob}</c></item>
    ///   <item><c>MLCpc:Override:Symbol:{symbol}:{Knob}</c></item>
    ///   <item><c>MLCpc:Override:Timeframe:{timeframe}:{Knob}</c></item>
    /// </list>
    /// Override keys with unrecognised knob suffixes are logged once per cycle so typos
    /// surface in dashboards.
    /// </summary>
    public bool OverridesEnabled { get; set; } = true;
}
