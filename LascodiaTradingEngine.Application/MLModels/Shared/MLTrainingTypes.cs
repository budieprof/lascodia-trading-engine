using System.Text.Json.Serialization;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Shared;

// ── Primitive training sample ─────────────────────────────────────────────────

/// <summary>A single labelled feature vector extracted from a candle window.</summary>
/// <param name="Features">Flat 33-element aggregated feature vector (used by ensemble trainers).</param>
/// <param name="Direction">1 = Buy, 0 = Sell/Flat.</param>
/// <param name="Magnitude">ATR-normalised magnitude of the expected move, clamped to [-5, 5].</param>
/// <param name="SequenceFeatures">
/// Optional per-timestep feature matrix [T][C] for temporal models (TCN).
/// T = <see cref="MLFeatureHelper.LookbackWindow"/>, C = <see cref="MLFeatureHelper.SequenceChannelCount"/>.
/// <c>null</c> when the sample was built without sequence data (backward-compatible).
/// </param>
public record TrainingSample(
    float[]     Features,
    int         Direction,
    float       Magnitude,
    float[][]?  SequenceFeatures = null);

/// <summary>Pre-computed COT sentiment values for a single candle timestamp.</summary>
/// <param name="NetNorm">Normalised net non-commercial positioning (scale ÷100 000, clipped to [-3,3]).</param>
/// <param name="Momentum">Normalised week-over-week change in net positioning (÷10 000, clipped to [-3,3]).</param>
/// <param name="HasData">
/// <c>true</c> when real COT data was found for this timestamp.
/// <c>false</c> when no COT reports are available — allows the model to distinguish
/// genuine neutral positioning from a data-absence, eliminating silent zero-padding.
/// </param>
public record CotFeatureEntry(float NetNorm, float Momentum, bool HasData = true)
{
    /// <summary>
    /// Sentinel entry used when no COT data is available for the currency.
    /// Sets <see cref="HasData"/> to <c>false</c> so the feature vector can encode the absence explicitly.
    /// </summary>
    public static readonly CotFeatureEntry Zero = new(0f, 0f, HasData: false);
}

// ── Per-run hyperparameter overrides (from HyperparamConfigJson) ─────────────

/// <summary>
/// Nullable subset of <see cref="TrainingHyperparams"/> encoded as JSON in
/// <c>MLTrainingRun.HyperparamConfigJson</c> by <c>TriggerMLHyperparamSearchCommand</c>.
/// Only non-null fields override the <c>EngineConfig</c> defaults in <c>MLTrainingWorker</c>.
/// </summary>
public class HyperparamOverrides
{
    public int?    K                   { get; set; }
    public double? LearningRate        { get; set; }
    public double? L2Lambda            { get; set; }
    public double? TemporalDecayLambda { get; set; }
    public int?    MaxEpochs           { get; set; }
    public int?    EmbargoBarCount     { get; set; }
    public Guid?   SearchBatchId       { get; set; }
    public int?    CandidateIndex      { get; set; }
    public int?    TotalCandidates     { get; set; }
    /// <summary>
    /// Set to <c>true</c> on the canonical promotion run queued by
    /// <c>MLHyperparamBestPickWorker</c> after picking the winning candidate.
    /// Used to prevent the best-pick worker from re-processing the same batch.
    /// </summary>
    public bool    IsPromotion         { get; set; }

    /// <summary>
    /// When set, the training run should produce a regime-specific sub-model scoped to
    /// this market regime name. Null = global model.
    /// </summary>
    public string? RegimeScope         { get; set; }

    // ── Self-tuning override fields ─────────────────────────────────────────
    public double? FpCostWeight               { get; set; }
    public bool?   UseClassWeights            { get; set; }
    public bool?   UseTripleBarrier           { get; set; }
    public double? TripleBarrierProfitAtrMult { get; set; }
    public double? TripleBarrierStopAtrMult   { get; set; }
    public double? LabelSmoothing             { get; set; }
    public double? NoiseSigma                 { get; set; }

    // ── Self-tuning metadata ────────────────────────────────────────────────
    public long?   ParentRunId           { get; set; }
    public int?    SelfTuningGeneration  { get; set; }
    public string? TriggeredBy           { get; set; }
    public string? FailurePatterns       { get; set; }

    // ── Rec #2: Execution-aware labels ────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, triple-barrier labels are computed net of spread and commission
    /// (execution-aware). Requires <see cref="SpreadCostPips"/> to be set.
    /// </summary>
    public bool?   UseExecutionAwareLabels { get; set; }

    /// <summary>
    /// Round-trip spread + commission cost in pips. Used to deflate barrier targets
    /// when <see cref="UseExecutionAwareLabels"/> is <c>true</c>.
    /// </summary>
    public double? SpreadCostPips       { get; set; }

    // ── Rec #7: Causal feature masking ────────────────────────────────────────

    /// <summary>
    /// Comma-separated zero-based feature indices to exclude from training.
    /// Populated by <c>MLCausalFeatureWorker</c> after Granger tests identify
    /// non-causal features. E.g. "4,17" to mask MacdNorm and DayOfWeekCos.
    /// </summary>
    public string? DisabledFeatureIndices { get; set; }

    // ── Rec #11: Monte Carlo Dropout ─────────────────────────────────────────

    /// <summary>
    /// Number of stochastic forward passes at inference time for MC-Dropout.
    /// 0 = disabled. Default 50 when enabled.
    /// </summary>
    public int?    McDropoutSamples     { get; set; }

    // ── Rec #13: Cointegration features ──────────────────────────────────────

    /// <summary>
    /// Symbol to use as the cointegration peer for spread-feature computation.
    /// E.g. "GBPUSD" when training an "EURUSD" model. Null = disabled.
    /// Activates <c>MLFeatureHelper.BuildCointegrationFeatures()</c> which appends
    /// 3 features: SpreadZScore, SpreadMomentum, CointegrationResidualLag1.
    /// </summary>
    public string? CointegrationPeerSymbol { get; set; }

    // ── Rec #14: Curriculum learning ──────────────────────────────────────────

    /// <summary>When <c>true</c>, curriculum learning pacing is applied during training.</summary>
    public bool?   UseCurriculumLearning { get; set; }

    /// <summary>
    /// Fraction of the easiest samples to use in epoch 1 (0–1). Typically 0.3.
    /// The curriculum progressively adds harder samples across subsequent epochs.
    /// </summary>
    public double? CurriculumEasyFraction { get; set; }

    /// <summary>
    /// Controls how quickly the curriculum difficulty ramps up.
    /// Exponent e: at epoch t, difficulty percentile = (t/T)^e.
    /// Default 1.0 (linear). Values &lt; 1 ramp fast, &gt; 1 ramp slowly.
    /// </summary>
    public double? CurriculumPacingExponent { get; set; }

    // ── Rec #15: Liquidity gating ─────────────────────────────────────────────

    /// <summary>
    /// Spread percentile rank above which ML signals are suppressed by
    /// <c>MLLiquidityFingerprintWorker</c>. Default 90 (P90). Set to 100 to disable.
    /// </summary>
    public int?    MaxLiquiditySpreadPercentile { get; set; }

    // ── Rec #20: Temporal sample weighting ────────────────────────────────────

    /// <summary>
    /// Half-life (days) for exponential temporal decay weighting.
    /// w_i = exp(−ln2 / HalfLife × days_since_sample). Null = disabled.
    /// </summary>
    public double? ExponentialDecayHalfLifeDays { get; set; }

    // ── Rec #21: Quantile regression ──────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, the trainer adds quantile regression heads at τ=0.10 and τ=0.90
    /// for asymmetric magnitude prediction (downside/upside estimates).
    /// </summary>
    public bool?   UseQuantileRegression { get; set; }

    // ── Rec #22: SMOTE oversampling ────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c> and class imbalance exceeds 60/40, SMOTE is applied to
    /// oversample the minority direction class before training.
    /// </summary>
    public bool?   UseSmoteOversampling  { get; set; }

    // ── Rec #23: OOD detection ────────────────────────────────────────────────

    /// <summary>
    /// Mahalanobis distance threshold above which a prediction is flagged as OOD.
    /// Default 3.0 (≈ 3σ). Set to 0 to disable OOD gating.
    /// </summary>
    public double? OodThresholdSigma     { get; set; }

    // ── Rec #27: Adversarial augmentation ─────────────────────────────────────

    /// <summary>
    /// FGSM perturbation magnitude ε. When > 0, adversarial samples are mixed into
    /// each training batch by adding ε × sign(∇_x Loss). Typical value 0.01–0.05.
    /// </summary>
    public double? AdversarialFgsmEpsilon { get; set; }

    // ── Rec #28: Confident learning ────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, cross-val Confident Learning is used to detect and remove
    /// mislabelled training samples before model fitting.
    /// </summary>
    public bool?   UseConfidentLearning  { get; set; }

    // ── Rec #29: Weight pruning ────────────────────────────────────────────────

    /// <summary>
    /// When > 0, weights below this fraction of the maximum absolute weight are zeroed.
    /// E.g. 0.2 prunes the bottom 20 % of weights by magnitude.  Default 0 (disabled).
    /// </summary>
    public double? PruningSparsityTarget { get; set; }

    // ── Rec #33: MAML fine-tuning ──────────────────────────────────────────────

    /// <summary>
    /// FK (as long) of the MAML meta-initialiser model to warm-start from.
    /// When set, the trainer runs K inner-loop SGD steps from the MAML weights
    /// rather than random initialisation.
    /// </summary>
    public long?   MamlInitModelId      { get; set; }

    /// <summary>Number of inner-loop gradient steps for MAML fine-tuning. Default 5.</summary>
    public int?    MamlInnerSteps       { get; set; }

    // ── Rec #47: Jacobian / input-gradient regularisation ─────────────────────

    /// <summary>
    /// L2 penalty coefficient applied to the input gradient norm ‖∇_x Loss‖².
    /// Penalises large input sensitivity, making the model less susceptible to small
    /// adversarial perturbations without requiring explicit FGSM augmentation.
    /// 0.0 = disabled. Typical 1e-4 – 1e-2.
    /// </summary>
    public double? JacobianRegLambda { get; set; }

    // ── Rec #70: Mixup data augmentation ──────────────────────────────────────

    /// <summary>
    /// Mixup interpolation coefficient α for Beta(α,α) distribution.
    /// When non-null, training samples are augmented with convex combinations
    /// of random pairs. Typical range 0.1–0.4. Null = mixup disabled.
    /// </summary>
    public double? MixupAlpha { get; set; }

    // ── Rec #71: Curriculum learning ─────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, training samples are sorted from easy (high-confidence
    /// correct) to hard (near decision boundary) before each training epoch.
    /// </summary>
    public bool CurriculumLearning { get; set; }

    // ── Rec #72: Asymmetric loss ──────────────────────────────────────────────

    /// <summary>
    /// Asymmetric loss weight α: loss = -[α·y·log(p) + (1-y)·log(1-p)].
    /// α > 1 penalises false positives (wrong direction) more heavily.
    /// Null = symmetric BCE (α = 1).
    /// </summary>
    public double? AsymmetricLossAlpha { get; set; }

    // ── Rec #85: Noise Contrastive Estimation ─────────────────────────────────

    /// <summary>
    /// When <c>true</c>, training uses NCE loss instead of BCE, explicitly
    /// learning to distinguish true signal samples from K noise samples.
    /// </summary>
    public bool UseNceLoss { get; set; }

    /// <summary>
    /// Number of noise samples K per positive in NCE training. Default 5.
    /// Only used when <see cref="UseNceLoss"/> is <c>true</c>.
    /// </summary>
    public int? NceNoiseSamples { get; set; }

    // ── Rec #88: LASSO feature pre-selection ─────────────────────────────────

    /// <summary>When <c>true</c>, run L1-penalized logistic regression as a feature selector before ensemble training.</summary>
    public bool UseLasso { get; set; }

    /// <summary>L1 regularisation strength λ for LASSO feature selection. Default 0.01.</summary>
    public double? LassoLambda { get; set; }

    // ── Rec #91: Entropy diversity regularisation ─────────────────────────────

    /// <summary>
    /// Weight λ for the entropy diversity regularisation term
    /// loss += -λ·H(ensemble_output). Null = disabled.
    /// </summary>
    public double? EntropyDiversityLambda { get; set; }

    // ── Rec #92: P&L-weighted training ───────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, training samples are weighted by their realised P&amp;L magnitude
    /// so that high-impact trades count more in the loss function.
    /// </summary>
    public bool PnlWeightedTraining { get; set; }

    // ── Recs #126–135 ─────────────────────────────────────────────────────────
    /// <summary>Rec #126: Focal loss focusing parameter γ (0 = standard CE). Default 0 = disabled.</summary>
    public double FocalLossGamma { get; set; } = 0;
    /// <summary>Rec #127: SAM perturbation radius ρ. 0 = disabled.</summary>
    public double SamRho { get; set; } = 0;
    /// <summary>Rec #128: Lookahead inner-loop steps K. 0 = disabled.</summary>
    public int LookaheadK { get; set; } = 0;
    /// <summary>Rec #128: Lookahead slow-weight interpolation factor α.</summary>
    public double LookaheadAlpha { get; set; } = 0.5;
    /// <summary>Rec #129: Self-paced learning initial loss threshold λ₀. 0 = disabled.</summary>
    public double SplLambda0 { get; set; } = 0;
    /// <summary>Rec #129: SPL threshold growth step per epoch.</summary>
    public double SplLambdaStep { get; set; } = 0.01;
    /// <summary>Rec #130: Label smoothing ε. 0 = disabled.</summary>
    public double LabelSmoothingEpsilon { get; set; } = 0;
    /// <summary>Rec #135: Natural gradient learning rate η. 0 = disabled.</summary>
    public double NaturalGradEta { get; set; } = 0;
    /// <summary>Rec #137: Deep ensemble pairwise diversity penalty β. 0 = disabled.</summary>
    public double DiversityPenaltyBeta { get; set; } = 0;

    // ── Recs #156, #157, #165 ──────────────────────────────────────────────────
    /// <summary>Rec #156: Stochastic depth drop probability (0 = disabled).</summary>
    public double StochasticDepthProb { get; set; } = 0;
    /// <summary>Rec #157: CutMix alpha for Beta distribution (0 = disabled).</summary>
    public double CutMixAlpha { get; set; } = 0;
    /// <summary>Rec #165: CVaR risk level α for constrained RL.</summary>
    public double CvarAlpha { get; set; } = 0.1;
    /// <summary>Rec #165: Minimum acceptable CVaR threshold.</summary>
    public double CvarMinThreshold { get; set; } = -0.5;

    // ── Recs #178, #179, #180, #182 ───────────────────────────────────────────
    /// <summary>Rec #178: Weight of the magnitude head loss in multi-task training. Default 0.3.</summary>
    public double MultiTaskMagnitudeWeight { get; set; } = 0.3;
    /// <summary>Rec #180: Softmax temperature for self-distillation soft targets. Default 3.0.</summary>
    public double SelfDistillTemp          { get; set; } = 3.0;
    /// <summary>Rec #182: FGSM adversarial perturbation magnitude ε. Default 0.01.</summary>
    public double FgsmEpsilon              { get; set; } = 0.01;

    // Recs #207, #208
    /// <summary>Rec #207 — SGDR initial cycle length T₀ (epochs). Default 10.</summary>
    public int    SgdrT0      { get; set; } = 10;     // Rec #207 — SGDR initial cycle length
    /// <summary>Rec #207 — SGDR cycle length multiplier T_mult. Default 2.</summary>
    public int    SgdrTMult   { get; set; } = 2;      // Rec #207 — SGDR cycle length multiplier

    // ── Recs #236–265 ─────────────────────────────────────────────────────────
    public int FedAvgRounds { get; set; } = 10;           // #236 number of federated rounds
    public int TtaAugmentations { get; set; } = 8;        // #237 number of TTA augmentations
    public double IrmPenaltyWeight { get; set; } = 1.0;   // #238 IRM invariance penalty weight
    public double EwcLambda { get; set; } = 400.0;        // #239 EWC regularisation strength
    public double AdaptiveDropoutBase { get; set; } = 0.3;// #242 base dropout rate for adaptive dropout
    public double SelectiveThreshold { get; set; } = 0.7; // #243 confidence threshold for selective prediction
    public int SlicedWassersteinProj { get; set; } = 50;  // #244 number of random projections
    public double BohbMinBudget { get; set; } = 10.0;     // #251 BOHB minimum budget
    public double BohbMaxBudget { get; set; } = 100.0;    // #251 BOHB maximum budget
    public int BohbBrackets { get; set; } = 4;            // #251 BOHB number of brackets
    public double CmaEsSigma0 { get; set; } = 0.3;        // #264 CMA-ES initial step size
    public int CmaEsPopSize { get; set; } = 10;           // #264 CMA-ES population size
    public int CmaEsGenerations { get; set; } = 20;       // #264 CMA-ES generations

    // ── Recs #266–295 ─────────────────────────────────────────────────────────
    public int QbcCommitteeSize { get; set; } = 5;           // #269 Query by Committee: committee size
    public int CoreSetSize { get; set; } = 100;               // #270 Core-Set k-center size
    public double SprtP0 { get; set; } = 0.50;               // #271 SPRT null hypothesis accuracy
    public double SprtP1 { get; set; } = 0.55;               // #271 SPRT alternative hypothesis accuracy
    public double SprtAlpha { get; set; } = 0.05;            // #271 SPRT type-I error bound
    public double SprtBeta { get; set; } = 0.10;             // #271 SPRT type-II error bound
    public double LinUcbAlpha { get; set; } = 1.0;           // #278 LinUCB exploration parameter
    public double PpoClipEpsilon { get; set; } = 0.2;        // #279 PPO clip ratio
    public int PpoEpochs { get; set; } = 4;                  // #279 PPO inner epochs
    public double MmdKernelBandwidth { get; set; } = 1.0;    // #281 MMD RBF kernel bandwidth
    public double FedProxMu { get; set; } = 0.01;            // #282 FedProx proximal term weight
    public int AdaHessianSampleSize { get; set; } = 10;      // #291 Hutchinson estimator samples
    public double LambBeta1 { get; set; } = 0.9;             // #292 LAMB momentum beta1
    public double LambBeta2 { get; set; } = 0.999;           // #292 LAMB momentum beta2
    public int IsolationForestTrees { get; set; } = 100;     // #272 Isolation Forest tree count
    public int NBeatsPolynomialDeg { get; set; } = 3;        // #275 N-BEATS polynomial trend degree
    public int NBeatsFourierK { get; set; } = 4;             // #275 N-BEATS Fourier harmonics
    public int StftWindowSize { get; set; } = 10;            // #277 STFT window in bars
    public int VpinBucketSize { get; set; } = 50;            // #288 VPIN volume bucket size
    public double ConformalMartingaleAlpha { get; set; } = 0.05; // #294 martingale significance level
    public int MmcBootstrapReps { get; set; } = 500;         // #267/#268 MCS/SPA bootstrap replications

    // Recs #296–325
    public double HuberDelta { get; set; } = 1.35;           // #296 Huber loss transition point
    public double LtsTrimFraction { get; set; } = 0.10;      // #297 LTS fraction to trim (worst residuals)
    public int SsaWindowLength { get; set; } = 10;           // #298 SSA embedding window L
    public double FtrlAlpha { get; set; } = 0.1;             // #299 FTRL learning rate
    public double FtrlBeta { get; set; } = 1.0;              // #299 FTRL smoothing parameter
    public double NclLambda { get; set; } = 0.3;             // #300 NCL correlation penalty weight
    public double MwuEpsilon { get; set; } = 0.1;            // #301 MWU learning rate for multiplicative update
    public int TransferEntropyLag { get; set; } = 1;         // #302 TE conditioning lag
    public int CcmLibrarySizeMax { get; set; } = 200;        // #303 CCM max library size
    public double PacBayesDelta { get; set; } = 0.05;        // #304 PAC-Bayes confidence parameter
    public int DmdModes { get; set; } = 5;                   // #308 DMD number of modes to extract
    public int KoopmanDictSize { get; set; } = 20;           // #309 EDMD dictionary size
    public int HessianTopK { get; set; } = 5;                // #310 top-K eigenvalues to compute
    public double DbscanEpsilon { get; set; } = 0.5;         // #317 DBSCAN neighbourhood radius
    public int DbscanMinPoints { get; set; } = 3;            // #317 DBSCAN min cluster size
    public double PgdEpsilon { get; set; } = 0.1;            // #319 PGD attack epsilon ball radius
    public int PgdSteps { get; set; } = 10;                  // #319 PGD iteration steps
    public double PeltPenalty { get; set; } = 0;             // #322 PELT penalty (0 = auto BIC)
    public double TaskArithmeticScale { get; set; } = 1.0;   // #325 task vector scaling factor
    public double KellyMaxFraction { get; set; } = 0.25;     // #312 Kelly fraction cap
    public double OmegaThreshold { get; set; } = 0.0;        // #313 Omega ratio return threshold

    // ── Recs #326–355 ─────────────────────────────────────────────────────────

    /// <summary>Rank for low-rank decomposition (Rec #326).</summary>
    public int? LowRankR { get; set; }

    /// <summary>GEM episodic memory size (Rec #334).</summary>
    public int? GemEpisodeSize { get; set; }

    /// <summary>Reptile inner-loop steps (Rec #335).</summary>
    public int? ReptileK { get; set; }

    /// <summary>PackNet binary mask count (Rec #338).</summary>
    public int? PackNetMasks { get; set; }

    /// <summary>SGLD noise scale (Rec #339).</summary>
    public double? SgldNoise { get; set; }

    /// <summary>Particle filter count (Rec #336).</summary>
    public int? ParticleCount { get; set; }

    /// <summary>GAS model beta parameter (Rec #337).</summary>
    public double? GasBeta { get; set; }

    /// <summary>DQN replay buffer size (Rec #340).</summary>
    public int? DqnReplaySize { get; set; }

    /// <summary>DQN epsilon-greedy exploration rate (Rec #340).</summary>
    public double? DqnEpsilon { get; set; }

    /// <summary>SAC entropy coefficient (Rec #343).</summary>
    public double? SacAlpha { get; set; }

    /// <summary>Node2Vec random walk length (Rec #347).</summary>
    public int? Node2VecWalkLen { get; set; }

    /// <summary>Survival model time steps (Rec #349).</summary>
    public int? SurvivalTimeSteps { get; set; }

    /// <summary>Flow matching sigma noise level (Rec #351).</summary>
    public double? FlowMatchingSigma { get; set; }

    /// <summary>Learn-then-test significance level in integer basis points (Rec #353).</summary>
    public int? LearnThenTestAlpha { get; set; }

    /// <summary>NFL test Monte Carlo runs (Rec #355).</summary>
    public int? NflMonteCarloRuns { get; set; }

    // ── Recs #361–385 ─────────────────────────────────────────────────────────

    /// <summary>Rec #361: Evidential annealing coefficient for deep evidential regression uncertainty estimation.</summary>
    public double? EvidentialAnnealing { get; set; }

    /// <summary>Rec #362: Temperature T for knowledge distillation or Boltzmann soft-max scaling.</summary>
    public double? TemperatureT { get; set; }

    /// <summary>Rec #363: Number of inducing points for sparse GP / DKL approximation.</summary>
    public int? DklInducingPoints { get; set; }

    /// <summary>Rec #381: Beta-VAE disentanglement weight β applied to the KL divergence term.</summary>
    public double? BetaVaeBeta { get; set; }

    /// <summary>Rec #372: Number of feature subsets K for Rotation Forest PCA decomposition.</summary>
    public int? RotationSubsets { get; set; }

    /// <summary>Rec #369: Hoeffding tree split confidence δ for concept drift detection.</summary>
    public double? HoeffdingDelta { get; set; }

    /// <summary>Rec #371: Number of k nearest neighbours for Dynamic Ensemble Selection competence region.</summary>
    public int? DesKNeighbors { get; set; }

    /// <summary>Rec #385: Seasonal period S for the SARIMA component of the SARIMA-Neural hybrid model.</summary>
    public int? SarimaPeriod { get; set; }

    /// <summary>Rec #373: Number of perturbation samples for LIME local surrogate fitting.</summary>
    public int? LimePerturbCount { get; set; }

    /// <summary>Rec #378: Minimum fractional differencing order d_min for the ADF-based grid search.</summary>
    public double? FractionalDMin { get; set; }

    // ── Recs #388–415 ─────────────────────────────────────────────────────────

    /// <summary>Rec #388: Number of random convolutional kernels for the ROCKET transform.</summary>
    public int? RocketNumKernels      { get; set; }

    /// <summary>Rec #389: Number of sequential attention steps in the TabNet architecture.</summary>
    public int? TabNetSteps           { get; set; }

    /// <summary>Rec #389: Sparsity regularisation coefficient for TabNet attention masks.</summary>
    public double? TabNetSparsity     { get; set; }

    /// <summary>Rec #390: Number of attention heads in the FT-Transformer encoder.</summary>
    public int? FtTransformerHeads    { get; set; }

    /// <summary>Rec #390: Number of stacked transformer blocks in the FT-Transformer encoder. Default 3.</summary>
    public int? FtTransformerNumLayers { get; set; }

    /// <summary>Rec #392: State space dimension for the Mamba SSM architecture.</summary>
    public int? MambaStateSize        { get; set; }

    /// <summary>Rec #399: Focal loss focusing parameter γ (separate nullable override for focal loss).</summary>
    public double? FocalGamma         { get; set; }

    /// <summary>Rec #398: Label smoothing ε override for this run.</summary>
    public double? LabelSmoothEpsilon { get; set; }

    /// <summary>Rec #400: Number of nearest neighbours k used by the SMOTE oversampler.</summary>
    public int? SmoteKNeighbors       { get; set; }

    /// <summary>Rec #403: K-FAC damping coefficient λ for the Kronecker-factored curvature approximation.</summary>
    public double? KfacDampening      { get; set; }

    /// <summary>Rec #411: Number of ordinal ranking classes for the ordinal regression head.</summary>
    public int? OrdinalClasses        { get; set; }

    /// <summary>Rec #412: NSGA-II population size for multi-objective hyperparameter search.</summary>
    public int? NsgaIIPopSize         { get; set; }

    /// <summary>Rec #415: L1 sparsity penalty λ for the Sparse PCA component loading matrix.</summary>
    public double? SparseL1Lambda     { get; set; }

    // Batch 19 hyperparameter overrides
    public int? GFlowNetSteps    { get; set; }
    public int? SfAdamWarmup     { get; set; }
    public int? SnnTimeSteps     { get; set; }
    public int? GrokFastWindow   { get; set; }
    public int? RfFlowSteps      { get; set; }
    public int? PcGradTasks      { get; set; }
    public int? IvaeLatentDim    { get; set; }
    public int? MuonOrthSteps    { get; set; }
    public int? TttAdaptSteps    { get; set; }
    public int? HgnnHyperedges   { get; set; }

    // Batch 20 hyperparameter overrides
    public int? Mamba2StateSize         { get; set; }
    public int? GrpoGroupSize           { get; set; }
    public int? DoraRank                { get; set; }
    public int? BitNetTernary           { get; set; }
    public int? BasedFeatureMapDim      { get; set; }
    public int? VicRegLambdaScale       { get; set; }
    public int? FnoModes                { get; set; }
    public int? MonarchBlockSize        { get; set; }
    public int? CoconutThoughtSteps     { get; set; }
    public int? LagrangianConstraintType { get; set; }

    // Batch 21 hyperparameter overrides
    public int? DeltaNetMemDim       { get; set; }
    public int? DiffTransformerHeads { get; set; }
    public int? TitansMemorySize     { get; set; }
    public int? GlaGateRank          { get; set; }
    public int? Hgrn2Layers          { get; set; }
    public int? LinOssOscillators    { get; set; }
    public int? HedgehogPolyDegree   { get; set; }
    public int? FlashFftConvDepth    { get; set; }
    public int? GmmVaeComponents     { get; set; }
    public int? SMoEExperts          { get; set; }
}

// ── Hyperparameters ───────────────────────────────────────────────────────────

/// <summary>
/// Complete set of training hyperparameters loaded from <c>EngineConfig</c> at run-time.
/// All keys live under the <c>MLTraining:</c> prefix and are hot-reloadable.
/// </summary>
public record TrainingHyperparams(
    int    K,
    double LearningRate,
    double L2Lambda,
    int    MaxEpochs,
    int    EarlyStoppingPatience,
    double MinAccuracyToPromote,
    double MinExpectedValue,
    double MaxBrierScore,
    double MinSharpeRatio,
    int    MinSamples,
    int    ShadowRequiredTrades,
    int    ShadowExpiryDays,
    int    WalkForwardFolds,
    int    EmbargoBarCount,
    int    TrainingTimeoutMinutes,
    double TemporalDecayLambda,
    int    DriftWindowDays,
    int    DriftMinPredictions,
    double DriftAccuracyThreshold,
    /// <summary>
    /// Maximum allowed std dev of accuracy across walk-forward folds.
    /// Models with high cross-fold variance are unstable and should not be promoted.
    /// Default 0.15 (15 %). Set to 1.0 to disable the gate.
    /// </summary>
    double MaxWalkForwardStdDev,
    /// <summary>
    /// Label smoothing ε applied to binary cross-entropy targets.
    /// y_smooth = y × (1 − ε) + 0.5 × ε.
    /// Reduces sensitivity to systematic labelling errors from outcome resolution.
    /// Default 0.05. Set to 0.0 to disable.
    /// </summary>
    double LabelSmoothing,
    /// <summary>
    /// Features with permutation importance below this fraction of equal-share weight
    /// (1 / featureCount) are masked and the ensemble is re-trained without them.
    /// Default 0.0 (disabled). A value of 0.5 prunes features below half the equal-share.
    /// </summary>
    double MinFeatureImportance,
    /// <summary>
    /// When <c>true</c>, after training the global model the worker also trains per-regime
    /// sub-models by partitioning training samples according to <see cref="MarketRegimeSnapshot"/>
    /// records. The scorer then routes inference to the regime-specific model when available.
    /// </summary>
    bool   EnableRegimeSpecificModels,
    /// <summary>
    /// Fraction of features sampled per base learner (Random Forest-style diversity).
    /// 0.0 = all features used (disabled). Typical value ≈ sqrt(F)/F ≈ 0.55 for F=29.
    /// Each bag trains on a different random subset, forcing learner diversity.
    /// </summary>
    double FeatureSampleRatio,
    /// <summary>
    /// Maximum Expected Calibration Error (ECE) allowed for model promotion.
    /// 0.0 disables the gate. ECE &lt; 0.05 is considered well-calibrated.
    /// Prevents over-confident models from passing Platt scaling unchecked.
    /// </summary>
    double MaxEce,
    /// <summary>
    /// When <c>true</c>, training labels are determined by the triple-barrier method
    /// (profit target, stop-loss, or time horizon — whichever fires first).
    /// Produces labels directly aligned with trading P&amp;L rather than next-bar direction.
    /// </summary>
    bool   UseTripleBarrier,
    /// <summary>ATR multiplier for the profit target barrier. Default 1.5.</summary>
    double TripleBarrierProfitAtrMult,
    /// <summary>ATR multiplier for the stop-loss barrier. Default 1.0.</summary>
    double TripleBarrierStopAtrMult,
    /// <summary>Maximum bars to hold before the time-horizon barrier fires. Default 24.</summary>
    int    TripleBarrierHorizonBars,
    /// <summary>
    /// Standard deviation of zero-mean Gaussian noise added to each feature value during
    /// training (input-noise regularisation). 0.0 = disabled. Typical values: 0.01–0.05.
    /// Adds a data-dependent L2 penalty effect, reducing overfitting in low-sample regimes.
    /// </summary>
    double NoiseSigma,
    /// <summary>
    /// Cost weight applied to false-positive (FP) predictions in the asymmetric BCE loss.
    /// loss = FpCostWeight × y × log(p) + (1 − FpCostWeight) × (1 − y) × log(1 − p).
    /// 0.5 = symmetric (standard BCE). Values > 0.5 penalise false positives more heavily,
    /// biasing the model toward precision. Default 0.5 (disabled).
    /// </summary>
    double FpCostWeight,
    /// <summary>
    /// Negative Correlation Learning regularisation coefficient λ_ncl.
    /// Adds penalty λ_ncl × p_k × (p_k − p_avg) to each learner's loss, discouraging
    /// correlated errors and increasing ensemble diversity. 0.0 = disabled. Typical 0.1–0.5.
    /// </summary>
    double NclLambda,
    /// <summary>
    /// Fractional differencing order d applied to price-derived features to achieve stationarity
    /// while preserving long-memory autocorrelation. 0.0 = disabled (standard price-based features).
    /// Typical range: 0.2–0.6. Higher d = more stationarity, less memory retention.
    /// </summary>
    double FracDiffD,
    /// <summary>
    /// Maximum allowed drawdown on the simulated equity curve within each walk-forward fold.
    /// Folds where the simulated P&amp;L curve has a peak-to-trough drawdown exceeding this
    /// fraction are flagged, and the model is rejected if the majority of folds fail.
    /// 1.0 = disabled (no equity-curve gate). Default 1.0.
    /// </summary>
    double MaxFoldDrawdown,
    /// <summary>
    /// Minimum Sharpe ratio required on the simulated equity curve within each walk-forward fold.
    /// Folds with Sharpe below this threshold are flagged; model rejected if majority fail.
    /// −99.0 = disabled. Default −99.0.
    /// </summary>
    double MinFoldCurveSharpe,
    /// <summary>
    /// Fraction of the K base learners to be polynomial (degree-2 interaction) learners.
    /// For PolyLearnerFraction = f, the last ⌊K×f⌋ learners augment their features with all
    /// pairwise products of the top-5 features, enabling non-linear decision boundaries.
    /// 0.0 = disabled (all linear learners). Typical 0.33 for heterogeneous ensembles.
    /// </summary>
    double PolyLearnerFraction,
    /// <summary>
    /// Number of bars ahead of any test sample whose training labels must be purged to
    /// prevent forward-looking leakage. Training samples whose forward label window
    /// (up to PurgeHorizonBars candles) overlaps with the test fold start are removed.
    /// Set equal to <see cref="TripleBarrierHorizonBars"/> for maximum correctness.
    /// 0 = disabled (use EmbargoBarCount only). Default 0.
    /// </summary>
    int PurgeHorizonBars,
    /// <summary>
    /// Minimum predicted probability of the correct label below which a training sample
    /// is considered likely mislabeled and has its loss contribution downweighted.
    /// For a Buy sample with label 1: if P(1) &lt; NoiseCorrectionThreshold, weight = P(1).
    /// 0.0 = disabled. Typical 0.05–0.10. Set to 0.0 for standard unweighted training.
    /// </summary>
    double NoiseCorrectionThreshold,
    /// <summary>
    /// Maximum allowed Pearson correlation between any two base learner weight vectors.
    /// Learner pairs with ρ exceeding this threshold are considered redundant; the one
    /// with lower OOB accuracy is re-initialised with a different random seed and fine-tuned
    /// for 10 epochs. 1.0 = disabled. Typical 0.90–0.95.
    /// </summary>
    double MaxLearnerCorrelation,
    /// <summary>
    /// Epoch at which Stochastic Weight Averaging (SWA) begins accumulating an ensemble-average
    /// of the weight vectors. The SWA weights occupy a flatter loss basin and generalise better
    /// OOD. After training completes, the final snapshot uses SWA weights instead of last-epoch
    /// weights. 0 = disabled. Typical: 60–80% of MaxEpochs.
    /// </summary>
    int    SwaStartEpoch,
    /// <summary>
    /// Frequency (in epochs) at which the SWA running average is updated.
    /// 1 = update every epoch (recommended). Only used when SwaStartEpoch > 0.
    /// </summary>
    int    SwaFrequency,
    /// <summary>
    /// Mixup interpolation coefficient α. For each training sample, a random partner is drawn
    /// and the two samples are linearly interpolated: x_mix = λ·x_i + (1−λ)·x_j,
    /// y_mix = λ·y_i + (1−λ)·y_j, where λ ~ Beta(α, α).
    /// 0.0 = disabled. Typical 0.1–0.4.
    /// </summary>
    double MixupAlpha,
    /// <summary>
    /// When <c>true</c>, applies Caruana et al. greedy forward ensemble selection on the
    /// calibration set after training. The result is a per-learner usage-frequency weight
    /// stored in <see cref="ModelSnapshot.EnsembleSelectionWeights"/> and used at inference
    /// instead of uniform averaging. False = uniform average (default).
    /// </summary>
    bool   EnableGreedyEnsembleSelection,
    /// <summary>
    /// Maximum L2 norm of the gradient vector before an Adam weight update.
    /// When the gradient norm exceeds this value it is rescaled to MaxGradNorm.
    /// 0.0 = disabled. Typical 1.0–5.0. Particularly useful when NoiseCorrectionThreshold
    /// or NclLambda are enabled.
    /// </summary>
    double MaxGradNorm,
    /// <summary>
    /// When > 0, replaces binary 0/1 direction labels with a continuous soft label:
    ///   y_soft = sigmoid(signedMagnitude / AtrLabelSensitivity)
    /// where signedMagnitude = Magnitude × (+1 for Buy, −1 for Sell).
    /// Gives richer gradient signal for large/small ATR-normalised moves.
    /// 0.0 = use binary labels. Typical 1.0–3.0.
    /// </summary>
    double AtrLabelSensitivity,
    /// <summary>
    /// Minimum one-sided z-score required for champion-challenger promotion in
    /// <c>MLShadowArbiterWorker</c>. The z-score is computed from the two-proportion
    /// z-test comparing challenger vs champion accuracy.
    /// z ≥ 1.645 = 95% CI (default). z ≥ 1.282 = 90% CI.
    /// 0.0 = disabled (no z-score gate beyond the existing accuracy margin check).
    /// </summary>
    double ShadowMinZScore,
    /// <summary>
    /// L1 regularisation coefficient for elastic-net (L1 + L2) weight penalty.
    /// Applied via the proximal soft-thresholding operator after each Adam update:
    ///   w[j] ← sign(w[j]) × max(0, |w[j]| − L1Lambda × lr).
    /// Induces sparsity in the weight vector; complement to L2Lambda.
    /// 0.0 = disabled (pure L2/ridge). Typical 1e-5 – 1e-3.
    /// </summary>
    double L1Lambda,
    /// <summary>
    /// Quantile level τ for the asymmetric (pinball-loss) magnitude regressor.
    /// The regressor predicts the τ-th conditional quantile of ATR-normalised magnitude
    /// rather than the mean, enabling asymmetric risk sizing.
    /// 0.0 = disabled (use MSE regressor). 0.90 = 90th-percentile regressor (conservative sizing).
    /// </summary>
    double MagnitudeQuantileTau,
    /// <summary>
    /// Multi-task joint loss weight β. When > 0, each base learner also trains a
    /// separate linear magnitude head on the same feature representation. The combined
    /// gradient is ∂L_dir/∂w + β × ∂L_mag_huber/∂w_mag. This couples signal quality
    /// with reward magnitude for richer gradient signal.
    /// 0.0 = disabled (direction-only logistic). Typical 0.1–0.5.
    /// </summary>
    double MagLossWeight,
    /// <summary>
    /// Number of days of the most-recent training data treated as "current distribution"
    /// for density-ratio importance weighting. A logistic discriminator is trained to
    /// distinguish recent (label=1) vs historical (label=0) samples; per-sample weights
    /// p/(1−p) re-weight the bootstrap to focus on samples from the current distribution.
    /// 0 = disabled (uniform temporal decay only). Typical 30–90.
    /// </summary>
    int    DensityRatioWindowDays,
    /// <summary>
    /// Number of bars (samples) per trading day, used to convert
    /// <see cref="DensityRatioWindowDays"/> into a sample count for the incremental update
    /// window and density-ratio weighting. 24 = hourly (default), 96 = 15-min, 288 = 5-min.
    /// 0 falls back to 24.
    /// </summary>
    int    BarsPerDay,
    /// <summary>
    /// Durbin-Watson threshold below which autocorrelated magnitude residuals are flagged.
    /// DW ≈ 2 means no autocorrelation; DW &lt; 1.5 indicates positive autocorrelation.
    /// When DW &lt; threshold, a warning is logged and the statistic is stored in the snapshot
    /// for downstream workers to trigger AR-feature injection in the next training cycle.
    /// 0.0 = disabled. Typical 1.5.
    /// </summary>
    double DurbinWatsonThreshold,
    /// <summary>
    /// Multiplicative factor applied to the per-learner learning rate when the rolling
    /// validation accuracy drops more than 5 % below its epoch-best (adaptive LR decay).
    /// The decay triggers at most once per learner and makes the remaining cosine schedule
    /// start from the decayed value. 0.0 = disabled. Typical 0.3–0.5.
    /// </summary>
    double AdaptiveLrDecayFactor,
    /// <summary>
    /// When <c>true</c>, after training all K base learners the ensemble is pruned by
    /// removing any learner whose marginal OOB accuracy contribution is negative (i.e. the
    /// ensemble accuracy improves when that learner is excluded). Pruned learners are set
    /// to zero weights. The number of surviving learners is stored in the snapshot.
    /// </summary>
    bool   OobPruningEnabled,
    /// <summary>
    /// Features whose pairwise mutual information (MI) with another feature exceeds this
    /// fraction of log(2) (= maximum binary MI) are flagged as redundant. A log warning
    /// and a <c>ModelSnapshot.RedundantFeaturePairs</c> entry are emitted so operators can
    /// exclude one of the pair in a future run. 0.0 = disabled. Typical 0.70–0.90.
    /// </summary>
    double MutualInfoRedundancyThreshold,
    /// <summary>
    /// Minimum linear regression slope of per-fold Sharpe ratios across walk-forward folds.
    /// A significantly negative slope means model quality deteriorates across successive
    /// out-of-sample periods — an early indicator of regime instability.
    /// When the fitted slope falls below this threshold the model is rejected by the equity-
    /// curve gate. −99.0 = disabled. Typical −0.10.
    /// </summary>
    double MinSharpeTrendSlope,
    /// <summary>
    /// Temperature scaling factor T fitted on the calibration fold as a single-parameter
    /// alternative to Platt scaling: calibP = sigmoid(logit(rawP) / T).
    /// T > 1 softens the probabilities (wider prediction sets); T &lt; 1 sharpens them.
    /// When 0.0, temperature scaling is disabled and global Platt is used instead.
    /// Stored in <see cref="ModelSnapshot.TemperatureScale"/>.
    /// </summary>
    bool   FitTemperatureScale,
    /// <summary>
    /// Minimum allowed Brier Skill Score (BSS) for model promotion.
    /// BSS = 1 − Brier / Brier_naive, where Brier_naive = p_base × (1 − p_base).
    /// BSS &gt; 0 means the model beats a naive base-rate predictor.
    /// 0.0 = disabled. Typical 0.02–0.05.
    /// </summary>
    double MinBrierSkillScore,
    /// <summary>
    /// Exponential decay lambda λ applied to prediction log ages when fitting Platt
    /// recalibration in <c>MLRecalibrationWorker</c>.
    /// Weight_i = exp(−λ × days_ago_i). 0.0 = uniform weights (standard SGD).
    /// Typical 0.05–0.10 (≈ 7–14 day half-life).
    /// </summary>
    double RecalibrationDecayLambda,
    /// <summary>
    /// Maximum average pairwise Pearson correlation allowed between base learner weight
    /// vectors before a warning is logged. Unlike <see cref="MaxLearnerCorrelation"/> (which
    /// triggers re-init during training), this only stores the metric and logs a warning.
    /// Stored as <see cref="ModelSnapshot.EnsembleDiversity"/>. 1.0 = warning disabled.
    /// </summary>
    double MaxEnsembleDiversity,
    /// <summary>
    /// When <c>true</c>, augments the standard BCE loss with a Reverse Cross-Entropy (RCE)
    /// term: L_total = CE(p,y) + α × RCE(p,y). RCE saturates for confident wrong predictions,
    /// making gradient descent ignore likely-mislabelled triple-barrier timeout samples.
    /// Set <see cref="SymmetricCeAlpha"/> to control the balance. False = standard BCE only.
    /// </summary>
    bool   UseSymmetricCE,
    /// <summary>
    /// Weight of the Reverse Cross-Entropy term in the SCE loss (α).
    /// L_total = CE(p,y) + SymmetricCeAlpha × RCE(p,y).
    /// 0.0 = disabled. Typical 0.1–0.5. Only used when <see cref="UseSymmetricCE"/> is true.
    /// </summary>
    double SymmetricCeAlpha,
    /// <summary>
    /// Diversity regularisation coefficient λ. Adds −λ × 2(p_k − p̄)·p_k(1−p_k) to each
    /// learner's gradient, actively pushing learner probabilities away from the ensemble mean.
    /// Complements NCL (which pulls errors apart) by operating on the raw probability space.
    /// 0.0 = disabled. Typical 0.05–0.20.
    /// </summary>
    double DiversityLambda,
    /// <summary>
    /// When <c>true</c>, computes label smoothing ε from the training set's label-ambiguity
    /// proxy (fraction of samples with ATR-normalised magnitude below the 20th percentile)
    /// rather than using the fixed <see cref="LabelSmoothing"/> value.
    /// ε = clip(ambiguousFraction × 0.5, 0.01, 0.20). Stored in
    /// <see cref="ModelSnapshot.AdaptiveLabelSmoothing"/> for traceability.
    /// </summary>
    bool   UseAdaptiveLabelSmoothing,
    /// <summary>
    /// Exponential decay rate λ for model age confidence decay at inference.
    /// In <c>MLSignalScorer</c>: calibP ← 0.5 + (calibP − 0.5) × exp(−λ × daysSinceTrain).
    /// Smoothly shrinks the model's signal toward 0.5 as it ages, naturally discounting
    /// stale predictions while the next retrain is pending. 0.0 = disabled. Typical 0.005–0.02.
    /// </summary>
    double AgeDecayLambda,
    /// <summary>
    /// When <c>true</c> and a warm-start parent snapshot is available, computes per-sample
    /// novelty scores from the parent model's <see cref="ModelSnapshot.FeatureQuantileBreakpoints"/>
    /// and up-weights training samples that lie outside the parent model's inter-decile range
    /// (fraction of features outside [q10, q90]). Focuses the ensemble on samples from the
    /// current distribution. False = no covariate shift reweighting beyond density ratio.
    /// </summary>
    bool   UseCovariateShiftWeights,
    /// <summary>
    /// Maximum fraction of walk-forward folds that may fail the equity-curve gate
    /// (MaxFoldDrawdown or MinFoldCurveSharpe) before the entire training run is rejected.
    /// 0.5 = reject when more than half of folds are bad (default). 1.0 = gate disabled.
    /// </summary>
    double MaxBadFoldFraction,
    /// <summary>
    /// Minimum ratio of new model OOB accuracy to parent champion OOB accuracy for promotion.
    /// When a parent champion exists and the new model's OOB accuracy falls below
    /// parent × MinQualityRetentionRatio, the model is rejected regardless of absolute gates.
    /// 0.97 = allow at most 3% regression from parent. 0.0 = gate disabled (default).
    /// </summary>
    double MinQualityRetentionRatio,
    /// <summary>Rec #178: Weight of the magnitude head loss in multi-task training. Default 0.3.</summary>
    double MultiTaskMagnitudeWeight,
    /// <summary>Rec #179: Fraction of easiest samples used in the first curriculum epoch. Default 0.3.</summary>
    double CurriculumEasyFraction,
    /// <summary>Rec #180: Softmax temperature for self-distillation soft targets. Default 3.0.</summary>
    double SelfDistillTemp,
    /// <summary>Rec #182: FGSM adversarial perturbation magnitude ε. Default 0.01.</summary>
    double FgsmEpsilon,
    /// <summary>
    /// Minimum F1 score required for model promotion. Prevents single-class predictors
    /// (F1=0.000) from being promoted. 0.0 = disabled. Default 0.10.
    /// </summary>
    double MinF1Score,
    /// <summary>
    /// When true, applies inverse-frequency class weighting during training so the
    /// minority class receives higher loss penalty. Prevents majority-class collapse
    /// in imbalanced datasets. Default true.
    /// </summary>
    bool UseClassWeights,
    /// <summary>
    /// Number of top features (by index order) to check for pairwise MI redundancy.
    /// Higher values increase coverage but are O(topN²) in compute.
    /// 0 = use trainer default (10). Typical 10–30.
    /// </summary>
    int MutualInfoRedundancyTopN = 0,
    /// <summary>
    /// When true and <c>AdaBoostMaxTreeDepth ≥ 2</c>, uses a jointly-optimal depth-2 tree
    /// search that evaluates all (root, left-child, right-child) split combinations to minimise
    /// total weighted classification error.  O(F²·m·log m) per round vs O(F·m·log m) for the
    /// greedy default.  Recommended for F ≤ 30; disable for large feature sets.
    /// </summary>
    bool UseJointDepth2Search = false,
    /// <summary>Rec #398: Label smoothing epsilon for LabelSmoothModelTrainer. Null = 0.1.</summary>
    double? LabelSmoothEpsilon = null,
    /// <summary>Rec #399: Focal loss gamma for FocalLossModelTrainer. Null = 2.0.</summary>
    double? FocalGamma = null,
    /// <summary>Rec #400: K nearest neighbours for SMOTE oversampling. Null = 5.</summary>
    int?    SmoteKNeighbors = null,
    /// <summary>
    /// RNG seed for SMOTE synthetic sample generation. Null = derive a reproducible seed from
    /// training-data size so that different datasets produce different augmentations while
    /// a given dataset always produces the same synthetics. Set explicitly for full control.
    /// </summary>
    int?    SmoteSeed = null,
    /// <summary>Rec #416: NCDE hidden dimension. Null = 32.</summary>
    int? NcdeHiddenDim = null,
    /// <summary>Rec #418: DeepAR LSTM hidden size. Null = 64.</summary>
    int? DeepArHiddenSize = null,
    /// <summary>Rec #418: DeepAR number of forecast horizons. Null = 5.</summary>
    int? DeepArHorizons = null,
    /// <summary>Rec #422: MAF number of autoregressive transforms. Null = 5.</summary>
    int? MafTransforms = null,
    /// <summary>Rec #423: NSF number of spline knots. Null = 8.</summary>
    int? NsfKnots = null,
    /// <summary>Rec #424: N-HiTS number of stacks. Null = 3.</summary>
    int? NHitsStacks = null,
    /// <summary>Rec #425: PatchTST patch length p. Null = 16.</summary>
    int? PatchLength = null,
    /// <summary>Rec #426: iTransformer d_model dimension. Null = 64.</summary>
    int? ITransformerDModel = null,
    /// <summary>Rec #427: Diffusion Policy denoising steps. Null = 100.</summary>
    int? DiffusionPolicySteps = null,
    /// <summary>Rec #428: ENN epistemic index dimension. Null = 8.</summary>
    int? EnnIndexDim = null,
    /// <summary>Rec #431: DDPG replay buffer size. Null = 10000.</summary>
    int? DdpgBufferSize = null,
    /// <summary>Rec #440: S4D state size N. Null = 64.</summary>
    int? S4dStateSize = null,
    /// <summary>Rec #444: DDPM diffusion timesteps. Null = 1000.</summary>
    int? DdpmTimesteps = null,
    /// <summary>Rec #445: Crossformer router segment count. Null = 6.</summary>
    int? CrossformerSegments = null,
    // ── Recs #446–475 ────────────────────────────────────────────────────────
    int? TimesNetTopK = null,           // Rec #446: top-K periods detected by FFT
    int? SvgpInducingM = null,          // Rec #447: number of inducing points M (adaptive default: clamp(N/10, 20, 200))
    int? SvgpMiniBatchSize = null,      // Rec #447: mini-batch size for ELBO optimisation (default 256)
    int? EsnReservoirSize = null,        // Rec #448: ESN reservoir neuron count
    int? ElmHiddenSize = null,           // Rec #449: ELM hidden layer size
    int? DLinearMovingAvg = null,        // Rec #450: moving average kernel size
    int? FedformerModes = null,          // Rec #451: number of Fourier modes K
    int? CardDiffusionSteps = null,      // Rec #452: number of diffusion steps
    int? FitsFreqCutoff = null,          // Rec #454: low-pass frequency cutoff K
    int? AutoformerMovingAvg = null,     // Rec #455: moving average window for decomposition
    int? RetNetHeads = null,             // Rec #458: number of retention heads
    int? KoopmanLatentDim = null,        // Rec #459: Koopman observable space dimension
    int? EdlEvidenceHidden = null,       // Rec #460: EDL evidence network hidden size
    int? DssmLatentDim = null,           // Rec #461: DSSM latent state dimension
    int? LatentOdeLatentDim = null,      // Rec #462: latent ODE latent dimension
    int? GrudInputSize = null,           // Rec #463: GRU-D input size (number of features)
    int? WnnDecompositionLevels = null,  // Rec #464: DWT decomposition levels
    int? CvaeLatentDim = null,           // Rec #465: CVAE latent dimension
    int? HyenaOrder = null,              // Rec #467: Hyena operator order N
    int? ChronosContextLen = null,       // Rec #468: Chronos context window length
    int? GatHeads = null,                // Rec #471: GAT number of attention heads
    int? PinnCollocationPoints = null,   // Rec #472: number of PDE collocation points
    int? BnnNumLayers = null,            // Rec #473: BNN-VI number of layers
    int? RwkvLayers = null,              // Rec #474: RWKV number of layers
    // ── Recs #476–500 ────────────────────────────────────────────────────────
    int? Td3PolicyDelay = null,           // TD3: delayed policy update frequency (default 2)
    int? InformerProbSparseFactor = null, // Informer: ProbSparse sampling factor c (default 5)
    int? XlstmBlockSize = null,           // xLSTM: number of xLSTM blocks (default 4)
    int? TsMaeMaskRatio = null,           // TS-MAE: masking ratio percent (default 75)
    int? IqnQuantileCount = null,         // IQN: number of quantile samples N (default 32)
    int? DtContextLen = null,             // Decision Transformer: context window length (default 20)
    int? SngpSpectralNorm = null,         // SNGP: spectral norm iterations (default 1)
    int? MegaChunkSize = null,            // MEGA: chunk size for EMA (default 16)
    int? TimeMixerMixingDim = null,       // TimeMixer: mixing hidden dim (default 32)
    int? TsMixerHiddenDim = null,         // TSMixer: inter/intra MLP hidden dim (default 64)
    int? DreamerDynDim = null,            // Dreamer V3: dynamics model hidden dim (default 512)
    int? TransXlMemLen = null,            // Transformer-XL: memory segment length (default 16)
    int? DiffTsSteps = null,              // Diffusion-TS: denoising steps T (default 100)
    int? RainbowAtomCount = null,         // Rainbow DQN: distributional atoms (default 51)
    int? IqlExpectileCount = null,        // IQL: expectile regression count (default 10)
    int? CqlAlpha = null,                 // CQL: conservative penalty weight ×10 (default 10 = 1.0)
    int? MtlTaskCount = null,             // MTL: number of auxiliary tasks (default 3)
    int? DannLambda = null,               // DANN: gradient reversal lambda ×10 (default 10 = 1.0)
    int? DannFeatDim = null,              // DANN: feature extractor hidden dim (default 32)
    int? DannDomHid = null,               // DANN: domain classifier hidden dim (default 16)
    bool DannAbstentionF1Sweep = false,   // DANN: optimize abstention threshold via F1 sweep (vs fixed top-60%)
    double DannQuantileRegressorLr = 0.0, // DANN: quantile regressor learning rate (0.0 = LR/10)
    // ── Recs #501–510 ────────────────────────────────────────────────────────
    int? NBeatsBlocks = null,             // N-BEATS: number of stacked residual blocks (default 4)
    int? NLinearNorm = null,              // NLinear: apply last-value normalisation flag 0/1 (default 1)
    int? SciNetLevels = null,             // SCINet: binary-tree depth (default 2, max 2)
    int? EfficientZeroRollout = null,     // EfficientZero: world-model rollout steps (default 5)
    int? MocoQueueSize = null,            // MoCo v2: momentum-contrast queue size (default 256)
    // ── Recs #506–510 ────────────────────────────────────────────────────────
    int? LoraRank = null,                 // LoRA (#506): low-rank adaptation rank r (default 4)
    int? BekkPairs = null,               // BEKK-GARCH (#507): number of asset pairs K (default 2)
    int? FarimaMaxLag = null,            // FARIMA (#508): maximum lag for fractional differencing (default 20)
    int? QrfTrees = null,                // Quantile RF (#509): number of decision trees T (default 50)
    int? SiameseMargin = null,           // Siamese (#510): contrastive loss margin ×10 (default 10 = 1.0)
    /// <summary>
    /// Random seed for the Quantile RF trainer. 0 = non-deterministic (new Random() per run).
    /// Default 42 (fully deterministic). Change to any other non-zero value to vary the random
    /// forest construction without sacrificing reproducibility.
    /// </summary>
    int QrfSeed = 42,
    // ── Robustness & capacity improvements ────────────────────────────────────
    /// <summary>
    /// Maximum absolute weight value per learner dimension. Weights are clamped to
    /// [−MaxWeightMagnitude, +MaxWeightMagnitude] after each Adam update to prevent
    /// weight explosion. 0.0 = disabled. Typical 10.0.
    /// </summary>
    double MaxWeightMagnitude = 10.0,
    /// <summary>
    /// Mini-batch size for SGD. Each epoch iterates over mini-batches of this size
    /// instead of sample-by-sample updates. Larger batches give more stable Adam
    /// variance estimates. 1 = sample-by-sample SGD (legacy). Typical 32–64.
    /// </summary>
    int MiniBatchSize = 1,
    /// <summary>
    /// Conformal prediction coverage target (1−α). Default 0.90 = 90% coverage.
    /// Must be in (0, 1). Higher values produce wider prediction sets.
    /// </summary>
    double ConformalCoverage = 0.90,
    /// <summary>
    /// Minimum threshold for EV-optimal threshold sweep (percentage points).
    /// Default 30 (= 0.30). Paired with ThresholdSearchMax.
    /// </summary>
    int ThresholdSearchMin = 30,
    /// <summary>
    /// Maximum threshold for EV-optimal threshold sweep (percentage points).
    /// Default 75 (= 0.75). Paired with ThresholdSearchMin.
    /// </summary>
    int ThresholdSearchMax = 75,
    /// <summary>
    /// Number of hidden units in the per-learner MLP hidden layer. When > 0, each base
    /// learner is a 1-hidden-layer MLP (input → ReLU(hidden) → sigmoid) instead of a linear
    /// logistic regression. Dramatically increases representational capacity for non-linear
    /// feature interactions. 0 = linear logistic regression (legacy). Typical 16–64.
    /// </summary>
    int MlpHiddenDim = 0,
    /// <summary>
    /// Number of hidden layers in the per-learner MLP. Only used when <see cref="MlpHiddenDim"/> &gt; 0.
    /// 1 = single hidden layer, ReLU(W₁x + b₁) → sigmoid (default, legacy behaviour).
    /// 2 = two hidden layers of <see cref="MlpHiddenDim"/> units each with a ReLU between them,
    ///     substantially increasing non-linear capacity. Both layers are stored packed in the
    ///     existing MlpHiddenWeights array so snapshot format remains backward-compatible.
    /// Values ≥ 3 are treated as 2.
    /// </summary>
    int MlpHiddenLayers = 1,
    /// <summary>
    /// When <c>true</c> and a warm-start snapshot is provided, runs an incremental update
    /// pass (reduced epochs, lower LR) over only the most recent DensityRatioWindowDays of
    /// data instead of a full retrain. Much faster for adapting to regime changes.
    /// False = full retrain (default).
    /// </summary>
    bool UseIncrementalUpdate = false,
    /// <summary>
    /// Maximum depth of each regression tree in the GBM ensemble.
    /// Shallow trees (3–5) with many boosting rounds generalise best for financial data.
    /// 0 = use default (3). Typical 3–6.
    /// </summary>
    int GbmMaxDepth = 0,
    // ── ElmModelTrainer improvements ────────────────────────────────────────
    /// <summary>
    /// Outer random seed used to derive per-learner seeds in the ELM ensemble.
    /// Different values produce different bootstrap samples and random projections,
    /// enabling run-to-run variation. 0 = use the legacy deterministic scheme.
    /// </summary>
    int ElmOuterSeed = 0,
    /// <summary>
    /// Learning rate for the meta-label and abstention SGD sub-models.
    /// 0.0 = use default (0.01).
    /// </summary>
    double ElmSubModelLr = 0.0,
    /// <summary>
    /// Maximum training epochs for the meta-label and abstention SGD sub-models.
    /// 0 = use default (200).
    /// </summary>
    int ElmSubModelMaxEpochs = 0,
    /// <summary>
    /// Early-stopping patience for the meta-label and abstention SGD sub-models.
    /// 0 = use default (25).
    /// </summary>
    int ElmSubModelPatience = 0,
    /// <summary>
    /// Learning rate for the ELM magnitude regressor (Huber-loss SGD).
    /// 0.0 = use default (0.001).
    /// </summary>
    double ElmMagRegressorLr = 0.0,
    /// <summary>
    /// Maximum training epochs for the ELM magnitude regressor.
    /// 0 = use default (200).
    /// </summary>
    int ElmMagRegressorMaxEpochs = 0,
    /// <summary>
    /// Early-stopping patience for the ELM magnitude regressor.
    /// 0 = use default (15).
    /// </summary>
    int ElmMagRegressorPatience = 0,
    /// <summary>
    /// Sharpe ratio annualisation factor. Default 252 (equity trading days).
    /// Use 365 for 24/7 markets (crypto), 252 for equities, or a custom value.
    /// 0 = use default (252).
    /// </summary>
    double SharpeAnnualisationFactor = 0.0,
    /// <summary>
    /// When <c>true</c>, applies SMOTE (Synthetic Minority Oversampling) to the
    /// minority class before stratified bootstrap in the ELM trainer. Uses
    /// <see cref="SmoteKNeighbors"/> for the number of nearest neighbours.
    /// Only activates when the minority/majority class ratio is below
    /// <see cref="ElmSmoteMinorityRatioThreshold"/>.
    /// </summary>
    bool ElmUseSmote = false,
    /// <summary>
    /// Minority-to-majority class ratio below which SMOTE is triggered in the ELM trainer.
    /// 0.4 = activate SMOTE when the minority class is less than 40% of the majority.
    /// 0.0 = always apply SMOTE when ElmUseSmote is true.
    /// </summary>
    double ElmSmoteMinorityRatioThreshold = 0.4,
    /// <summary>
    /// Fraction of data allocated to the training split in the final model.
    /// 0.0 = adaptive (auto-selects based on dataset size: larger datasets use 0.70,
    /// smaller datasets use up to 0.80 to maximise training data).
    /// Must be in (0, 1) when set explicitly. Default 0.0 (adaptive).
    /// </summary>
    double ElmTrainSplitRatio = 0.0,
    /// <summary>
    /// Fraction of data allocated to the calibration split in the final model.
    /// 0.0 = adaptive (auto-selects based on dataset size: typically 0.10–0.15).
    /// Must be in (0, 1) when set explicitly. Default 0.0 (adaptive).
    /// </summary>
    double ElmCalSplitRatio = 0.0,
    /// <summary>
    /// Per-sample dropout rate applied to ELM hidden units during training.
    /// Each sample sees a different random sparsity mask, reducing co-adaptation.
    /// 0.0 = no dropout. Default 0.10 (10%). Typical 0.05–0.20.
    /// </summary>
    double ElmDropoutRate = 0.10,
    /// <summary>
    /// Hidden-layer activation function for ELM learners.
    /// Sigmoid (default), Tanh (zero-centered), or ReLU (sparse/unbounded).
    /// Different activations suit different feature distributions.
    /// </summary>
    ElmActivation ElmActivation = ElmActivation.Sigmoid,
    /// <summary>
    /// Maximum deviation (±) in hidden-layer size across learners, as a fraction of
    /// <see cref="ElmHiddenSize"/>. Each learner draws a hidden size uniformly from
    /// [H × (1 − variation), H × (1 + variation)], increasing architectural diversity.
    /// 0.0 = all learners use the same hidden size (default). Typical 0.10–0.30.
    /// </summary>
    double ElmHiddenSizeVariation = 0.0,
    /// <summary>
    /// Multiplicative weight applied to SMOTE-generated synthetic samples in the
    /// H^TH / H^TY accumulation. Lower values prevent synthetic samples from
    /// dominating the ridge solve. 1.0 = equal weight (default). Typical 0.3–0.7.
    /// </summary>
    double ElmSmoteSampleWeight = 1.0,
    /// <summary>
    /// Number of walk-forward CV folds for the ELM magnitude regressor.
    /// When > 1, the magnitude regressor is trained via temporal CV (expanding window
    /// with embargo) and parameters are averaged across folds, matching the rigor of
    /// the direction model's CV. 0 = single train/val split (default). Typical 3.
    /// </summary>
    int ElmMagRegressorCvFolds = 0,
    /// <summary>
    /// When <c>true</c>, each ELM base learner is assigned a different activation function
    /// (cycling through Sigmoid → Tanh → ReLU) to maximise ensemble diversity.
    /// False = all learners use <see cref="ElmActivation"/> (default).
    /// </summary>
    bool ElmMixActivations = false,
    /// <summary>
    /// When <c>true</c>, uses inverse-frequency class weights in the ridge solve
    /// instead of SMOTE synthetic sample generation. This avoids creating temporally
    /// impossible synthetic samples in time-series data while still addressing class imbalance.
    /// False = use SMOTE when <see cref="ElmUseSmote"/> is enabled (default).
    /// </summary>
    bool ElmUseClassWeights = true,
    /// <summary>
    /// Winsorize percentile for ELM features before Z-score standardization.
    /// Clips each feature to the [p, 1−p] quantile range, reducing the influence
    /// of adversarial outliers on the standardization statistics and ridge solve.
    /// 0.0 = disabled (default). Typical 0.01 (clip to p1/p99).
    /// </summary>
    double ElmWinsorizePercentile = 0.0,
    /// <summary>
    /// Winsorize percentile for TabNet features before Z-score standardization.
    /// Clips each feature to the [p, 1−p] quantile range computed on the training split,
    /// reducing the influence of adversarial outliers on standardization and gradient descent.
    /// 0.0 = disabled (default). Typical 0.01 (clip to p1/p99).
    /// </summary>
    double TabNetWinsorizePercentile = 0.0,
    /// <summary>
    /// When <c>true</c>, appends squared ELM hidden activation terms to the
    /// augmented magnitude feature space, enabling the magnitude regressor to capture
    /// non-linear patterns in predicted move size. The augmented dimension becomes
    /// <c>featureCount + 2 × hiddenSize</c> instead of <c>featureCount + hiddenSize</c>.
    /// Existing snapshots without quadratic terms are backward-compatible — inference
    /// checks <c>augWeights.Length</c> to determine whether squared terms are present.
    /// False = linear augmented regressor only (default).
    /// </summary>
    bool ElmMagQuadraticTerms = false,
    // ── TCN architecture hyperparams ─────────────────────────────────────
    /// <summary>
    /// Number of convolutional filters (output channels) per TCN block.
    /// Higher values increase capacity but also memory and compute.
    /// 0 = use default (32). Typical 16–64.
    /// </summary>
    int TcnFilters = 0,
    /// <summary>
    /// Number of stacked TCN residual blocks. More blocks increase the receptive field
    /// exponentially (via dilation doubling). The receptive field is 1 + (K−1) × Σ dilations.
    /// 0 = use default (4). Typical 3–6.
    /// </summary>
    int TcnNumBlocks = 0,
    /// <summary>
    /// Activation function for TCN convolutional blocks.
    /// ReLU (default) or GELU (smoother gradients, better for deeper networks).
    /// </summary>
    TcnActivation TcnActivation = TcnActivation.Relu,
    /// <summary>
    /// When <c>true</c>, applies layer normalisation after each convolutional block
    /// (before the activation function). Stabilises training and allows higher learning rates.
    /// False = no normalisation (legacy). Default true.
    /// </summary>
    bool TcnUseLayerNorm = true,
    /// <summary>
    /// When <c>true</c>, applies scaled dot-product attention pooling over all timestep
    /// hidden states instead of extracting only the last timestep. Captures long-range
    /// dependencies beyond the convolutional receptive field.
    /// False = last-timestep extraction (legacy). Default true.
    /// </summary>
    bool TcnUseAttentionPooling = true,
    /// <summary>
    /// Number of linear warmup epochs before cosine annealing begins.
    /// During warmup, LR ramps linearly from 0 to the base LR. Helps stability
    /// with LayerNorm + attention. 0 = no warmup (legacy). Typical 5–10.
    /// </summary>
    int TcnWarmupEpochs = 0,
    /// <summary>
    /// Number of attention heads for multi-head attention pooling.
    /// Each head attends to different temporal patterns independently.
    /// 1 = single-head (legacy). Typical 2–8. Must divide TcnFilters evenly.
    /// </summary>
    int TcnAttentionHeads = 1,
    /// <summary>
    /// When <c>true</c>, TCN blocks use gated activation: gate = σ(Conv_g) ⊙ tanh(Conv_f)
    /// instead of plain ReLU/GELU. Doubles per-block parameters but improves gradient flow
    /// and expressiveness for noisy financial data. Default false (plain activation).
    /// </summary>
    bool TcnUseGating = false,
    /// <summary>
    /// Block index at which the TCN backbone splits into separate direction and magnitude
    /// branches. E.g. 2 means blocks 0–1 are shared, blocks 2+ are task-specific.
    /// 0 = disabled (shared backbone for both heads, legacy). Typical: numBlocks − 1.
    /// </summary>
    int TcnLateSplitBlock = 0,
    /// <summary>
    /// Comma-separated kernel sizes per block (e.g. "3,3,5,5"). When empty, all blocks use
    /// the default kernel size 3. Wider kernels in deeper blocks capture longer patterns.
    /// </summary>
    string TcnKernelSizes = "",
    /// <summary>
    /// When <c>true</c>, TCN conv layers use depthwise separable convolutions (depthwise + pointwise)
    /// to reduce parameter count. Beneficial at higher filter counts (64+). Default false.
    /// </summary>
    bool TcnDepthwiseSeparable = false,
    /// <summary>
    /// Standard deviation of Gaussian noise added to gradients: g += N(0, σ/(1+t)^0.55).
    /// Helps escape sharp minima. 0.0 = disabled. Typical 0.01–0.1.
    /// </summary>
    double TcnGradientNoiseStd = 0.0,
    /// <summary>
    /// Learning rate multiplier for attention projection weights (Q, K, V).
    /// 1.0 = same as base LR (default). Typical 0.1–0.5 for fine-tuning.
    /// </summary>
    double TcnAttentionLrScale = 1.0,
    /// <summary>
    /// Learning rate multiplier for the classification and magnitude head weights.
    /// 1.0 = same as base LR (default). Typical 1.0–3.0 for faster head convergence.
    /// </summary>
    double TcnHeadLrScale = 1.0,
    /// <summary>
    /// When <c>true</c>, two forward passes with different dropout masks produce a KL divergence
    /// penalty term (R-Drop regularisation). Forces consistent predictions under different masks.
    /// Default false.
    /// </summary>
    bool TcnUseRDrop = false,
    /// <summary>
    /// R-Drop KL divergence penalty coefficient. Only used when TcnUseRDrop is true.
    /// 0.0 = disabled. Typical 0.1–1.0.
    /// </summary>
    double TcnRDropAlpha = 0.5,
    /// <summary>
    /// Number of epochs during which lower TCN blocks are frozen when warm-starting.
    /// After these epochs, blocks are unfrozen one at a time from top to bottom.
    /// 0 = disabled (all blocks trainable from epoch 0). Typical 5–15.
    /// </summary>
    int TcnProgressiveUnfreezeEpochs = 0,
    /// <summary>
    /// When <c>true</c> and a warm-start parent snapshot provides FeatureImportanceScores,
    /// the attention query weights are biased toward channels the parent found important.
    /// Default false.
    /// </summary>
    bool TcnUseChannelImportanceTransfer = false,
    /// <summary>
    /// When <c>true</c>, uses Combinatorial Purged Cross-Validation (CPCV) instead of
    /// standard expanding-window walk-forward CV. Provides unbiased backtest-adjusted
    /// Sharpe estimates. More expensive: O(C(n,k)) vs O(k). Default false.
    /// </summary>
    bool TcnUseCpcv = false,
    /// <summary>
    /// Number of Monte Carlo label-shuffled CV runs for permutation p-value computation.
    /// 0 = disabled. Typical 100–500. Tests whether observed accuracy beats random labels.
    /// </summary>
    int TcnMonteCarloPermutations = 0,
    /// <summary>
    /// When <c>true</c>, later walk-forward folds are weighted higher in aggregate metrics
    /// (recency-weighted) using exponential decay. Default false (equal fold weighting).
    /// </summary>
    bool TcnUseFoldWeighting = false,
    /// <summary>
    /// When <c>true</c>, validates that warm-start parent architecture config matches the
    /// current config and logs structured warnings on mismatch. Default true.
    /// </summary>
    bool TcnValidateWarmStartCompat = true,
    /// <summary>
    /// AdaBoost alpha shrinkage factor η applied to each stump weight: α_eff = η × α.
    /// Reduces variance (at the cost of requiring more rounds). 1.0 = no shrinkage (default).
    /// Typical 0.5–0.9. Only used by AdaBoostModelTrainer.
    /// </summary>
    double AdaBoostAlphaShrinkage = 1.0,
    /// <summary>
    /// Fraction of K new residual rounds added per warm-start generation.
    /// effectiveK = max(5, (int)(K × AdaBoostWarmStartRoundsFraction)).
    /// 0.0 = use default (1/3). Only used by AdaBoostModelTrainer.
    /// </summary>
    double AdaBoostWarmStartRoundsFraction = 0.0,
    /// <summary>
    /// When <c>true</c>, uses the SAMME.R (Real AdaBoost) weight update rule. Each base
    /// learner contributes weighted leaf probability estimates (½·logit(p)) instead of
    /// discrete ±1 votes, giving richer gradient signal. SAMME.R typically converges
    /// faster and calibrates better than discrete SAMME, especially with depth-2 trees.
    /// False = discrete SAMME (default). Only used by AdaBoostModelTrainer.
    /// </summary>
    bool UseSammeR = false,
    /// <summary>
    /// Maximum depth of each base learner tree in the AdaBoost ensemble.
    /// 1 = decision stump (default). 2 = depth-2 tree — greedily splits each child
    /// partition, capturing pairwise feature interactions at ≈3× more search cost per
    /// boosting round. Only used by AdaBoostModelTrainer.
    /// </summary>
    int AdaBoostMaxTreeDepth = 1,
    /// <summary>
    /// Maximum allowed average Population Stability Index (PSI) across features when comparing
    /// the current training distribution to the parent model's quantile breakpoints.
    /// When > 0 and the computed avg PSI exceeds this threshold, a warning is logged before
    /// training proceeds. PSI &lt; 0.10 = stable; 0.10–0.25 = moderate drift; &gt; 0.25 = significant.
    /// 0.0 = gate disabled (default). Typical 0.20–0.25.
    /// </summary>
    double QrfPsiDriftWarnThreshold = 0.0,
    /// <summary>
    /// Hidden-layer size for the 2-layer MLP magnitude regressor in the Quantile RF trainer.
    /// When > 0, a single ReLU hidden layer of this width replaces the linear Huber regressor,
    /// enabling non-linear magnitude prediction. Trained with Adam + Huber loss + cosine LR.
    /// 0 = linear regressor (default). Typical 16–64.
    /// </summary>
    int QrfMagHiddenDim = 0,
    /// <summary>
    /// Maximum depth of each decision tree in the Quantile RF ensemble.
    /// 0 = use default (6). Typical 4–10. Increase for complex, high-dimensional
    /// feature sets; decrease to reduce overfitting on small datasets.
    /// </summary>
    int QrfMaxDepth = 0,
    /// <summary>
    /// Minimum number of samples required to reside in a leaf node in the Quantile RF ensemble.
    /// 0 = use default (3). Higher values (5–10) reduce overfitting on noisy data.
    /// </summary>
    int QrfMinLeaf = 0,
    /// <summary>
    /// When <c>true</c>, aborts training with a zero-metric result if more than 30 % of
    /// training features appear non-stationary (|ρ₁| &gt; 0.97) and <see cref="FracDiffD"/> == 0.
    /// False = log a warning only (default). Enable when feature stationarity is a hard requirement.
    /// </summary>
    bool QrfStationarityGateEnabled = false,
    /// <summary>
    /// Minimum number of calibration samples required for isotonic calibration (PAVA).
    /// Below this threshold, isotonic calibration is skipped entirely to prevent overfitting.
    /// When the calibration set has between this value and 2x this value, leave-one-out
    /// cross-validation is used to guard against isotonic overfitting. Default 50.
    /// </summary>
    int MinIsotonicCalibrationSamples = 50,
    /// <summary>
    /// Number of linear warmup epochs before cosine annealing begins for the FT-Transformer.
    /// During warmup, LR ramps linearly from 0 to the base LR. Helps stability with
    /// LayerNorm + multi-head attention. 0 = no warmup (legacy). Typical 5–10.
    /// </summary>
    int FtWarmupEpochs = 0,
    /// <summary>
    /// EV-optimal threshold sweep step size in basis points (hundredths of a percent).
    /// 100 = 1% steps (legacy). 50 = 0.5% steps (finer). Typical 50–100.
    /// Must be > 0. Default 50.
    /// </summary>
    int ThresholdSearchStepBps = 50,
    /// <summary>
    /// Dropout rate for attention weights and FFN hidden activations in the FT-Transformer.
    /// 0.0 = no dropout. Default 0.10 (10%). Typical 0.05–0.20.
    /// </summary>
    double FtDropoutRate = 0.10,
    /// <summary>
    /// When <c>true</c>, adds a learnable per-head positional bias to attention scores
    /// in the FT-Transformer. The bias is a [NumHeads][S×S] matrix added before softmax,
    /// allowing the model to encode feature-ordering structure. False = no positional bias (default).
    /// </summary>
    bool FtUsePositionalEncoding = false,
    /// <summary>
    /// Row subsampling fraction for stochastic GBM (analogous to XGBoost subsample).
    /// Each boosting round trains on this fraction of the training set.
    /// 1.0 = no subsampling. Default 0.8. Typical 0.5–1.0.
    /// </summary>
    double GbmRowSubsampleRatio = 0.8,
    /// <summary>
    /// Minimum number of samples required in a leaf node (analogous to LightGBM min_data_in_leaf).
    /// Prevents overfitting on noisy financial data by requiring sufficient evidence per leaf.
    /// 0 = use default (4). Typical 10–30.
    /// </summary>
    int GbmMinSamplesLeaf = 0,
    /// <summary>
    /// Minimum loss reduction (gain) required to make a further partition on a leaf node
    /// (analogous to XGBoost gamma / min_split_loss). Suppresses marginal splits.
    /// 0.0 = any positive gain accepted (default). Typical 0.0–1.0.
    /// </summary>
    double GbmMinSplitGain = 0.0,
    /// <summary>
    /// When true, uses histogram-based split finding (256 bins) instead of exact scan.
    /// O(n + 256·m) per tree vs O(n·m·log n). Faster on large datasets, slightly less precise.
    /// </summary>
    bool GbmUseHistogramSplits = false,
    /// <summary>Number of histogram bins for split finding when GbmUseHistogramSplits=true. Default 256.</summary>
    int GbmHistogramBins = 256,
    /// <summary>
    /// When true, uses leaf-wise (best-first) tree growth instead of level-wise (depth-first).
    /// Produces deeper asymmetric trees that capture signal with fewer nodes. Default false.
    /// </summary>
    bool GbmUseLeafWiseGrowth = false,
    /// <summary>Maximum number of leaves when using leaf-wise growth. 0 = 2^maxDepth (default).</summary>
    int GbmMaxLeaves = 0,
    /// <summary>
    /// DART dropout rate — fraction of existing trees randomly dropped per boosting round.
    /// Combats over-specialization of later trees. 0.0 = standard GBM (default). Typical 0.05–0.15.
    /// </summary>
    double GbmDartDropRate = 0.0,
    /// <summary>
    /// Per-depth decay factor for GbmMinSplitGain. Effective gain threshold at depth d =
    /// GbmMinSplitGain × (1 − GbmMinSplitGainDecayPerDepth)^d. Allows broad root splits
    /// while suppressing noisy leaf splits. 0.0 = uniform threshold (default).
    /// </summary>
    double GbmMinSplitGainDecayPerDepth = 0.0,
    /// <summary>
    /// When true, applies learning rate annealing: lr × (1 − round/numRounds).
    /// Early trees make bold moves; late trees fine-tune. Default false.
    /// </summary>
    bool GbmShrinkageAnnealing = false,
    /// <summary>
    /// How often (in rounds) to check validation loss for early stopping.
    /// 0 = auto (every 5 rounds, or every round if numRounds &lt; 30). Default 0.
    /// </summary>
    int GbmValCheckFrequency = 0,
    /// <summary>
    /// JSON-encoded interaction constraints — groups of feature indices that may co-occur in a path.
    /// E.g. "[[0,1,2],[3,4]]" means features 0-2 can interact, 3-4 can interact, but not cross-group.
    /// Empty = no constraints (default).
    /// </summary>
    string GbmInteractionConstraints = "",
    /// <summary>
    /// Hidden dimension for meta-label MLP. 0 = single-layer logistic (default). Typical 8–16.
    /// </summary>
    int GbmMetaLabelHiddenDim = 0,
    /// <summary>
    /// Estimated spread/slippage cost per trade (in magnitude units) to subtract during
    /// EV-optimal threshold computation. 0.0 = no cost adjustment (default).
    /// </summary>
    double GbmEvThresholdSpreadCost = 0.0,
    /// <summary>
    /// When true, fits separate buy-abstention and sell-abstention thresholds
    /// instead of a single symmetric threshold. Default false.
    /// </summary>
    bool GbmUseSeparateAbstention = false,
    /// <summary>
    /// Reserved for a future GBM regime-labeled training contract. The current GBM trainer
    /// rejects this flag because <c>TrainingSample</c> does not yet carry per-sample regime labels.
    /// Default false.
    /// </summary>
    bool GbmRegimeConditioned = false,
    /// <summary>
    /// When true, computes sliding-window loss during training and auto-excludes
    /// early data segments that hurt recent-window performance. Default false.
    /// </summary>
    bool GbmConceptDriftGate = false,
    /// <summary>
    /// Reserved for a future GBM regime-labeled training contract. The current GBM trainer
    /// rejects this flag because <c>TrainingSample</c> does not yet carry per-sample regime labels.
    /// Default false.
    /// </summary>
    bool GbmRegimeAwareEarlyStopping = false,
    /// <summary>
    /// Maximum parallelism for walk-forward CV folds. 0 = unlimited (default).
    /// Prevents starving other workers on shared infrastructure.
    /// </summary>
    int GbmCvMaxParallelism = 0,
    /// <summary>
    /// When true, forces deterministic execution (sequential CV, fixed seeds).
    /// Slower but fully reproducible. Default false.
    /// </summary>
    bool GbmDeterministic = false,
    /// <summary>
    /// Maximum number of warm-start trees to keep. 0 = keep all (default).
    /// When set, prunes the tail of low-value prior trees before adding new rounds.
    /// </summary>
    int GbmMaxWarmStartTrees = 0,

    // ── QRF-specific v4.1 hyperparams ─────────────────────────────────────────

    /// <summary>
    /// Number of rounds for Greedy Ensemble Selection in the QRF trainer.
    /// 0 = use default (100). Higher values explore more tree combinations.
    /// </summary>
    int QrfGesRounds = 0,
    /// <summary>
    /// Early-stop patience for GES: stop if NLL hasn't improved for this many rounds.
    /// 0 = disabled (run all QrfGesRounds). Default 0. Typical 10–20.
    /// </summary>
    int QrfGesEarlyStopPatience = 0,
    /// <summary>
    /// Number of shuffle repeats for permutation feature importance.
    /// Averaged across repeats to reduce variance. 1 = single shuffle (default). Typical 3–5.
    /// </summary>
    int QrfPermutationRepeats = 1,
    /// <summary>
    /// When true, sweeps the abstention gate threshold on the calibration set
    /// to maximize precision at ≥50 % recall, instead of using the default 0.5.
    /// </summary>
    bool QrfAbstentionSweepEnabled = false,
    /// <summary>
    /// Leaf probability shrinkage factor toward the global base rate.
    /// leafP = shrinkage × globalBaseRate + (1 − shrinkage) × leafP.
    /// Reduces variance from small leaves. 0.0 = disabled (default). Typical 0.05–0.20.
    /// </summary>
    double QrfLeafShrinkage = 0.0,
    /// <summary>
    /// When true, aborts training if Durbin-Watson statistic falls below
    /// <see cref="DurbinWatsonThreshold"/>, indicating autocorrelated magnitude residuals.
    /// False = log warning only (default).
    /// </summary>
    bool QrfDurbinWatsonGateEnabled = false,
    /// <summary>
    /// Hard PSI drift threshold for QRF. When &gt; 0 and the average PSI between
    /// current training data and the parent model exceeds this value, warm-start
    /// is rejected and a cold retrain is forced. 0.0 = disabled (default). Typical 0.25.
    /// </summary>
    double QrfPsiDriftHardThreshold = 0.0,
    /// <summary>
    /// When true, aborts training if the median feature stability CoV from
    /// walk-forward CV exceeds <see cref="QrfMaxFeatureStabilityCov"/>.
    /// </summary>
    bool QrfFeatureStabilityGateEnabled = false,
    /// <summary>
    /// Maximum allowed median feature stability CoV (coefficient of variation
    /// of importance across CV folds). Models relying on unstable features are
    /// unreliable. 0.0 = disabled. Typical 1.0–2.0.
    /// </summary>
    double QrfMaxFeatureStabilityCov = 0.0,
    /// <summary>
    /// When true, uses magnitude-adjusted Kelly fraction: p − (1−p) × avgLoss/avgWin
    /// instead of the simplified 2p−1 formula. Default false.
    /// </summary>
    bool QrfUseAdjustedKelly = false,
    /// <summary>
    /// Maximum serialized model size in megabytes. Training aborts if the estimated
    /// snapshot would exceed this limit. 0 = unlimited (default). Typical 50–200.
    /// </summary>
    int QrfMaxModelSizeMb = 0,
    /// <summary>
    /// L2 regularisation coefficient for the quantile magnitude regressor.
    /// 0.0 = no regularisation (default). Typical 0.001–0.01.
    /// </summary>
    double QrfQuantileL2 = 0.0,
    /// <summary>
    /// Early stopping patience (epochs) for the quantile magnitude regressor.
    /// 0 = disabled (run all epochs). Default 0. Typical 10–20.
    /// </summary>
    int QrfQuantileEarlyStopPatience = 0,
    // ── ROCKET-specific improvements ──────────────────────────────────────────
    /// <summary>When true, uses MiniRocket ternary {-1, 0, 1} kernel weights instead of Gaussian.</summary>
    bool RocketUseMiniWeights = false,
    /// <summary>When true, generates channel-independent kernels per feature subgroup.</summary>
    bool RocketMultivariate = false,
    /// <summary>Number of linear warmup epochs before cosine annealing for ROCKET ridge training. 0 = disabled.</summary>
    int RocketWarmupEpochs = 0,
    /// <summary>Winsorize percentile (e.g. 0.01 = clip to p1/p99) before Z-score standardization. 0 = disabled.</summary>
    double RocketWinsorizePercentile = 0.0,
    /// <summary>Fraction of top kernels (by weight magnitude) to retain from warm-start parent. 0 = regenerate all.</summary>
    double RocketKernelRetentionFraction = 0.0,
    /// <summary>When true, uses sequential execution for walk-forward CV and permutation importance (deterministic).</summary>
    bool RocketDeterministicParallel = false,
    /// <summary>When true, applies L2 penalty to the bias term in ridge training.</summary>
    bool RocketRegularizeBias = false,
    /// <summary>Number of kernel subsets for MC kernel dropout epistemic uncertainty. 0 = disabled.</summary>
    int RocketKernelDropoutSubsets = 0,
    /// <summary>When true, uses combinatorial purged CV instead of walk-forward CV.</summary>
    bool RocketUseCpcv = false,
    /// <summary>When true, initializes feature pruning mask from parent model's importance scores.</summary>
    bool RocketUseParentImportanceForPruning = false,
    /// <summary>SMOTE target ratio — minority will be oversampled to this fraction of majority count. 1.0 = full parity.</summary>
    double SmoteTargetRatio = 1.0,
    /// <summary>When true, runs Edited Nearest Neighbors cleanup on SMOTE synthetics to remove noisy samples.</summary>
    bool SmoteEnnEnabled = false,
    /// <summary>
    /// Minimum acceptance rate required for promotion in legacy training flows.
    /// The current runtime path no longer enforces this gate, so 0.0 disables it.
    /// </summary>
    double MinAcceptanceRateToPromote = 0.0,
    // ── TabNet v3 architecture hyperparameters ───────────────────────────────
    /// <summary>Rec #389 v3: Number of sequential attention steps. Default 3.</summary>
    int TabNetSteps = 3,
    /// <summary>Rec #389 v3: Entropy-based sparsity regularisation coefficient for attention masks. Default 0.0001.</summary>
    double TabNetSparsity = 0.0001,
    /// <summary>Rec #389 v3: Hidden dimension (width) of each FC layer inside the Feature Transformer. Default = 8 × nSteps.</summary>
    int TabNetHiddenDim = 0,
    /// <summary>Rec #389 v3: Number of shared FC→BN→GLU blocks across all decision steps. Default 2.</summary>
    int TabNetSharedLayers = 2,
    /// <summary>Rec #389 v3: Number of step-specific FC→BN→GLU blocks. Default 2.</summary>
    int TabNetStepLayers = 2,
    /// <summary>Rec #389 v3: Prior-scale relaxation γ ∈ [1.0, 2.0] controlling feature reuse across steps. Default 1.5.</summary>
    double TabNetRelaxationGamma = 1.5,
    /// <summary>Rec #389 v3: Virtual batch size for Ghost Batch Normalization. Default 128.</summary>
    int TabNetGhostBatchSize = 128,
    /// <summary>Rec #389 v3: Output dimension of the Attentive Transformer FC layer. 0 = same as TabNetHiddenDim.</summary>
    int TabNetAttentionDim = 0,
    /// <summary>Rec #389 v3: Use true sparsemax (exact zeros) vs softmax fallback. Default true.</summary>
    bool TabNetUseSparsemax = true,
    /// <summary>Rec #389 v3: Enable Gated Linear Units (GLU) in the Feature Transformer. Default true.</summary>
    bool TabNetUseGlu = true,
    /// <summary>Rec #389 v3: Dropout rate applied after each FC block. Default 0.0.</summary>
    double TabNetDropoutRate = 0.0,
    /// <summary>Rec #389 v3: BN running-mean momentum. Default 0.98.</summary>
    double TabNetMomentumBn = 0.98,
    /// <summary>Rec #389 v3: Epochs for unsupervised encoder-decoder pre-training. 0 = disabled.</summary>
    int TabNetPretrainEpochs = 0,
    /// <summary>Rec #389 v3: Fraction of features masked during pre-training. Default 0.3.</summary>
    double TabNetPretrainMaskFraction = 0.3,
    /// <summary>Rec #389 v3: Linear LR warmup epochs before cosine decay kicks in. 0 = disabled. Default 0.</summary>
    int TabNetWarmupEpochs = 0,
    /// <summary>
    /// Huber loss delta threshold for the magnitude regression head. Gradients for
    /// residuals within ±δ are linear (MSE-like); beyond δ they are capped (MAE-like).
    /// Smaller δ is more robust to outlier magnitudes but provides less gradient signal.
    /// 0.0 = use default (1.0). Typical 0.5–2.0.
    /// </summary>
    double TabNetHuberDelta = 0.0,
    /// <summary>
    /// Maximum training epochs for the Platt / temperature / conditional-Platt calibration
    /// gradient descent loops. More epochs allow finer calibration but increase training time.
    /// 0 = use default (200). Typical 100–500.
    /// </summary>
    int TabNetCalibrationEpochs = 0,
    /// <summary>
    /// Learning rate for the Platt / temperature / conditional-Platt calibration SGD.
    /// 0.0 = use default (0.01). Typical 0.005–0.05.
    /// </summary>
    double TabNetCalibrationLr = 0.0,
    /// <summary>
    /// Minimum number of samples required in a dataset split (calibration, test, conditional
    /// branch) before calibration, evaluation, or importance computation is attempted.
    /// Splits with fewer samples are skipped or use fallback values.
    /// 0 = use default (10). Typical 10–30.
    /// </summary>
    int TabNetMinCalibrationSamples = 0,
    /// <summary>
    /// Explicit FT-Transformer attention-head override.
    /// 0 = use the warm-start architecture when available, otherwise the trainer default.
    /// </summary>
    int FtTransformerHeads = 0,
    /// <summary>
    /// Explicit FT-Transformer stacked-layer override.
    /// 0 = use the warm-start architecture when available, otherwise the trainer default.
    /// </summary>
    int FtTransformerArchitectureNumLayers = 0,
    /// <summary>
    /// Deterministic root seed for trainers that need repeatable bagging, validation splits,
    /// and auxiliary searches. 0 falls back to a trainer-specific default.
    /// </summary>
    int TrainingRandomSeed = 42,
    double AdaBoostWinsorizePercentile = 0.0,
    double AdaBoostMaxAdversarialAuc = 0.0,
    double ElmMaxAdversarialAuc = 0.0,
    double GbmMaxAdversarialAuc = 0.0,
    double TcnMaxAdversarialAuc = 0.0,
    double BaggedLogisticMaxAdversarialAuc = 0.0,
    double DannMaxAdversarialAuc = 0.0,
    double FtTransformerMaxAdversarialAuc = 0.0,
    double QrfMaxAdversarialAuc = 0.0,
    double RocketMaxAdversarialAuc = 0.0,
    double SmoteMaxAdversarialAuc = 0.0,
    double SvgpMaxAdversarialAuc = 0.0
    );

// ── Evaluation metrics ────────────────────────────────────────────────────────

/// <summary>
/// Out-of-sample evaluation metrics returned by <see cref="IBaggedTrainer"/>
/// after every training fold and the final model.
/// </summary>
public record EvalMetrics(
    double Accuracy,
    double Precision,
    double Recall,
    double F1,
    double MagnitudeRmse,
    double ExpectedValue,
    double BrierScore,
    double WeightedAccuracy,
    double SharpeRatio,
    int    TP,
    int    FP,
    int    FN,
    int    TN,
    /// <summary>
    /// Out-of-bag accuracy estimate — free generalization estimate from the bootstrap
    /// samples each learner did not train on (~37% of data per learner).
    /// 0.0 when OOB estimation is disabled or insufficient samples are available.
    /// </summary>
    double OobAccuracy = 0.0);

// ── Walk-forward cross-validation result ─────────────────────────────────────

/// <summary>Aggregated metrics across all walk-forward CV folds.</summary>
public record WalkForwardResult(
    double AvgAccuracy,
    double StdAccuracy,
    double AvgF1,
    double AvgEV,
    double AvgSharpe,
    int    FoldCount,
    double StdF1     = 0.0,
    double StdEV     = 0.0,
    double StdSharpe = 0.0,
    /// <summary>
    /// Linear regression slope of per-fold Sharpe ratios (fold index as x).
    /// Negative = deteriorating OOS performance across time.
    /// 0.0 when fewer than 3 folds completed.
    /// </summary>
    double   SharpeTrend = 0.0,
    /// <summary>
    /// Per-feature walk-forward stability score: coefficient of variation (σ/μ) of the
    /// mean absolute weight magnitude across all CV folds. Low CV = stable feature contribution;
    /// high CV = feature importance varies erratically across time periods (unreliable).
    /// Null / empty when fewer than 2 folds completed. Stored in
    /// <see cref="ModelSnapshot.FeatureStabilityScores"/>.
    /// </summary>
    double[]? FeatureStabilityScores = null,
    /// <summary>Per-fold metrics for downstream regime-aware model selection.</summary>
    WalkForwardFoldMetric[]? FoldMetrics = null,
    /// <summary>
    /// Chronologically blocked out-of-fold probability residuals |p_oof - y|.
    /// Persisted into TabNet snapshots as a more faithful uncertainty baseline
    /// than the previous infinitesimal-jackknife approximation.
    /// </summary>
    double[]? OofResiduals = null,
    /// <summary>Linear regression slope of fold accuracies over time. Negative = degrading.</summary>
    double AccuracyDecayRate = 0.0,
    /// <summary>Linear regression slope of fold F1 scores over time. Negative = degrading.</summary>
    double F1DecayRate = 0.0,
    /// <summary>Std. dev. of fold-level Platt A parameters fitted on non-test calibration windows.</summary>
    double RecalibrationStabilityA = 0.0,
    /// <summary>Std. dev. of fold-level Platt B parameters fitted on non-test calibration windows.</summary>
    double RecalibrationStabilityB = 0.0);

/// <summary>Per-fold walk-forward CV metrics for granular analysis.</summary>
public record WalkForwardFoldMetric(double Accuracy, double F1, double EV, double Sharpe, double MaxDD);

// ── Training result ───────────────────────────────────────────────────────────

/// <summary>
/// Complete result returned by <see cref="IMLModelTrainer.TrainAsync"/>.
/// </summary>
public record TrainingResult(
    EvalMetrics      FinalMetrics,
    WalkForwardResult CvResult,
    byte[]           ModelBytes);

/// <summary>
/// Typed, versioned replay descriptor for model-specific feature-pipeline transforms.
/// Used to rebuild augmented features deterministically at inference time.
/// </summary>
public class FeatureTransformDescriptor
{
    public string Kind { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public string Operation { get; set; } = string.Empty;
    public int InputFeatureCount { get; set; }
    public int OutputStartIndex { get; set; }
    public int OutputCount { get; set; }
    public int[][] SourceIndexGroups { get; set; } = [];
}

/// <summary>
/// Structured trace entry for the lightweight TabNet architecture search.
/// Persisted into the snapshot for reproducibility and auditability.
/// </summary>
public class TabNetAutoTuneTraceEntry
{
    public int Steps { get; set; }
    public int HiddenDim { get; set; }
    public int AttentionDim { get; set; }
    public double Gamma { get; set; }
    public double DropoutRate { get; set; }
    public double SparsityCoeff { get; set; }
    public double Score { get; set; }
    public double CvAccuracy { get; set; }
    public double CvF1 { get; set; }
    public double CvExpectedValue { get; set; }
    public double CvSharpe { get; set; }
    public double CvStdAccuracy { get; set; }
    public double HoldoutAccuracy { get; set; }
    public double HoldoutF1 { get; set; }
    public double HoldoutExpectedValue { get; set; }
    public double HoldoutSharpe { get; set; }
    public double HoldoutBrier { get; set; }
    public double HoldoutEce { get; set; }
    public double HoldoutThreshold { get; set; }
    public int TuneTrainSampleCount { get; set; }
    public int TuneHoldoutSampleCount { get; set; }
    public int HoldoutStartIndex { get; set; }
    public int HoldoutCount { get; set; }
    public int CvFoldCount { get; set; }
    public string HoldoutSplitName { get; set; } = "SELECTION";
    public string HoldoutSliceHash { get; set; } = string.Empty;
    public string ScoreBreakdown { get; set; } = string.Empty;
    public string[] RejectionReasons { get; set; } = [];
    public bool Selected { get; set; }
}

/// <summary>
/// Structured artifact describing the final TabNet prune-and-retrain decision.
/// </summary>
public class TabNetPruningDecisionArtifact
{
    public bool Accepted { get; set; }
    public double BaselineScore { get; set; }
    public double CandidateScore { get; set; }
    public double ScoreDelta { get; set; }
    public double CandidateAccuracy { get; set; }
    public double CandidateBrier { get; set; }
    public double CandidateEce { get; set; }
    public int PrunedFeatureCount { get; set; }
    public int RetainedFeatureCount { get; set; }
    public string SelectionSplitName { get; set; } = "SELECTION";
    public int SelectionSampleCount { get; set; }
    public int CalibrationSampleCount { get; set; }
    public double BaselineThreshold { get; set; }
    public double CandidateThreshold { get; set; }
    public string[] Reasons { get; set; } = [];
}

/// <summary>
/// Structured artifact describing the final TabNet model audit.
/// </summary>
public class TabNetAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public int ActiveFeatureCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public double MaxTransformReplayShift { get; set; }
    public double MaxMaskApplicationShift { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public double MaxUncertaintyObserved { get; set; }
    public double RecordedEce { get; set; }
    public string FeatureSchemaFingerprint { get; set; } = string.Empty;
    public string PreprocessingFingerprint { get; set; } = string.Empty;
    public string[] Findings { get; set; } = [];
}

/// <summary>
/// Structured warm-start compatibility and reuse summary for TabNet.
/// </summary>
public class TabNetWarmStartArtifact
{
    public bool Compatible { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
    public int Attempted { get; set; }
    public int Reused { get; set; }
    public int Resized { get; set; }
    public int Skipped { get; set; }
    public int Rejected { get; set; }
    public double ReuseRatio { get; set; }
}

/// <summary>
/// Structured artifact describing the final deployed TabNet calibration stack.
/// </summary>
public class TabNetCalibrationArtifact
{
    public string SelectedGlobalCalibration { get; set; } = "PLATT";
    public string CalibrationSelectionStrategy { get; set; } = "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS";
    public double GlobalPlattNll { get; set; }
    public double TemperatureNll { get; set; }
    public bool TemperatureSelected { get; set; }
    public int FitSampleCount { get; set; }
    public int DiagnosticsSampleCount { get; set; }
    public double DiagnosticsSelectedGlobalNll { get; set; }
    public double DiagnosticsSelectedStackNll { get; set; }
    public int ConformalSampleCount { get; set; }
    public int MetaLabelSampleCount { get; set; }
    public int AbstentionSampleCount { get; set; }
    public string AdaptiveHeadMode { get; set; } = "SHARED";
    public int AdaptiveHeadCrossFitFoldCount { get; set; }
    public double ConditionalRoutingThreshold { get; set; } = 0.5;
    public int BuyBranchSampleCount { get; set; }
    public double BuyBranchBaselineNll { get; set; }
    public double BuyBranchFittedNll { get; set; }
    public bool BuyBranchAccepted { get; set; }
    public int SellBranchSampleCount { get; set; }
    public double SellBranchBaselineNll { get; set; }
    public double SellBranchFittedNll { get; set; }
    public bool SellBranchAccepted { get; set; }
    public int IsotonicSampleCount { get; set; }
    public int IsotonicBreakpointCount { get; set; }
    public double PreIsotonicNll { get; set; }
    public double PostIsotonicNll { get; set; }
    public bool IsotonicAccepted { get; set; }
}

/// <summary>
/// Compact metrics persisted for key TabNet model-selection/evaluation splits.
/// </summary>
public class TabNetMetricSummary
{
    public string SplitName { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double Threshold { get; set; }
    public double Accuracy { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1 { get; set; }
    public double ExpectedValue { get; set; }
    public double BrierScore { get; set; }
    public double WeightedAccuracy { get; set; }
    public double SharpeRatio { get; set; }
    public double Ece { get; set; }
}

/// <summary>
/// Compact metrics persisted for key FT-Transformer model-selection/evaluation splits.
/// </summary>
public class FtTransformerMetricSummary
{
    public string SplitName { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double Threshold { get; set; }
    public double Accuracy { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1 { get; set; }
    public double ExpectedValue { get; set; }
    public double BrierScore { get; set; }
    public double WeightedAccuracy { get; set; }
    public double SharpeRatio { get; set; }
    public double Ece { get; set; }
}

/// <summary>
/// Compact metrics persisted for key GBM model-selection/evaluation splits.
/// </summary>
public class GbmMetricSummary
{
    public string SplitName { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double Threshold { get; set; }
    public double Accuracy { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1 { get; set; }
    public double ExpectedValue { get; set; }
    public double BrierScore { get; set; }
    public double WeightedAccuracy { get; set; }
    public double SharpeRatio { get; set; }
    public double Ece { get; set; }
}

/// <summary>
/// Structured FT-Transformer calibration summary persisted with the snapshot.
/// </summary>
public class FtTransformerCalibrationArtifact
{
    public string SelectedGlobalCalibration { get; set; } = "PLATT";
    public string CalibrationSelectionStrategy { get; set; } = "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS";
    public double GlobalPlattNll { get; set; }
    public double TemperatureNll { get; set; }
    public double GlobalPlattEce { get; set; }
    public double TemperatureEce { get; set; }
    public bool TemperatureSelected { get; set; }
    public string AdaptiveHeadMode { get; set; } = "DISJOINT_DIAGNOSTICS";
    public int AdaptiveHeadCrossFitFoldCount { get; set; }
    public int FitSampleCount { get; set; }
    public int DiagnosticsSampleCount { get; set; }
    public int RefitSampleCount { get; set; }
    public int ThresholdSelectionSampleCount { get; set; }
    public int KellySelectionSampleCount { get; set; }
    public double DiagnosticsSelectedGlobalNll { get; set; }
    public double DiagnosticsSelectedGlobalEce { get; set; }
    public double DiagnosticsSelectedStackNll { get; set; }
    public double DiagnosticsSelectedStackEce { get; set; }
    public int ConformalSampleCount { get; set; }
    public string ConformalSelectionStrategy { get; set; } = "DISJOINT_HOLDOUT";
    public double ConditionalRoutingThreshold { get; set; } = 0.5;
    public int RoutingThresholdCandidateCount { get; set; }
    public double RoutingThresholdSelectedNll { get; set; }
    public double RoutingThresholdSelectedEce { get; set; }
    public double[] RoutingThresholdCandidates { get; set; } = [];
    public double[] RoutingThresholdCandidateNlls { get; set; } = [];
    public double[] RoutingThresholdCandidateEces { get; set; } = [];
    public int BuyBranchSampleCount { get; set; }
    public double BuyBranchBaselineNll { get; set; }
    public double BuyBranchFittedNll { get; set; }
    public bool BuyBranchAccepted { get; set; }
    public int SellBranchSampleCount { get; set; }
    public double SellBranchBaselineNll { get; set; }
    public double SellBranchFittedNll { get; set; }
    public bool SellBranchAccepted { get; set; }
    public int IsotonicSampleCount { get; set; }
    public int IsotonicBreakpointCount { get; set; }
    public double PreIsotonicNll { get; set; }
    public double PostIsotonicNll { get; set; }
    public double PreIsotonicEce { get; set; }
    public double PostIsotonicEce { get; set; }
    public bool IsotonicAccepted { get; set; }
    public double[] SelectedStackCrossFitFoldNlls { get; set; } = [];
    public double[] SelectedStackCrossFitFoldEces { get; set; } = [];
}

/// <summary>
/// Structured GBM calibration summary persisted with the snapshot.
/// </summary>
public class GbmCalibrationArtifact
{
    public string SelectedGlobalCalibration { get; set; } = "PLATT";
    public string CalibrationSelectionStrategy { get; set; } = "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS";
    public double GlobalPlattNll { get; set; }
    public double TemperatureNll { get; set; }
    public bool TemperatureSelected { get; set; }
    public int FitSampleCount { get; set; }
    public int DiagnosticsSampleCount { get; set; }
    public double DiagnosticsSelectedGlobalNll { get; set; }
    public double DiagnosticsSelectedStackNll { get; set; }
    public int ConformalSampleCount { get; set; }
    public int MetaLabelSampleCount { get; set; }
    public int AbstentionSampleCount { get; set; }
    public string AdaptiveHeadMode { get; set; } = "SHARED";
    public int AdaptiveHeadCrossFitFoldCount { get; set; }
    public double ConditionalRoutingThreshold { get; set; } = 0.5;
    public int BuyBranchSampleCount { get; set; }
    public double BuyBranchBaselineNll { get; set; }
    public double BuyBranchFittedNll { get; set; }
    public bool BuyBranchAccepted { get; set; }
    public int SellBranchSampleCount { get; set; }
    public double SellBranchBaselineNll { get; set; }
    public double SellBranchFittedNll { get; set; }
    public bool SellBranchAccepted { get; set; }
    public int IsotonicSampleCount { get; set; }
    public int IsotonicBreakpointCount { get; set; }
    public double PreIsotonicNll { get; set; }
    public double PostIsotonicNll { get; set; }
    public bool IsotonicAccepted { get; set; }
}

public class GbmDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class GbmWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedTreeCount { get; set; }
    public int TotalParentTrees { get; set; }
    public double ReuseRatio { get; set; }
    public bool PreprocessingReused { get; set; }
    public bool FeatureLayoutInherited { get; set; }
    public bool OobReplayApplied { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

/// <summary>
/// Structured TCN calibration summary persisted with the snapshot.
/// </summary>
public class TcnCalibrationArtifact
{
    public string SelectedGlobalCalibration { get; set; } = "PLATT";
    public string CalibrationSelectionStrategy { get; set; } = "FIT_ON_CALIBRATION_HOLDOUT";
    public double GlobalPlattA { get; set; } = 1.0;
    public double GlobalPlattB { get; set; }
    public double TemperatureScale { get; set; }
    public double BuyBranchPlattA { get; set; } = 1.0;
    public double BuyBranchPlattB { get; set; }
    public double SellBranchPlattA { get; set; } = 1.0;
    public double SellBranchPlattB { get; set; }
    public double ConditionalRoutingThreshold { get; set; } = 0.5;
    public double[] IsotonicBreakpoints { get; set; } = [];
    public double OptimalThreshold { get; set; } = 0.5;
    public double ConformalQHat { get; set; } = 1.0;
    public int CalibrationSampleCount { get; set; }
    public int DiagnosticsSampleCount { get; set; }
    public int BuyBranchSampleCount { get; set; }
    public int SellBranchSampleCount { get; set; }
    public int IsotonicSampleCount { get; set; }
    public int IsotonicBreakpointCount { get; set; }
}

/// <summary>
/// Structured TCN warm-start compatibility and reuse summary.
/// </summary>
public class TcnWarmStartArtifact
{
    public bool Compatible { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
    public int ReusedBlockCount { get; set; }
    public int DroppedBlockCount { get; set; }
    public double ReuseRatio { get; set; }
}

/// <summary>
/// Structured TCN post-train parity and contract audit artifact.
/// </summary>
public class TcnAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public int ActiveChannelCount { get; set; }
    public int RawFeatureCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public double RecordedEce { get; set; }
    public string FeatureSchemaFingerprint { get; set; } = string.Empty;
    public string PreprocessingFingerprint { get; set; } = string.Empty;
    public string[] Findings { get; set; } = [];
}

public class TcnDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

/// <summary>
/// Compact metrics persisted for key AdaBoost model-selection/evaluation splits.
/// </summary>
public class AdaBoostMetricSummary
{
    public string SplitName { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double Threshold { get; set; }
    public double Accuracy { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1 { get; set; }
    public double ExpectedValue { get; set; }
    public double BrierScore { get; set; }
    public double WeightedAccuracy { get; set; }
    public double SharpeRatio { get; set; }
    public double Ece { get; set; }
}

/// <summary>
/// Structured AdaBoost calibration summary persisted with the snapshot.
/// </summary>
public class AdaBoostCalibrationArtifact
{
    public string SelectedGlobalCalibration { get; set; } = "PLATT";
    public string CalibrationSelectionStrategy { get; set; } = "FIT_AND_EVAL_ON_SHARED_CALIBRATION";
    public double GlobalPlattNll { get; set; }
    public double TemperatureNll { get; set; }
    public bool TemperatureSelected { get; set; }
    public int FitSampleCount { get; set; }
    public int DiagnosticsSampleCount { get; set; }
    public int ThresholdSelectionSampleCount { get; set; }
    public int KellySelectionSampleCount { get; set; }
    public double DiagnosticsSelectedGlobalNll { get; set; }
    public double DiagnosticsSelectedStackNll { get; set; }
    public int ConformalSampleCount { get; set; }
    public int BuyConformalSampleCount { get; set; }
    public int SellConformalSampleCount { get; set; }
    public int MetaLabelSampleCount { get; set; }
    public int AbstentionSampleCount { get; set; }
    public string AdaptiveHeadMode { get; set; } = "SHARED";
    public int AdaptiveHeadCrossFitFoldCount { get; set; }
    public double ConditionalRoutingThreshold { get; set; } = 0.5;
    public int BuyBranchSampleCount { get; set; }
    public double BuyBranchBaselineNll { get; set; }
    public double BuyBranchFittedNll { get; set; }
    public bool BuyBranchAccepted { get; set; }
    public int SellBranchSampleCount { get; set; }
    public double SellBranchBaselineNll { get; set; }
    public double SellBranchFittedNll { get; set; }
    public bool SellBranchAccepted { get; set; }
    public int IsotonicSampleCount { get; set; }
    public int IsotonicBreakpointCount { get; set; }
    public double PreIsotonicNll { get; set; }
    public double PostIsotonicNll { get; set; }
    public bool IsotonicAccepted { get; set; }
}

/// <summary>
/// Structured AdaBoost post-train parity and contract audit artifact.
/// </summary>
public class AdaBoostAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public int ActiveFeatureCount { get; set; }
    public int RawFeatureCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public double RecordedEce { get; set; }
    public string FeatureSchemaFingerprint { get; set; } = string.Empty;
    public string PreprocessingFingerprint { get; set; } = string.Empty;
    public string[] Findings { get; set; } = [];
}

public class AdaBoostDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class ElmDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class ElmWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedLearnerCount { get; set; }
    public int TotalParentLearners { get; set; }
    public double ReuseRatio { get; set; }
    public bool InputWeightsTransferred { get; set; }
    public bool PruningRemapped { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

public class ElmAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public string[] Findings { get; set; } = [];
}

public class BaggedLogisticDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class BaggedLogisticWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedLearnerCount { get; set; }
    public int TotalParentLearners { get; set; }
    public double ReuseRatio { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

public class BaggedLogisticAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public string[] Findings { get; set; } = [];
}

public class QrfDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class QrfWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedTreeCount { get; set; }
    public int TotalParentTrees { get; set; }
    public double ReuseRatio { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

public class QrfAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public string[] Findings { get; set; } = [];
}

public class DannDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class DannWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedLayerCount { get; set; }
    public int TotalParentLayers { get; set; }
    public double ReuseRatio { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

public class DannAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public string[] Findings { get; set; } = [];
}

public class AdaBoostWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedStumpCount { get; set; }
    public int SkippedStumpCount { get; set; }
    public int TotalParentStumps { get; set; }
    public double ReuseRatio { get; set; }
    public bool WeightReplayApplied { get; set; }
    public bool WeightReplaySkippedDueToRegimeChange { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

/// <summary>
/// FT-Transformer warm-start compatibility summary.
/// </summary>
public class FtTransformerWarmStartArtifact
{
    public bool Compatible { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
    public int ReusedLayerCount { get; set; }
    public int RestoredPositionalBiasBlocks { get; set; }
    public int DroppedLayerCount { get; set; }
    public double ReuseRatio { get; set; }
}

/// <summary>
/// Structured FT-Transformer post-train parity and contract audit artifact.
/// </summary>
public class FtTransformerAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public int ActiveFeatureCount { get; set; }
    public int RawFeatureCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public double RecordedEce { get; set; }
    public string FeatureSchemaFingerprint { get; set; } = string.Empty;
    public string PreprocessingFingerprint { get; set; } = string.Empty;
    public string[] Findings { get; set; } = [];
}

/// <summary>
/// Structured GBM post-train parity and contract audit artifact.
/// </summary>
public class GbmAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public int ActiveFeatureCount { get; set; }
    public int RawFeatureCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public double MaxTransformReplayShift { get; set; }
    public double MaxMaskApplicationShift { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public double RecordedEce { get; set; }
    public string FeatureSchemaFingerprint { get; set; } = string.Empty;
    public string PreprocessingFingerprint { get; set; } = string.Empty;
    public string[] Findings { get; set; } = [];
}

/// <summary>
/// Structured TabNet drift/stationarity diagnostics computed on the final fit split.
/// </summary>
public class TabNetDriftArtifact
{
    public int SampleCount { get; set; }
    public int FeatureCount { get; set; }
    public int NonStationaryFeatureCount { get; set; }
    public double NonStationaryFeatureFraction { get; set; }
    public double MeanLag1Autocorrelation { get; set; }
    public double MaxLag1Autocorrelation { get; set; }
    public double MeanVarianceRatioDistance { get; set; }
    public double MaxVarianceRatioDistance { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MaxPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MaxChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MaxAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double MaxKpssLikeStatistic { get; set; }
    public double MeanRecentMeanShiftScore { get; set; }
    public double MaxRecentMeanShiftScore { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
}

public class RocketDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class RocketWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedKernelCount { get; set; }
    public int TotalParentKernels { get; set; }
    public double ReuseRatio { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

public class RocketAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public string[] Findings { get; set; } = [];
}

public class FtTransformerDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class SmoteDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class SmoteWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedLearnerCount { get; set; }
    public int TotalParentLearners { get; set; }
    public double ReuseRatio { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

public class SmoteAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public string[] Findings { get; set; } = [];
}

public class SvgpDriftArtifact
{
    public int NonStationaryFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
    public double NonStationaryFraction { get; set; }
    public bool GateTriggered { get; set; }
    public string GateAction { get; set; } = "PASS";
    public string[] FlaggedFeatures { get; set; } = [];
    public double MeanLag1Autocorrelation { get; set; }
    public double MeanPopulationStabilityIndex { get; set; }
    public double MeanChangePointScore { get; set; }
    public double MeanAdfLikeStatistic { get; set; }
    public double MeanKpssLikeStatistic { get; set; }
    public double FracDiffDApplied { get; set; }
}

public class SvgpWarmStartArtifact
{
    public bool Attempted { get; set; }
    public bool Compatible { get; set; }
    public int ReusedInducingPointCount { get; set; }
    public int TotalParentInducingPoints { get; set; }
    public double ReuseRatio { get; set; }
    public bool ArdLengthScalesTransferred { get; set; }
    public string[] CompatibilityIssues { get; set; } = [];
}

public class SvgpAuditArtifact
{
    public bool SnapshotContractValid { get; set; }
    public int AuditedSampleCount { get; set; }
    public double MaxRawParityError { get; set; }
    public double MeanRawParityError { get; set; }
    public double MaxDeployedCalibrationDelta { get; set; }
    public int ThresholdDecisionMismatchCount { get; set; }
    public string[] Findings { get; set; } = [];
}

/// <summary>
/// Structured train/selection/calibration/test split summary persisted for reproducibility.
/// </summary>
public class TrainingSplitSummary
{
    public int RawTrainCount { get; set; }
    public int RawSelectionCount { get; set; }
    public int RawCalibrationCount { get; set; }
    public int RawTestCount { get; set; }
    public int TrainStartIndex { get; set; }
    public int TrainCount { get; set; }
    public int SelectionStartIndex { get; set; }
    public int SelectionCount { get; set; }
    public int SelectionPruningStartIndex { get; set; }
    public int SelectionPruningCount { get; set; }
    public int SelectionThresholdStartIndex { get; set; }
    public int SelectionThresholdCount { get; set; }
    public int SelectionKellyStartIndex { get; set; }
    public int SelectionKellyCount { get; set; }
    public int CalibrationStartIndex { get; set; }
    public int CalibrationCount { get; set; }
    public int CalibrationFitStartIndex { get; set; }
    public int CalibrationFitCount { get; set; }
    public int CalibrationDiagnosticsStartIndex { get; set; }
    public int CalibrationDiagnosticsCount { get; set; }
    public int ConformalStartIndex { get; set; }
    public int ConformalCount { get; set; }
    public int MetaLabelStartIndex { get; set; }
    public int MetaLabelCount { get; set; }
    public int AbstentionStartIndex { get; set; }
    public int AbstentionCount { get; set; }
    public string AdaptiveHeadSplitMode { get; set; } = string.Empty;
    public int AdaptiveHeadCrossFitFoldCount { get; set; }
    public int[] AdaptiveHeadCrossFitFoldStartIndices { get; set; } = [];
    public int[] AdaptiveHeadCrossFitFoldCounts { get; set; } = [];
    public string[] AdaptiveHeadCrossFitFoldHashes { get; set; } = [];
    public int TestStartIndex { get; set; }
    public int TestCount { get; set; }
    public int EmbargoCount { get; set; }
    public int TrainEmbargoDropped { get; set; }
    public int SelectionEmbargoDropped { get; set; }
    public int CalibrationEmbargoDropped { get; set; }
}

// ── Serialisable model snapshot ───────────────────────────────────────────────

/// <summary>
/// JSON-serialisable representation of a trained model stored in
/// <see cref="LascodiaTradingEngine.Domain.Entities.MLModel.ModelBytes"/>.
/// Consumed by <c>MLSignalScorer</c> to run inference without re-training.
/// </summary>
public class ModelSnapshot
{
    public string   Type          { get; set; } = string.Empty;
    public string   Version       { get; set; } = string.Empty;
    public string[] Features      { get; set; } = [];
    /// <summary>
    /// Optional mapping from this snapshot's feature-space positions back to the raw
    /// feature indices emitted by <see cref="MLFeatureHelper.BuildFeatureVector"/>.
    /// Empty means the snapshot consumes the raw feature order directly.
    /// </summary>
    public int[] RawFeatureIndices { get; set; } = [];
    /// <summary>
    /// Ordered list of replayable feature-pipeline transforms applied after standardisation
    /// and before inference. Enables train/inference parity for architecture-specific
    /// preprocessing without hard-coding model-type branches into the scorer.
    /// </summary>
    public string[] FeaturePipelineTransforms { get; set; } = [];
    /// <summary>
    /// Typed, versioned descriptors for replayable feature-pipeline transforms.
    /// Preferred over <see cref="FeaturePipelineTransforms"/> for new snapshots.
    /// </summary>
    public FeatureTransformDescriptor[] FeaturePipelineDescriptors { get; set; } = [];
    /// <summary>
    /// Fingerprint of the raw feature schema expected by this snapshot.
    /// Used to reject semantically incompatible warm starts and invalid scoring payloads.
    /// </summary>
    public string FeatureSchemaFingerprint { get; set; } = string.Empty;
    /// <summary>
    /// Fingerprint of the replayable preprocessing layout (transforms + masks + counts).
    /// Separate from means/stds so warm-start compatibility can remain strict without blocking retraining.
    /// </summary>
    public string PreprocessingFingerprint { get; set; } = string.Empty;
    /// <summary>
    /// Fingerprint of the trainer architecture/hyperparameter layout used to fit the snapshot.
    /// </summary>
    public string TrainerFingerprint { get; set; } = string.Empty;
    /// <summary>
    /// Deterministic seed used by the trainer to initialise model weights and auxiliary searches.
    /// </summary>
    public int TrainingRandomSeed { get; set; }
    /// <summary>
    /// Reproducibility summary of the train/cal/test split boundaries used for the final fit.
    /// </summary>
    public TrainingSplitSummary? TrainingSplitSummary { get; set; }
    /// <summary>
    /// Split-scoped metrics for the selection holdout used for thresholding and pruning decisions.
    /// </summary>
    public TabNetMetricSummary? TabNetSelectionMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the post-fit calibration/diagnostic evaluation slice.
    /// </summary>
    public TabNetMetricSummary? TabNetCalibrationMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the final held-out test window.
    /// </summary>
    public TabNetMetricSummary? TabNetTestMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the FT-Transformer selection holdout.
    /// </summary>
    public FtTransformerMetricSummary? FtTransformerSelectionMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the FT-Transformer calibration diagnostics slice.
    /// </summary>
    public FtTransformerMetricSummary? FtTransformerCalibrationMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the FT-Transformer final test window.
    /// </summary>
    public FtTransformerMetricSummary? FtTransformerTestMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the GBM threshold-selection / pruning evaluation slice.
    /// </summary>
    public GbmMetricSummary? GbmSelectionMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the GBM post-fit calibration diagnostics slice.
    /// </summary>
    public GbmMetricSummary? GbmCalibrationMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the GBM final held-out test window.
    /// </summary>
    public GbmMetricSummary? GbmTestMetrics { get; set; }
    /// <summary>
    /// Structured FT-Transformer calibration summary.
    /// </summary>
    public FtTransformerCalibrationArtifact? FtTransformerCalibrationArtifact { get; set; }
    /// <summary>
    /// Structured GBM calibration summary.
    /// </summary>
    public GbmCalibrationArtifact? GbmCalibrationArtifact { get; set; }
    public GbmDriftArtifact? GbmDriftArtifact { get; set; }
    public GbmWarmStartArtifact? GbmWarmStartArtifact { get; set; }
    public double GbmCalibrationResidualMean { get; set; }
    public double GbmCalibrationResidualStd { get; set; }
    public double GbmCalibrationResidualThreshold { get; set; }
    /// <summary>
    /// Split-scoped metrics for the AdaBoost selection holdout.
    /// </summary>
    public AdaBoostMetricSummary? AdaBoostSelectionMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the AdaBoost calibration diagnostics slice.
    /// </summary>
    public AdaBoostMetricSummary? AdaBoostCalibrationMetrics { get; set; }
    /// <summary>
    /// Split-scoped metrics for the AdaBoost final test window.
    /// </summary>
    public AdaBoostMetricSummary? AdaBoostTestMetrics { get; set; }
    /// <summary>
    /// Structured AdaBoost calibration summary.
    /// </summary>
    public AdaBoostCalibrationArtifact? AdaBoostCalibrationArtifact { get; set; }
    /// <summary>
    /// AdaBoost post-train parity and contract audit artifact.
    /// </summary>
    public AdaBoostAuditArtifact? AdaBoostAuditArtifact { get; set; }
    public AdaBoostDriftArtifact? AdaBoostDriftArtifact { get; set; }
    public AdaBoostWarmStartArtifact? AdaBoostWarmStartArtifact { get; set; }
    public double AdaBoostCalibrationResidualMean { get; set; }
    public double AdaBoostCalibrationResidualStd { get; set; }
    public double AdaBoostCalibrationResidualThreshold { get; set; }
    /// <summary>
    /// FT-Transformer warm-start compatibility summary.
    /// </summary>
    public FtTransformerWarmStartArtifact? FtTransformerWarmStartArtifact { get; set; }
    /// <summary>
    /// FT-Transformer post-train parity and contract audit artifact.
    /// </summary>
    public FtTransformerAuditArtifact? FtTransformerAuditArtifact { get; set; }
    public FtTransformerDriftArtifact? FtTransformerDriftArtifact { get; set; }
    public double FtTransformerCalibrationResidualMean { get; set; }
    public double FtTransformerCalibrationResidualStd { get; set; }
    public double FtTransformerCalibrationResidualThreshold { get; set; }
    /// <summary>
    /// GBM post-train parity and contract audit artifact.
    /// </summary>
    public GbmAuditArtifact? GbmAuditArtifact { get; set; }
    public float[]  Means         { get; set; } = [];
    public float[]  Stds          { get; set; } = [];

    /// <summary>
    /// Per-feature lower clip bounds used by ELM winsorization before Z-score standardization.
    /// Empty when ELM winsorization was disabled for this snapshot.
    /// </summary>
    public float[] ElmWinsorizeLowerBounds { get; set; } = [];

    /// <summary>
    /// Per-feature upper clip bounds used by ELM winsorization before Z-score standardization.
    /// Empty when ELM winsorization was disabled for this snapshot.
    /// </summary>
    public float[] ElmWinsorizeUpperBounds { get; set; } = [];

    public int      BaseLearnersK { get; set; }

    [JsonPropertyName("Weights")]
    public double[][] Weights     { get; set; } = [];
    public double[]   Biases      { get; set; } = [];
    public double[]   MagWeights  { get; set; } = [];
    public double     MagBias     { get; set; }

    /// <summary>
    /// Augmented-space magnitude regressor weights (length = featureCount + hiddenSize).
    /// Uses original features + mean ELM hidden activations for nonlinear magnitude prediction.
    /// When non-empty, preferred over <see cref="MagWeights"/> (linear projection fallback).
    /// </summary>
    public double[] MagAugWeights { get; set; } = [];

    /// <summary>
    /// Augmented-space magnitude regressor bias. Paired with <see cref="MagAugWeights"/>.
    /// </summary>
    public double MagAugBias { get; set; }

    /// <summary>
    /// Per-learner accuracy-based ensemble weights (normalised to sum to 1).
    /// When non-empty, used for weighted ensemble averaging instead of uniform averaging.
    /// Derived from calibration-set per-learner accuracies.
    /// </summary>
    public double[] LearnerAccuracyWeights { get; set; } = [];

    /// <summary>
    /// Platt scaling parameter A. Calibrated probability =
    /// sigmoid(PlattA × logit(raw_prob) + PlattB).
    /// </summary>
    public double PlattA { get; set; } = 1.0;

    /// <summary>Platt scaling bias. 0.0 = no shift (identity calibration).</summary>
    public double PlattB { get; set; } = 0.0;

    public object? Metrics      { get; set; }
    public int     TrainSamples { get; set; }
    public int     TestSamples  { get; set; }
    public int     CalSamples   { get; set; }
    public int     SelectionSamples { get; set; }
    public int     EmbargoSamples { get; set; }
    public DateTime TrainedOn   { get; set; }

    /// <summary>
    /// Value of <see cref="TrainSamples"/> at the time calibration parameters (Platt, isotonic,
    /// temperature scaling, meta-label, abstention) were last fitted. Online updates via
    /// Sherman-Morrison increment <see cref="TrainSamples"/> but leave calibration frozen.
    /// Downstream workers can compare <c>TrainSamples - TrainSamplesAtLastCalibration</c>
    /// to decide when recalibration is needed.
    /// </summary>
    public int TrainSamplesAtLastCalibration { get; set; }

    /// <summary>
    /// Exponential forgetting factor for online Sherman-Morrison updates.
    /// 0.0 = no forgetting (default). Typical values: 0.001-0.01.
    /// Applied as inverse Gram inflation: P <- P / (1 - lambda) before each rank-1 update.
    /// </summary>
    public double OnlineForgettingFactor { get; set; }

    /// <summary>
    /// Permutation importance for each feature (index matches <see cref="Features"/>).
    /// Value = baseline accuracy − accuracy after shuffling that feature.
    /// Normalised to sum to 1.0. Empty when not yet computed.
    /// </summary>
    public float[] FeatureImportance { get; set; } = [];

    // ── COT normalisation bounds (set by MLTrainingWorker after training) ─────

    /// <summary>
    /// Minimum raw <c>NetNonCommercialPositioning</c> value observed in the COT training data.
    /// Used at inference time to apply consistent min-max normalisation instead of a hardcoded divisor.
    /// Default −300 000 matches the legacy ÷100 000 clamp-at-±3 behaviour.
    /// </summary>
    public float CotNetNormMin { get; set; } = -300_000f;

    /// <summary>Maximum raw <c>NetNonCommercialPositioning</c> over the training period.</summary>
    public float CotNetNormMax { get; set; } =  300_000f;

    /// <summary>Minimum raw <c>NetPositioningChangeWeekly</c> value in the training data.</summary>
    public float CotMomNormMin { get; set; } = -30_000f;

    /// <summary>Maximum raw <c>NetPositioningChangeWeekly</c> value in the training data.</summary>
    public float CotMomNormMax { get; set; } =  30_000f;

    // ── Multivariate drift baseline (set by MLTrainingWorker after training) ──

    /// <summary>
    /// Per-feature empirical variance computed from the standardised training feature matrix.
    /// Expected value is 1.0 for every feature under N(0, 1). Values significantly above 1.0
    /// in recent data indicate multivariate distributional shift.
    /// Length equals <see cref="Features"/>.Length; empty when not yet computed.
    /// </summary>
    public double[] FeatureVariances { get; set; } = [];

    /// <summary>
    /// Mahalanobis distance threshold for out-of-distribution detection.
    /// Features with normalised distance above this are flagged as OOD.
    /// Default 3.0 (≈ 3σ). Set to 0 to disable OOD gating.
    /// Populated from <see cref="HyperparamConfig.OodThresholdSigma"/> at training time.
    /// </summary>
    public double OodThreshold { get; set; } = 3.0;

    // ── Stacking meta-learner (set by BaggedLogisticTrainer after base learner training) ──

    /// <summary>
    /// Weights of the logistic meta-learner trained on per-learner OOF probabilities.
    /// <c>MetaWeights[k]</c> is the weight assigned to base learner k's raw probability.
    /// When empty, inference falls back to simple ensemble averaging.
    /// Length equals <see cref="BaseLearnersK"/>.
    /// </summary>
    public double[] MetaWeights { get; set; } = [];

    /// <summary>Bias of the logistic meta-learner. 0.0 when stacking is disabled.</summary>
    public double MetaBias { get; set; } = 0.0;

    // ── Per-regime feature standardisation (set by MLTrainingWorker after training) ──

    /// <summary>
    /// Per-regime feature means keyed by <c>MarketRegime.ToString()</c> (e.g. "Trending").
    /// Applied instead of the global <see cref="Means"/> when the current regime matches.
    /// Empty means regime-specific standardisation is disabled.
    /// </summary>
    public Dictionary<string, float[]> RegimeMeans { get; set; } = [];

    /// <summary>
    /// Per-regime feature standard deviations. Applied alongside <see cref="RegimeMeans"/>.
    /// Empty means regime-specific standardisation is disabled.
    /// </summary>
    public Dictionary<string, float[]> RegimeStds { get; set; } = [];

    // ── Feature pruning mask (set by BaggedLogisticTrainer after importance pruning) ──

    /// <summary>
    /// Boolean mask over the snapshot feature vector.
    /// <c>true</c> = feature is active (used for inference); <c>false</c> = pruned (zeroed).
    /// Empty or all-true means no features were pruned. All-false masks are invalid.
    /// Applied at inference time in <c>MLSignalScorer</c> before the ensemble forward pass.
    /// </summary>
    public bool[] ActiveFeatureMask { get; set; } = [];

    /// <summary>Number of pruned (inactive) features. 0 means all snapshot features are active.</summary>
    public int PrunedFeatureCount { get; set; }

    // ── Regime scope (set by MLTrainingWorker for regime-specific sub-models) ──

    /// <summary>
    /// Market regime this model was trained on (e.g. "Trending", "Ranging").
    /// <c>null</c> for the global model trained across all regimes.
    /// </summary>
    public string? RegimeScope { get; set; }

    // ── Feature subsampling (set by BaggedLogisticTrainer when FeatureSampleRatio > 0) ──

    /// <summary>
    /// Per-learner feature subset indices. <c>FeatureSubsetIndices[k]</c> contains the
    /// feature indices that base learner k was trained on.
    /// <c>null</c> or empty means all features were used (no subsampling).
    /// At inference <see cref="MLSignalScorer"/> uses these indices for exact per-learner inference.
    /// </summary>
    public int[][]? FeatureSubsetIndices { get; set; }

    // ── EV-optimal threshold (set by BaggedLogisticTrainer after threshold sweep) ──

    /// <summary>
    /// Decision threshold selected on the model-selection split after calibration.
    /// <c>MLSignalScorer</c> compares calibP against this value instead of the fixed 0.5.
    /// Default 0.5 = no optimisation (neutral binary threshold).
    /// </summary>
    public double OptimalThreshold { get; set; } = 0.5;

    // ── Calibration quality (set by BaggedLogisticTrainer after ECE computation) ──

    /// <summary>
    /// Expected Calibration Error on the held-out test set after Platt scaling.
    /// ECE = Σ |acc(bin) − conf(bin)| × |bin| / n  using 10 equal-width bins.
    /// Lower is better; values below 0.05 indicate well-calibrated confidence outputs.
    /// Used by <c>MLTrainingWorker</c> as a promotion quality gate.
    /// </summary>
    public double Ece { get; set; }

    // ── Reliability diagram bins (set post-Platt calibration) ────────���───────

    /// <summary>Per-bin mean predicted confidence for the reliability diagram. Length = adaptive bin count.</summary>
    public double[]? ReliabilityBinConfidence { get; set; }
    /// <summary>Per-bin observed accuracy (fraction of positive class) for the reliability diagram.</summary>
    public double[]? ReliabilityBinAccuracy { get; set; }
    /// <summary>Per-bin sample count for the reliability diagram.</summary>
    public int[]? ReliabilityBinCounts { get; set; }

    // ── Isotonic regression calibration (set by BaggedLogisticTrainer) ───────

    /// <summary>
    /// Sorted breakpoints of the isotonic (PAVA) calibration mapping fitted on the
    /// calibration fold after Platt scaling. Length = number of breakpoints × 2
    /// interleaved as [x0, y0, x1, y1, ...] where x = Platt-calibrated probability
    /// and y = isotonic-corrected probability. Empty = isotonic calibration disabled.
    /// At inference, linear interpolation is applied between breakpoints.
    /// </summary>
    public double[] IsotonicBreakpoints { get; set; } = [];

    // ── Out-of-bag accuracy (set by BaggedLogisticTrainer during FitEnsemble) ─

    /// <summary>
    /// Unweighted OOB accuracy — fraction of training samples correctly classified
    /// using only the base learners that did not train on each sample. For GBM snapshots
    /// this is measured in raw-probability space so subset-tree OOB predictions are not
    /// passed through full-ensemble calibration artifacts.
    /// Provides a low-bias generalization estimate without consuming a separate split.
    /// 0.0 when OOB estimation was disabled or insufficient samples were available.
    /// </summary>
    public double OobAccuracy { get; set; }

    // ── Conformal prediction threshold (set by BaggedLogisticTrainer) ────────

    /// <summary>
    /// Split-conformal nonconformity score threshold (q̂) computed at the desired
    /// coverage level (1 − α = 0.90 by default) on the held-out calibration fold.
    /// At inference, a prediction set is formed as:
    ///   {Buy}         if calibP ≥ 1 − q̂
    ///   {Sell}        if 1 − calibP ≥ 1 − q̂  (i.e. calibP ≤ q̂)
    ///   {Buy, Sell}   otherwise (ambiguous — both classes are plausible)
    /// 0.5 = no conformal correction applied.
    /// </summary>
    public double ConformalQHat { get; set; } = 0.5;

    /// <summary>Mondrian (per-class) conformal Q̂ for Buy class (y=1). Defaults to global Q̂.</summary>
    public double ConformalQHatBuy { get; set; }

    /// <summary>Mondrian (per-class) conformal Q̂ for Sell class (y=0). Defaults to global Q̂.</summary>
    public double ConformalQHatSell { get; set; }

    // ── Meta-labeling secondary classifier ───────────────────────────────────

    /// <summary>
    /// Output-layer weights of the meta-label classifier. Inputs are the replayed
    /// meta-feature vector [calibP, ensembleStd, top feature values...].
    /// When <see cref="MetaLabelHiddenDim"/> is 0 this is the full linear classifier.
    /// When <see cref="MetaLabelHiddenDim"/> is &gt; 0 this is the hidden→output weight vector.
    /// Empty = meta-labeling disabled.
    /// </summary>
    public double[] MetaLabelWeights { get; set; } = [];

    /// <summary>
    /// Output-layer bias of the meta-label classifier.
    /// Also used as the full linear-model bias when <see cref="MetaLabelHiddenDim"/> is 0.
    /// </summary>
    public double MetaLabelBias { get; set; }

    /// <summary>
    /// Hidden-layer width of the meta-label MLP. 0 = linear classifier.
    /// When &gt; 0, <see cref="MetaLabelHiddenWeights"/> and
    /// <see cref="MetaLabelHiddenBiases"/> hold the first-layer parameters.
    /// </summary>
    public int MetaLabelHiddenDim { get; set; }

    /// <summary>
    /// Flattened input→hidden weight matrix of the meta-label MLP in row-major order.
    /// Shape = [MetaLabelHiddenDim × inputDim]. Empty when the meta-label model is linear.
    /// </summary>
    public double[] MetaLabelHiddenWeights { get; set; } = [];

    /// <summary>
    /// Hidden-layer biases of the meta-label MLP. Length = <see cref="MetaLabelHiddenDim"/>.
    /// Empty when the meta-label model is linear.
    /// </summary>
    public double[] MetaLabelHiddenBiases { get; set; } = [];

    /// <summary>
    /// Classification threshold for the meta-label model.
    /// Predictions with meta-label P &lt; MetaLabelThreshold are filtered out (skipped).
    /// Default 0.5.
    /// </summary>
    public double MetaLabelThreshold { get; set; } = 0.5;

    /// <summary>
    /// Indices of the top feature values used as auxiliary inputs in the meta-label model.
    /// Stored so inference can reconstruct the exact same meta-feature vector.
    /// Empty = legacy fallback to the first raw features.
    /// </summary>
    public int[] MetaLabelTopFeatureIndices { get; set; } = [];

    // ── Feature distribution baselines for PSI monitoring ────────────────────

    /// <summary>
    /// Per-feature quantile breakpoints computed on the training set after standardisation.
    /// Outer array index = feature index (same order as <see cref="Features"/>).
    /// Inner array = 10 equal-population bin boundaries (length = 9 bin edges between 10 buckets).
    /// Empty = PSI monitoring disabled for this model.
    /// Used by <c>MLFeaturePsiWorker</c> to compute distribution drift.
    /// </summary>
    public double[][] FeatureQuantileBreakpoints { get; set; } = [];

    // ── Jackknife+ prediction intervals ──────────────────────────────────────

    /// <summary>
    /// Sorted OOB nonconformity residuals |y_i − ŷ_{-i}| from the training set.
    /// Used by <c>MLSignalScorer</c> to compute per-sample Jackknife+ prediction intervals.
    /// Empty = Jackknife+ intervals disabled.
    /// </summary>
    public double[] JackknifeResiduals { get; set; } = [];

    // ── Fractional differencing order ────────────────────────────────────────

    /// <summary>
    /// The fractional differencing order d actually used when building training features.
    /// Stored for reproducibility; inference must apply the same d. 0.0 = not applied.
    /// </summary>
    public double FracDiffD { get; set; }

    // ── Heterogeneous ensemble metadata ──────────────────────────────────────

    /// <summary>
    /// Index of the first polynomial learner in the base learner array.
    /// Learners 0 … PolyLearnerStartIndex−1 are linear; learners from this index onward
    /// use degree-2 polynomial feature augmentation.
    /// Equal to BaseLearnersK when no polynomial learners are used.
    /// </summary>
    public int PolyLearnerStartIndex { get; set; }

    // ── Adaptive threshold ────────────────────────────────────────────────────

    /// <summary>
    /// Online EMA-updated optimal decision threshold. Starts at 0.5 and is adjusted by
    /// <c>MLAdaptiveThresholdWorker</c> based on recent prediction EV curves.
    /// 0.0 = not yet initialised (use 0.5 at inference).
    /// </summary>
    public double AdaptiveThreshold { get; set; }

    // ── Regime-conditioned decision thresholds ────────────────────────────────

    /// <summary>
    /// Per-market-regime EV-optimal decision thresholds computed by
    /// <c>MLAdaptiveThresholdWorker</c> from resolved prediction logs filtered by regime.
    /// Keys are regime names (e.g. "Trending", "Ranging", "Volatile");
    /// values are the EMA-blended optimal thresholds for that regime.
    /// Empty = no regime-specific thresholds computed yet (fall back to AdaptiveThreshold).
    /// </summary>
    public Dictionary<string, double> RegimeThresholds { get; set; } = [];

    // ── Prediction accuracy half-life ─────────────────────────────────────────

    /// <summary>
    /// Exponential accuracy decay rate λ fitted by <c>MLModelHalfLifeWorker</c>.
    /// acc(t) ≈ acc_0 × e^{−DecayLambda × t_days}. 0.0 = not yet estimated.
    /// </summary>
    public double DecayLambda { get; set; }

    /// <summary>
    /// Estimated days until model accuracy crosses the drift threshold at current decay rate.
    /// Computed as t = −ln(DriftAccuracyThreshold / acc_0) / DecayLambda.
    /// 0.0 = not yet estimated. Used by <c>MLModelHalfLifeWorker</c> to trigger proactive retraining.
    /// </summary>
    public double HalfLifeDays { get; set; }

    // ── Feature importance scores ─────────────────────────────────────────────

    /// <summary>
    /// Permutation importance score per feature, same order as <see cref="Features"/>.
    /// Computed on the calibration set during training. Used by warm-start feature importance
    /// transfer to bias bootstrap feature sampling in the next retrain toward historically
    /// important features. Empty = permutation importance not computed.
    /// </summary>
    public double[] FeatureImportanceScores { get; set; } = [];

    // ── Greedy ensemble selection weights ────────────────────────────────────

    /// <summary>
    /// Caruana et al. greedy ensemble selection usage-frequency weights.
    /// Length = BaseLearnersK. EnsembleSelectionWeights[k] = number of times learner k
    /// was selected in the greedy forward pass (can be 0 for redundant learners).
    /// Normalised to sum to 1 at inference time. Empty = uniform average used instead.
    /// </summary>
    public double[] EnsembleSelectionWeights { get; set; } = [];

    // ── Model lineage ─────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="MLModel.Id"/> of the parent model from which this model warm-started.
    /// 0 = first-generation model (cold start). Enables genealogical tracing of model evolution
    /// and rollback to any ancestor in the lineage chain.
    /// </summary>
    public long ParentModelId { get; set; }

    /// <summary>
    /// Number of warm-start retrains from the original cold-start ancestor.
    /// 0 = this model was cold-started in the current lineage convention.
    /// Warm-start retrains increment from the parent's <see cref="GenerationNumber"/> + 1.
    /// </summary>
    public int GenerationNumber { get; set; }

    // ── Selective prediction / abstention gate ────────────────────────────────

    /// <summary>
    /// Weights of the abstention logistic gate classifier.
    /// Inputs: [calibP, ensStd, metaLabelScore] (3 features).
    /// Predicts P("this is a tradeable environment") from the calibration set.
    /// When the abstention score &lt; AbstentionThreshold, the signal is suppressed.
    /// Empty = abstention gate disabled.
    /// </summary>
    public double[] AbstentionWeights { get; set; } = [];

    /// <summary>Bias term of the abstention gate classifier.</summary>
    public double AbstentionBias { get; set; }

    /// <summary>
    /// P(tradeable) threshold below which the signal is suppressed at inference.
    /// Default 0.5. Set higher (e.g. 0.6) to be more selective.
    /// </summary>
    public double AbstentionThreshold { get; set; } = 0.5;

    // ── Asymmetric quantile magnitude regressor (Round 5) ─────────────────────

    /// <summary>
    /// Weights of the τ-quantile (pinball-loss) magnitude regressor.
    /// Predicts the conditional τ-th quantile of ATR-normalised magnitude.
    /// Empty = quantile regressor disabled (MSE regressor used instead).
    /// </summary>
    public double[] MagQ90Weights { get; set; } = [];

    /// <summary>Bias of the quantile magnitude regressor. 0.0 when disabled.</summary>
    public double MagQ90Bias { get; set; }

    // ── QRF 2-layer MLP magnitude regressor ───────────────────────────────────

    /// <summary>
    /// Hidden-layer width of the QRF MLP magnitude regressor. 0 = linear regressor used instead.
    /// When > 0, <see cref="QrfMlpW1"/>, <see cref="QrfMlpB1"/>, <see cref="QrfMlpW2"/>, and
    /// <see cref="QrfMlpB2"/> hold the trained weights; <c>MLSignalScorer</c> selects the MLP
    /// over the linear <see cref="MagWeights"/> path.
    /// </summary>
    public int QrfMlpHiddenDim { get; set; }

    /// <summary>
    /// Flattened input→hidden weight matrix W1 [H × F] in row-major order.
    /// Row h contains the F weights feeding hidden unit h.
    /// Empty when <see cref="QrfMlpHiddenDim"/> is 0.
    /// </summary>
    public double[] QrfMlpW1 { get; set; } = [];

    /// <summary>Hidden-layer biases b1 [H]. Empty when MLP is disabled.</summary>
    public double[] QrfMlpB1 { get; set; } = [];

    /// <summary>
    /// Output-layer weights W2 [H]. The scalar magnitude output is W2·ReLU(W1·x + b1) + b2.
    /// Empty when <see cref="QrfMlpHiddenDim"/> is 0.
    /// </summary>
    public double[] QrfMlpW2 { get; set; } = [];

    /// <summary>Output-layer bias b2. 0.0 when MLP is disabled.</summary>
    public double QrfMlpB2 { get; set; }

    // ── Decision boundary distance scoring (Round 5) ──────────────────────────

    /// <summary>
    /// Mean absolute raw-logit distance |logit(P(Buy|x))| over the calibration set.
    /// Higher values indicate predictions sit farther from the 0.5 decision boundary.
    /// 0.0 = not computed.
    /// </summary>
    public double DecisionBoundaryMean { get; set; }

    /// <summary>Standard deviation of the calibration-set raw-logit distance statistic. 0.0 = not computed.</summary>
    public double DecisionBoundaryStd { get; set; }

    // ── Durbin-Watson auto-correlation statistic (Round 5) ────────────────────

    /// <summary>
    /// Durbin-Watson statistic computed on magnitude regressor residuals over the training set.
    /// DW ≈ 2 = no autocorrelation; DW &lt; 1.5 = positive autocorrelation (model missing structure).
    /// Default 2.0 (uncorrelated). Stored for monitoring by downstream workers.
    /// </summary>
    public double DurbinWatsonStatistic { get; set; } = 2.0;

    // ── Class-conditional Platt scaling (Round 6) ─────────────────────────────

    /// <summary>
    /// Platt scaling parameter A fitted only on the Buy (Direction=1) calibration samples.
    /// Applied when <c>calibP ≥ 0.5</c> (predicted Buy) to correct Buy-direction over/under-confidence.
    /// 0.0 = not fitted (fall back to global PlattA).
    /// </summary>
    public double PlattABuy { get; set; }

    /// <summary>Platt scaling bias fitted on Buy calibration samples. 0.0 = not fitted.</summary>
    public double PlattBBuy { get; set; }

    /// <summary>
    /// Platt scaling parameter A fitted only on the Sell (Direction=0) calibration samples.
    /// Applied when <c>calibP &lt; 0.5</c> (predicted Sell) to correct Sell-direction bias.
    /// 0.0 = not fitted (fall back to global PlattA).
    /// </summary>
    public double PlattASell { get; set; }

    /// <summary>Platt scaling bias fitted on Sell calibration samples. 0.0 = not fitted.</summary>
    public double PlattBSell { get; set; }

    /// <summary>
    /// Probability routing threshold used by class-conditional calibration.
    /// Defaults to 0.5 for legacy snapshots.
    /// </summary>
    public double ConditionalCalibrationRoutingThreshold { get; set; } = 0.5;

    // ── Average Kelly fraction (Round 6) ──────────────────────────────────────

    /// <summary>
    /// Half-Kelly fraction averaged across the calibration set: mean(max(0, 2p−1)) × 0.5.
    /// Represents the model's expected position-size multiplier at its average conviction level.
    /// Downstream workers can use this as a default lot-size scaler.
    /// 0.0 = not computed.
    /// </summary>
    public double AvgKellyFraction { get; set; }

    // ── Mutual-information redundant feature pairs (Round 6) ──────────────────

    /// <summary>
    /// Feature pairs whose pairwise mutual information exceeded the training-time
    /// <c>MutualInfoRedundancyThreshold</c>. Stored as "Feature1:Feature2" strings.
    /// Empty = MI check disabled or no redundant pairs found.
    /// Operators can inspect these pairs and exclude one from the feature set in future runs.
    /// </summary>
    public string[] RedundantFeaturePairs { get; set; } = [];

    // ── OOB-pruned learner count (Round 6) ────────────────────────────────────

    /// <summary>
    /// Number of base learners removed by OOB-contribution pruning.
    /// 0 = pruning disabled or no learners removed.
    /// Effective ensemble size = BaseLearnersK − OobPrunedLearnerCount.
    /// </summary>
    public int OobPrunedLearnerCount { get; set; }

    // ── Walk-forward Sharpe trend (Round 6) ───────────────────────────────────

    /// <summary>
    /// Estimated linear regression slope of per-fold Sharpe ratios across the walk-forward folds.
    /// Negative slope indicates deteriorating out-of-sample performance across time.
    /// 0.0 = not computed (fewer than 3 folds).
    /// </summary>
    public double WalkForwardSharpeTrend { get; set; }

    // ── Temperature scaling (Round 7) ─────────────────────────────────────────

    /// <summary>
    /// Temperature scalar T fitted on the calibration fold: calibP = σ(logit(rawP) / T).
    /// T = 1.0 = no correction (identity). 0.0 = temperature scaling disabled / not fitted.
    /// Applied in <c>MLSignalScorer</c> when &gt; 0 and &lt; 10 (sanity bounds).
    /// </summary>
    public double TemperatureScale { get; set; }

    // ── Ensemble diversity (Round 7) ──────────────────────────────────────────

    /// <summary>
    /// Average pairwise prediction disagreement rate across calibration samples.
    /// 0.0 = learners vote identically; 1.0 = maximal directional disagreement.
    /// Lower values indicate a redundant ensemble with weak diversity.
    /// </summary>
    public double EnsembleDiversity { get; set; }

    // ── Brier Skill Score (Round 7) ───────────────────────────────────────────

    /// <summary>
    /// Brier Skill Score on the held-out test set: BSS = 1 − Brier / Brier_naive.
    /// Brier_naive = p_base × (1 − p_base) where p_base is the test-set Buy fraction.
    /// BSS &gt; 0 means the model beats a naive base-rate predictor. Values &gt; 0.05 are good.
    /// </summary>
    public double BrierSkillScore { get; set; }

    // ── Model age decay (Round 8) ─────────────────────────────────────────────

    /// <summary>
    /// UTC timestamp at which this model snapshot was serialised (end of training).
    /// Used by <c>MLSignalScorer</c> together with <see cref="AgeDecayLambda"/> to apply
    /// an exponential confidence decay: calibP ← 0.5 + (calibP − 0.5) × exp(−λ × days).
    /// Default (DateTime.MinValue) = age decay disabled.
    /// </summary>
    public DateTime TrainedAtUtc { get; set; }

    /// <summary>
    /// Exponential decay rate λ for model age confidence decay.
    /// Copied from <c>TrainingHyperparams.AgeDecayLambda</c> at serialisation time.
    /// 0.0 = age decay disabled.
    /// </summary>
    public double AgeDecayLambda { get; set; }

    // ── Feature walk-forward stability scores (Round 8) ───────────────────────

    /// <summary>
    /// Per-feature coefficient of variation (σ/μ) of mean absolute weight magnitudes
    /// across walk-forward CV folds. Index matches <see cref="Features"/>.
    /// Low CV (≈ 0) = stable contribution; high CV (&gt; 1) = erratic / unreliable feature.
    /// Empty = fewer than 2 walk-forward folds completed.
    /// </summary>
    public double[] FeatureStabilityScores { get; set; } = [];

    // ── Adaptive label smoothing (Round 8) ────────────────────────────────────

    /// <summary>
    /// The label smoothing ε actually applied during training.
    /// When <c>UseAdaptiveLabelSmoothing</c> was enabled this is the data-driven value
    /// computed from the training-set ambiguous-label fraction; otherwise equals the fixed
    /// <c>MLTraining:LabelSmoothing</c> config value.
    /// 0.0 = label smoothing was not applied.
    /// </summary>
    public double AdaptiveLabelSmoothing { get; set; }

    // ── Per-learner calibration accuracy (Round 9) ─────────────────────────────

    /// <summary>
    /// Per-base-learner accuracy on the held-out calibration set.
    /// Length == K (number of base learners). Used at inference for softmax-weighted
    /// ensemble voting when greedy ensemble selection weights are not available.
    /// Empty = not yet computed (models trained before Round 9).
    /// </summary>
    public double[] LearnerCalAccuracies { get; set; } = [];

    // ── Training hyperparameter lineage (Round 11) ───────────────────────────

    /// <summary>
    /// Full JSON serialisation of the <see cref="TrainingHyperparams"/> record that was
    /// used to produce this snapshot. Stored verbatim at the end of the training pass for
    /// complete reproducibility — given the same candle data and this JSON you can replay
    /// the exact training run. Empty for models trained before Round 11.
    /// </summary>
    public string HyperparamsJson { get; set; } = string.Empty;

    // ── Rec #1: TCN architecture weights (Round 12) ───────────────────────────

    /// <summary>
    /// JSON-serialised TCN convolutional block weights produced by <c>TcnModelTrainer</c>.
    /// Non-null only when <c>LearnerArchitecture == TemporalConvNet || HybridTcnLogistic</c>.
    /// Used by <c>MLSignalScorer</c> to deserialise the TCN forward pass.
    /// Null for BaggedLogistic models.
    /// </summary>
    public string? ConvWeightsJson { get; set; }

    /// <summary>Per-channel means for TCN sequence feature standardisation. Length = <see cref="MLFeatureHelper.SequenceChannelCount"/>.</summary>
    public float[] SeqMeans { get; set; } = [];

    /// <summary>Per-channel standard deviations for TCN sequence feature standardisation.</summary>
    public float[] SeqStds { get; set; } = [];

    /// <summary>
    /// Ordered TCN sequence-channel names consumed by <see cref="ConvWeightsJson"/>.
    /// Kept separate from <see cref="Features"/> because the live scorer still builds
    /// the flat feature vector in the 33-feature space.
    /// </summary>
    public string[] TcnChannelNames { get; set; } = [];

    /// <summary>
    /// Boolean mask over the TCN sequence channels. Applied by the TCN inference engine
    /// after sequence standardisation so pruned channels stay inactive in production.
    /// </summary>
    public bool[] TcnActiveChannelMask { get; set; } = [];

    /// <summary>
    /// Channel-level TCN importance scores aligned with <see cref="TcnChannelNames"/>.
    /// Used by TCN-specific explainability and warm-start channel-importance transfer.
    /// </summary>
    public double[] TcnChannelImportanceScores { get; set; } = [];

    /// <summary>Structured deployed-calibration artifact for TCN snapshots.</summary>
    public TcnCalibrationArtifact? TcnCalibrationArtifact { get; set; }

    /// <summary>Structured warm-start compatibility and reuse summary for TCN snapshots.</summary>
    public TcnWarmStartArtifact? TcnWarmStartArtifact { get; set; }

    /// <summary>Structured post-train audit artifact for TCN snapshots.</summary>
    public TcnAuditArtifact? TcnAuditArtifact { get; set; }

    public TcnDriftArtifact? TcnDriftArtifact { get; set; }
    public double TcnCalibrationResidualMean { get; set; }
    public double TcnCalibrationResidualStd { get; set; }
    public double TcnCalibrationResidualThreshold { get; set; }

    /// <summary>Maximum absolute difference between trainer raw probability and persisted-snapshot TCN inference on audited samples.</summary>
    public double TcnTrainInferenceParityMaxError { get; set; }

    // ── Rec #50: Per-regime temperature scaling ───────────────────────────────

    /// <summary>
    /// Per-regime temperature scalars fitted by <c>MLRegimeTemperatureWorker</c>.
    /// Keys are regime names (e.g. "Trending"); values are T ∈ (0, ∞) such that
    /// calibP = σ(logit(rawP) / T_regime). Empty = use global TemperatureScale.
    /// </summary>
    public Dictionary<string, double> RegimeTemperatures { get; set; } = [];

    // ── Rec #53: Beta calibration ─────────────────────────────────────────────

    /// <summary>
    /// Beta calibration parameter a (shape of the Beta distribution numerator polynomial).
    /// calibP = Φ(a × logit(rawP) + b) where Φ is the standard normal CDF.
    /// 0.0 = beta calibration not fitted (use Platt/isotonic instead).
    /// </summary>
    public double BetaCalA { get; set; }

    /// <summary>Beta calibration bias parameter b. 0.0 = not fitted.</summary>
    public double BetaCalB { get; set; }

    // ── Rec #54: TD(λ) value function weights ─────────────────────────────────

    /// <summary>
    /// Weights of the TD(λ) value function head V(s) trained by <c>MLTdValueWorker</c>.
    /// Predicts the expected discounted cumulative P&amp;L from the current market state,
    /// used to scale signal confidence by long-term value.
    /// Length = feature count. Empty = TD value head not fitted.
    /// </summary>
    public double[] TdValueWeights { get; set; } = [];

    /// <summary>Bias of the TD(λ) value function head. 0.0 when disabled.</summary>
    public double TdValueBias { get; set; }

    // ── Rec #56: ACI conformal quantile ──────────────────────────────────────

    /// <summary>
    /// Adaptive Conformal Inference running quantile Q̂ updated online by
    /// <c>MLAciConformalWorker</c>. Used at scoring time to produce calibrated
    /// prediction intervals. 0.0 = ACI not yet fitted.
    /// </summary>
    public double AciConformalQHat { get; set; }

    // ── Rec #64: QR-DQN distributional value quantile weights ────────────────

    /// <summary>
    /// Per-quantile weight vectors for the distributional value head (QR-DQN style).
    /// Outer array length = N quantiles (e.g. 51); inner length = feature count.
    /// Null = distributional head not fitted.
    /// </summary>
    public double[][]? QuantileWeights { get; set; }

    /// <summary>Per-quantile biases for the distributional value head. Null = not fitted.</summary>
    public double[]? QuantileBiases { get; set; }

    // ── Rec #66: SWA checkpoint count ────────────────────────────────────────

    /// <summary>
    /// Number of epoch-end checkpoints that were averaged into this snapshot via SWA.
    /// 0 = SWA not applied (standard last-epoch weights used).
    /// </summary>
    public int SwaCheckpointCount { get; set; }

    public int SmoteSeed { get; set; }
    public SmoteDriftArtifact? SmoteDriftArtifact { get; set; }
    public SmoteWarmStartArtifact? SmoteWarmStartArtifact { get; set; }
    public SmoteAuditArtifact? SmoteAuditArtifact { get; set; }
    public double SmoteCalibrationResidualMean { get; set; }
    public double SmoteCalibrationResidualStd { get; set; }
    public double SmoteCalibrationResidualThreshold { get; set; }
    public int SmoteSelectionSamples { get; set; }
    public double SmoteAdversarialAuc { get; set; }
    public double SmoteCalibrationLoss { get; set; }
    public double SmoteRefinementLoss { get; set; }
    public double SmotePredictionStabilityScore { get; set; }

    // ── Rec #67: Echo State Network readout ──────────────────────────────────

    /// <summary>
    /// Trained readout layer weights for the Echo State Network (Rec #67).
    /// Length = ReservoirSize. Null = ESN readout not fitted.
    /// </summary>
    public double[]? ReservoirReadoutWeights { get; set; }

    // ── Rec #73: AdaGrad accumulated gradient squares ────────────────────────

    /// <summary>
    /// AdaGrad accumulated squared gradient for the Platt A/B parameters.
    /// Used by <c>MLOnlinePlattWorker</c> to compute adaptive learning rate.
    /// 0.0 = AdaGrad not yet used.
    /// </summary>
    public double AdaGradPlattG { get; set; }

    /// <summary>
    /// AdaGrad accumulated squared gradient per feature for the TD(λ) value head.
    /// Length = feature count. Empty = AdaGrad not applied to TD(λ).
    /// </summary>
    public double[] AdaGradTdG { get; set; } = [];

    // ── Rec #84: PC algorithm causal parent mask ──────────────────────────────

    /// <summary>
    /// Boolean mask of which features are identified as causal parents of the
    /// target by the PC algorithm worker (<c>MLPcCausalWorker</c>).
    /// Length = feature count; true = causal parent, false = spurious.
    /// Null = PC algorithm not yet run.
    /// </summary>
    public bool[]? CausalParentMask { get; set; }

    /// <summary>
    /// Serialised GBM tree structure produced by <see cref="GbmModelTrainer"/>.
    /// Null for models trained by other architectures.
    /// </summary>
    public string? GbmTreesJson { get; set; }

    /// <summary>
    /// Base log-odds intercept for GBM inference: logit(basePositiveRate).
    /// The GBM score is computed as <c>GbmBaseLogOdds + GbmLearningRate × Σ tree predictions</c>.
    /// Defaults to 0.0 (logit of 0.5) for snapshots serialised before this field was added.
    /// </summary>
    public double GbmBaseLogOdds { get; set; }

    /// <summary>
    /// Learning rate (shrinkage) applied to each GBM tree prediction during inference.
    /// Defaults to 0.1 for snapshots serialised before this field was added.
    /// </summary>
    public double GbmLearningRate { get; set; }

    /// <summary>
    /// Per-tree learning rates when shrinkage annealing is enabled. Length = number of trees.
    /// Empty = uniform GbmLearningRate for all trees (default/legacy).
    /// Inference: score = GbmBaseLogOdds + Σ GbmPerTreeLearningRates[t] × tree[t].Predict(x).
    /// </summary>
    public double[] GbmPerTreeLearningRates { get; set; } = [];

    // ── GBM extended snapshot fields ────────────────────────────────────────

    /// <summary>Approximate Venn-Abers-style multi-probability bounds [p_lower, p_upper] per calibration sample.</summary>
    public double[][] VennAbersMultiP { get; set; } = [];

    /// <summary>Coverage-accuracy tradeoff curve: [threshold, coverage, accuracy] triplets from abstention sweep.</summary>
    public double[] AbstentionCoverageAccuracyCurve { get; set; } = [];

    /// <summary>Separate buy-side abstention threshold (when GbmUseSeparateAbstention=true).</summary>
    public double AbstentionThresholdBuy { get; set; } = 0.5;

    /// <summary>Separate sell-side abstention threshold (when GbmUseSeparateAbstention=true).</summary>
    public double AbstentionThresholdSell { get; set; } = 0.5;

    /// <summary>Partial dependence data: per-feature marginal response curves for top features. [featureIdx][gridPoints].</summary>
    public double[][] PartialDependenceData { get; set; } = [];

    /// <summary>Calibration loss component of Murphy decomposition (reliability).</summary>
    public double CalibrationLoss { get; set; }

    /// <summary>Refinement loss component of Murphy decomposition (resolution).</summary>
    public double RefinementLoss { get; set; }

    /// <summary>Average prediction stability: mean distance-to-decision-boundary on test set.</summary>
    public double PredictionStabilityScore { get; set; }

    /// <summary>TreeSHAP baseline (expected output = mean prediction). Used for per-prediction attribution.</summary>
    public double TreeShapBaseline { get; set; }

    /// <summary>Gain-weighted feature importance from tree splits (total gain per feature, normalised).</summary>
    public float[] GainWeightedImportance { get; set; } = [];

    /// <summary>Empirical Jackknife+ coverage on calibration set at the target alpha.</summary>
    public double JackknifeCoverage { get; set; }

    /// <summary>Per-feature MI-redundancy drop recommendation: index of the feature to drop in each redundant pair.</summary>
    public int[] RedundantFeatureDropIndices { get; set; } = [];

    // ── Rec #86: Mean Teacher EMA weights ────────────────────────────────────

    /// <summary>
    /// EMA of model weights maintained by <c>MLMeanTeacherWorker</c> (teacher network).
    /// Shape mirrors <see cref="Weights"/>. Null = teacher not yet initialised.
    /// </summary>
    public double[][]? TeacherWeights { get; set; }

    /// <summary>EMA teacher bias vector (one per base learner). Null = not initialised.</summary>
    public double[]? TeacherBiases { get; set; }

    // ── Rec #94: Class-conditional conformal quantiles ────────────────────────

    /// <summary>ACI Q̂ for Buy class (y=1). Separate from the global <see cref="AciConformalQHat"/>.</summary>
    public double AciConformalQHatBuy { get; set; }

    /// <summary>ACI Q̂ for Sell class (y=0).</summary>
    public double AciConformalQHatSell { get; set; }

    // ── Rec #95: Monotone I-spline calibration coefficients ──────────────────

    /// <summary>
    /// Non-negative I-spline coefficients fitted by <c>MLMonotoneSplineWorker</c>.
    /// Length = number of knots + 1. Null = spline calibration not fitted.
    /// </summary>
    public double[]? ISplineCoefficients { get; set; }

    /// <summary>I-spline knot locations in [0, 1]. Null when ISplineCoefficients is null.</summary>
    public double[]? ISplineKnots { get; set; }

    // ── Rec #96: Input-dependent temperature weights ──────────────────────────

    /// <summary>
    /// Weight vector for input-dependent temperature scaling: T(x) = TempBase + w^T · x_reduced.
    /// Length = number of conditioning features. Null = not fitted (falls back to TemperatureScale).
    /// </summary>
    public double[]? TempWeights { get; set; }

    /// <summary>Base temperature scalar for input-dependent scaling.</summary>
    public double TempBase { get; set; } = 1.0;

    // ── Rec #106: Minimax regret decision matrix ──────────────────────────────

    /// <summary>
    /// 2×2 regret matrix R[action, outcome] for the minimax regret rule.
    /// R[0,0]=0, R[0,1]=FP cost, R[1,0]=FN cost, R[1,1]=0.
    /// Null = argmax rule used (default).
    /// </summary>
    public double[][]? MinimaxRegretMatrix { get; set; }

    // ── Rec #109: Spectral norms per base learner ─────────────────────────────

    /// <summary>
    /// Spectral norm (largest singular value) of each base learner's weight vector.
    /// Length = K (number of learners). Null = not computed.
    /// </summary>
    public double[]? SpectralNorms { get; set; }

    // ── Rec #111: Thompson Sampling bandit arm parameters ────────────────────

    /// <summary>
    /// Beta distribution α counts per base learner (successes + 1). Length = K.
    /// Null = Thompson Sampling not yet initialised.
    /// </summary>
    public double[]? BanditAlphaCounts { get; set; }

    /// <summary>Beta distribution β counts per base learner (failures + 1). Length = K.</summary>
    public double[]? BanditBetaCounts { get; set; }

    // ── Rec #114: Per-learner Platt calibration arrays ───────────────────────

    /// <summary>
    /// Per-base-learner Platt A parameters (slope). Length = K.
    /// When non-null, overrides the global <see cref="PlattA"/> for ensemble stacking.
    /// </summary>
    public double[]? PlattAArray { get; set; }

    /// <summary>Per-base-learner Platt B parameters (bias). Length = K.</summary>
    public double[]? PlattBArray { get; set; }

    // ── Recs #126–135 ─────────────────────────────────────────────────────────
    /// <summary>Rec #126: Focal loss per-sample weight cache (JSON double[]).</summary>
    public string? FocalWeightsJson { get; set; }
    /// <summary>Rec #127: SAM perturbed weights snapshot (JSON double[][]).</summary>
    public double[][] SamWeights { get; set; } = [];
    /// <summary>Rec #128: Lookahead slow weights (mirrors Weights structure).</summary>
    public double[][] LookaheadSlowWeights { get; set; } = [];
    /// <summary>Rec #131: MC-Dropout variance threshold — suppress signal when variance exceeds this.</summary>
    public double McDropoutVarianceThreshold { get; set; } = 0.1;
    /// <summary>Rec #135: Empirical Fisher diagonal for natural gradient preconditioning.</summary>
    public double[] FisherDiagonal { get; set; } = [];

    // ── Recs #160, #168 ────────────────────────────────────────────────────────
    /// <summary>Rec #160: Meta-SGD per-parameter learning rates (JSON double[][]).</summary>
    public double[][] MetaSgdLrJson { get; set; } = [];
    /// <summary>Rec #168: Hedged prediction multiplicative weights over K base predictors.</summary>
    public double[] HedgeWeightsJson { get; set; } = [];

    // Recs #186, #188, #195
    /// <summary>Attention weights over the LookbackWindow time steps (length = LookbackWindow).</summary>
    public double[]   AttentionWeights   { get; set; } = [];
    /// <summary>PCA eigenvectors (row-major: outer = component, inner = feature index).</summary>
    public double[][] PcaEigenvectors    { get; set; } = [];
    /// <summary>Per-session Platt A parameters. Index 0 = Asian, 1 = London, 2 = NewYork.</summary>
    public double[]   SessionPlattA      { get; set; } = []; // [Asian, London, NewYork]
    /// <summary>Per-session Platt B parameters. Index 0 = Asian, 1 = London, 2 = NewYork.</summary>
    public double[]   SessionPlattB      { get; set; } = []; // [Asian, London, NewYork]

    // Recs #207, #209
    /// <summary>Rec #207 — SGDR best checkpoint weights serialised as JSON.</summary>
    public string  SgdrBestWeightsJson { get; set; } = string.Empty;  // Rec #207 — SGDR best checkpoint
    /// <summary>Rec #209 — SWAG posterior mean (flattened 1-D projection of Weights[0]).</summary>
    public double[] SwagMeanWeights1D  { get; set; } = [];            // Rec #209 — SWAG posterior mean (flattened)
    /// <summary>Rec #209 — SWAG diagonal covariance (flattened 1-D).</summary>
    public double[] SwagCovDiag1D      { get; set; } = [];            // Rec #209 — SWAG diagonal covariance (flattened)

    // Recs #236–265
    public double[]? FederatedAggWeights { get; set; }     // #236 FedAvg aggregated weights (1D flattened)
    public double[]? TtaAugProbsJson { get; set; }         // #237 TTA augmentation probabilities per class
    public double[]? IrmPenaltyHistory { get; set; }       // #238 IRM environment penalty history
    public double[]? EwcFisherDiag { get; set; }           // #239 EWC Fisher diagonal (task-specific)
    public double[]? JacobianSensitivity { get; set; }     // #252 per-feature Jacobian sensitivity scores
    public double[]? RffPosteriorWeights { get; set; }     // #257 Random Fourier Feature posterior weights
    public double[]? HypernetworkCondWeights { get; set; } // #258 hypernetwork conditioning weights
    public double[]? SpectralMixtureWeights { get; set; }  // #259 spectral mixture kernel weights

    // Recs #266–295
    public double[]? LinUcbTheta { get; set; }            // #278 LinUCB per-threshold context weights
    public double[]? ByolOnlineWeights { get; set; }      // #283 BYOL online encoder weights (1D flat)
    public double[]? SimSiamProjectorW { get; set; }      // #284 SimSiam projector weights
    public double[][]? HamiltonTransMatrix { get; set; }  // #290 Hamilton 2x2 transition matrix
    public double[]? AdaHessianHessDiag { get; set; }     // #291 diagonal Hessian approximation
    public double[]? HrpWeights { get; set; }             // #287 HRP model allocation weights

    // Recs #296–325
    public double[]? HuberWeights { get; set; }           // #296 Huber regression coefficient vector
    public double[]? SsaSingularValues { get; set; }      // #298 SSA top singular values
    public double[]? MwuModelWeights { get; set; }        // #301 MWU exponential weights over base models
    public double[][]? KoopmanEigenvectors { get; set; }  // #309 Koopman EDMD eigenvectors
    public double[]? HessianTopEigenvalues { get; set; }  // #310 top-K Hessian eigenvalues (power iteration)
    public double[]? TaskVector { get; set; }             // #325 task arithmetic vector (θ_fine - θ_pre)
    public double[]? FtrlWeights { get; set; }            // #299 FTRL accumulated weight vector
    public double[]? NclCorrelationPenalty { get; set; }  // #300 NCL pairwise correlation penalties

    // ── Recs #326–355 ─────────────────────────────────────────────────────────

    /// <summary>Rec #326: Low-rank weight decomposition factors (outer array = rank components, inner = feature weights).</summary>
    public double[][]? LowRankFactors { get; set; }

    /// <summary>Rec #336: Particle filter importance weights. Length = particle count.</summary>
    public double[]? ParticleWeights { get; set; }

    /// <summary>Rec #337: Gaussian anomaly score parameters (mean and variance per feature).</summary>
    public double[]? GasParams { get; set; }

    /// <summary>Rec #341: Accumulated Local Effects per feature. Outer array = feature index, inner = ALE curve values.</summary>
    public double[][]? AleEffects { get; set; }

    /// <summary>Rec #347: Node2Vec graph embedding of feature correlations.</summary>
    public double[]? Node2VecEmbedding { get; set; }

    /// <summary>Rec #349: Survival analysis baseline hazard. Length = number of time steps.</summary>
    public double[]? SurvivalHazard { get; set; }

    /// <summary>Rec #351: Flow matching velocity field weights.</summary>
    public double[]? FlowMatchingVelocity { get; set; }

    /// <summary>Rec #352: Consistency model distillation weights.</summary>
    public double[]? ConsistencyDistillWeights { get; set; }

    // ── Recs #370, #372, #378, #379, #381 ────────────────────────────────────

    /// <summary>Rec #370: SAGE (Shapley Additive Global importancE) feature importance scores. Length = feature count.</summary>
    public double[]? SageImportanceScores { get; set; }

    /// <summary>Rec #379: Energy-Based Model energy scores per training sample. Null = EBM not trained.</summary>
    public double[]? EbmEnergyScores { get; set; }

    /// <summary>Rec #381: Beta-VAE posterior mean (μ) of the latent encoding. Length = LatentDimensions.</summary>
    public double[]? BetaVaeLatentMu { get; set; }

    /// <summary>Rec #378: Fractional differencing weight vector applied to price-derived features. Length = window size.</summary>
    public double[]? FractionalDiffWeights { get; set; }

    /// <summary>Rec #372: Serialised Rotation Forest rotation matrices as JSON (array of PCA rotation matrices per tree subset).</summary>
    public string? RotationForestJson { get; set; }

    // ── Recs #388–400 ─────────────────────────────────────────────────────────

    /// <summary>Rec #388: ROCKET mean PPV statistics per kernel. Length = numKernels.</summary>
    public double[]? RocketFeatureStats { get; set; }

    /// <summary>Rec #388: ROCKET kernel weights (outer = kernel index, inner = kernel tap weights). Required for deterministic inference.</summary>
    public double[][]? RocketKernelWeights { get; set; }

    /// <summary>Rec #388: ROCKET kernel dilations per kernel. Length = numKernels.</summary>
    public int[]? RocketKernelDilations { get; set; }

    /// <summary>Rec #388: ROCKET kernel padding flags per kernel. Length = numKernels.</summary>
    public bool[]? RocketKernelPaddings { get; set; }

    /// <summary>Rec #388: ROCKET kernel lengths per kernel. Length = numKernels.</summary>
    public int[]? RocketKernelLengths { get; set; }

    /// <summary>Rec #388: ROCKET-space feature means for standardisation. Length = 2 × numKernels.</summary>
    public double[]? RocketFeatureMeans { get; set; }

    /// <summary>Rec #388: ROCKET-space feature standard deviations for standardisation. Length = 2 × numKernels.</summary>
    public double[]? RocketFeatureStds { get; set; }

    /// <summary>Rec #388: Deterministic seed used for kernel generation. Stored for reproducibility.</summary>
    public int RocketKernelSeed { get; set; }

    /// <summary>Rec #389: TabNet per-step mean attention weights as JSON. Length = F.</summary>
    public string? TabNetAttentionJson { get; set; }

    /// <summary>Rec #389: TabNet per-step attention mask weights (outer = step, inner = F). Used for warm-start transfer.</summary>
    public double[][]? TabNetStepAttentionWeights { get; set; }

    /// <summary>
    /// Legacy single-value TabNet output-head weight kept for compatibility with older
    /// audit/test flows. Newer snapshots persist the full output head in <see cref="Weights"/>.
    /// </summary>
    public double TabNetOutputWeight { get; set; }

    /// <summary>
    /// Number of raw pre-augmentation features used by the TabNet preprocessing path.
    /// Equals <see cref="Features"/>.Length when polynomial augmentation is disabled.
    /// </summary>
    public int TabNetRawFeatureCount { get; set; }

    /// <summary>
    /// Indices of the raw standardised features used to generate TabNet degree-2 interaction
    /// features. Empty = polynomial augmentation disabled.
    /// </summary>
    public int[]? TabNetPolyTopFeatureIndices { get; set; }

    // ── TabNet v3 architecture weights (true TabNet: shared + step-specific Feature Transformer with GLU + BN) ──

    /// <summary>Rec #389 v3: Shared FC layer weights [layer][outDim][inDim].</summary>
    public double[][][]? TabNetSharedWeights { get; set; }

    /// <summary>Rec #389 v3: Shared FC layer biases [layer][dim].</summary>
    public double[][]? TabNetSharedBiases { get; set; }

    /// <summary>Rec #389 v3: Shared GLU gate weights [layer][outDim][inDim].</summary>
    public double[][][]? TabNetSharedGateWeights { get; set; }

    /// <summary>Rec #389 v3: Shared GLU gate biases [layer][dim].</summary>
    public double[][]? TabNetSharedGateBiases { get; set; }

    /// <summary>Rec #389 v3: Step-specific FC weights [step][layer][outDim][inDim].</summary>
    public double[][][][]? TabNetStepFcWeights { get; set; }

    /// <summary>Rec #389 v3: Step-specific FC biases [step][layer][dim].</summary>
    public double[][][]? TabNetStepFcBiases { get; set; }

    /// <summary>Rec #389 v3: Step-specific GLU gate weights [step][layer][outDim][inDim].</summary>
    public double[][][][]? TabNetStepGateWeights { get; set; }

    /// <summary>Rec #389 v3: Step-specific GLU gate biases [step][layer][dim].</summary>
    public double[][][]? TabNetStepGateBiases { get; set; }

    /// <summary>Rec #389 v3: Attentive Transformer FC weights [step][attDim][inDim].</summary>
    public double[][][]? TabNetAttentionFcWeights { get; set; }

    /// <summary>Rec #389 v3: Attentive Transformer FC biases [step][attDim].</summary>
    public double[][]? TabNetAttentionFcBiases { get; set; }

    /// <summary>Rec #389 v3: BN scale (gamma) params [bnIndex][dim].</summary>
    public double[][]? TabNetBnGammas { get; set; }

    /// <summary>Rec #389 v3: BN shift (beta) params [bnIndex][dim].</summary>
    public double[][]? TabNetBnBetas { get; set; }

    /// <summary>Rec #389 v3: BN running means for inference [bnIndex][dim].</summary>
    public double[][]? TabNetBnRunningMeans { get; set; }

    /// <summary>Rec #389 v3: BN running variances for inference [bnIndex][dim].</summary>
    public double[][]? TabNetBnRunningVars { get; set; }

    /// <summary>Rec #389 v3: Full output-head weights vector [hiddenDim]. Replaces legacy scalar TabNetOutputWeight.</summary>
    public double[]? TabNetOutputHeadWeights { get; set; }

    /// <summary>Rec #389 v3: Output-head bias.</summary>
    public double TabNetOutputHeadBias { get; set; }

    /// <summary>Rec #389 v3: Relaxation γ value used during training (stored for inference reproducibility).</summary>
    public double TabNetRelaxationGamma { get; set; } = 1.5;

    /// <summary>Rec #389 v3: Whether attention masks were trained with sparsemax or softmax.</summary>
    public bool TabNetUseSparsemax { get; set; } = true;

    /// <summary>Rec #389 v3: Whether the feature-transformer blocks used GLU gates or plain BN-linear activations.</summary>
    public bool TabNetUseGlu { get; set; } = true;

    /// <summary>Rec #389 v3: Hidden dimension used during training.</summary>
    public int TabNetHiddenDim { get; set; }

    /// <summary>Rec #389 v3: Initial BN FC weights for step-0 attention symmetry [F][F].</summary>
    public double[][]? TabNetInitialBnFcW { get; set; }

    /// <summary>Rec #389 v3: Initial BN FC biases for step-0 attention [F].</summary>
    public double[]? TabNetInitialBnFcB { get; set; }

    /// <summary>Rec #389 v3: Per-step attention importance breakdown [step][F].</summary>
    public double[][]? TabNetPerStepAttention { get; set; }

    /// <summary>Rec #389 v3: Per-step mean attention entropy (signal quality metric). Low = confident feature selection.</summary>
    public double[]? TabNetAttentionEntropy { get; set; }

    /// <summary>Rec #389 v3: Fraction of features selected (attention > 1e-6) at each decision step.</summary>
    public double[]? TabNetPerStepSparsity { get; set; }

    /// <summary>Rec #389 v3: Running-stat drift score per BN layer. Higher values imply train/inference normalization mismatch risk.</summary>
    public double[]? TabNetBnDriftByLayer { get; set; }

    /// <summary>Rec #389 v3: Mean aggregated hidden activation over the calibration/reference window.</summary>
    public double[]? TabNetActivationCentroid { get; set; }

    /// <summary>Rec #389 v3: Mean Euclidean distance to <see cref="TabNetActivationCentroid"/> over the reference window.</summary>
    public double TabNetActivationDistanceMean { get; set; }

    /// <summary>Rec #389 v3: Std dev of Euclidean distance to <see cref="TabNetActivationCentroid"/> over the reference window.</summary>
    public double TabNetActivationDistanceStd { get; set; }

    /// <summary>Rec #389 v3: Attention-entropy threshold above which deployed predictions are considered unusually uncertain.</summary>
    public double TabNetAttentionEntropyThreshold { get; set; }

    /// <summary>Rec #389 v3: Combined TabNet uncertainty threshold used by live scoring for abstention / OOD escalation.</summary>
    public double TabNetUncertaintyThreshold { get; set; }

    /// <summary>Rec #389 v3: Fraction of warm-start parameters successfully reused from the parent snapshot.</summary>
    public double TabNetWarmStartReuseRatio { get; set; }

    /// <summary>Rec #389 v3: Whether the optional prune-and-retrain pass was accepted into the final deployed snapshot.</summary>
    public bool TabNetPruningAccepted { get; set; }

    /// <summary>Rec #389 v3: Composite pruning score delta (accepted model minus baseline). Positive is better.</summary>
    public double TabNetPruningScoreDelta { get; set; }

    /// <summary>Rec #389 v3: Maximum absolute difference between trainer forward-pass raw probability and deployed inference on audited samples.</summary>
    public double TabNetTrainInferenceParityMaxError { get; set; }

    /// <summary>Rec #389 v3: Post-train audit findings. Empty means the snapshot passed the TabNet audit cleanly.</summary>
    public string[]? TabNetAuditFindings { get; set; }

    /// <summary>Rec #389 v3: Structured post-train audit artifact.</summary>
    public TabNetAuditArtifact? TabNetAuditArtifact { get; set; }

    /// <summary>Rec #389 v3: Structured architecture-search trace.</summary>
    public TabNetAutoTuneTraceEntry[]? TabNetAutoTuneTrace { get; set; }

    /// <summary>Rec #389 v3: Structured warm-start compatibility/reuse summary.</summary>
    public TabNetWarmStartArtifact? TabNetWarmStartArtifact { get; set; }

    /// <summary>Rec #389 v3: Structured pruning decision artifact.</summary>
    public TabNetPruningDecisionArtifact? TabNetPruningDecision { get; set; }

    /// <summary>Rec #389 v3: Structured deployed-calibration artifact.</summary>
    public TabNetCalibrationArtifact? TabNetCalibrationArtifact { get; set; }

    /// <summary>Rec #389 v3: Structured drift/stationarity diagnostics for the final training window.</summary>
    public TabNetDriftArtifact? TabNetDriftArtifact { get; set; }

    /// <summary>Rec #389 v3: Mean absolute calibration residual over the reference calibration window.</summary>
    public double TabNetCalibrationResidualMean { get; set; }

    /// <summary>Rec #389 v3: Std dev of absolute calibration residual over the reference calibration window.</summary>
    public double TabNetCalibrationResidualStd { get; set; }

    /// <summary>Rec #389 v3: Upper quantile threshold used to scale margin-based calibration uncertainty at inference time.</summary>
    public double TabNetCalibrationResidualThreshold { get; set; }

    /// <summary>Rec #390: FT-Transformer per-feature embedding weights (outer = feature, inner = dim).</summary>
    public double[][]? FtTransformerEmbedWeights { get; set; }

    /// <summary>
    /// Rec #390: raw feature-space width expected before optional <see cref="RawFeatureIndices"/>
    /// projection. Equal to <see cref="Features"/> length when the snapshot consumes the raw
    /// feature order directly.
    /// </summary>
    public int FtTransformerRawFeatureCount { get; set; }

    /// <summary>Rec #390: FT-Transformer per-feature embedding biases (outer = feature, inner = dim).</summary>
    public double[][]? FtTransformerEmbedBiases { get; set; }

    /// <summary>Rec #390 (legacy): FT-Transformer single QKV weight matrix. Superseded by separate Q/K/V.</summary>
    public double[][]? FtTransformerQkvWeights { get; set; }

    /// <summary>Rec #390: FT-Transformer output linear head weights. Length = EmbedDim.</summary>
    public double[]? FtTransformerOutputWeights { get; set; }

    /// <summary>Rec #390: FT-Transformer output linear head bias.</summary>
    public double FtTransformerOutputBias { get; set; }

    /// <summary>Rec #390: FT-Transformer query projection weights [EmbedDim][EmbedDim].</summary>
    public double[][]? FtTransformerWq { get; set; }

    /// <summary>Rec #390: FT-Transformer key projection weights [EmbedDim][EmbedDim].</summary>
    public double[][]? FtTransformerWk { get; set; }

    /// <summary>Rec #390: FT-Transformer value projection weights [EmbedDim][EmbedDim].</summary>
    public double[][]? FtTransformerWv { get; set; }

    /// <summary>Rec #390: FT-Transformer multi-head output projection weights [EmbedDim][EmbedDim].</summary>
    public double[][]? FtTransformerWo { get; set; }

    /// <summary>Rec #390: FT-Transformer FFN first layer weights [EmbedDim][FfnDim].</summary>
    public double[][]? FtTransformerWff1 { get; set; }

    /// <summary>Rec #390: FT-Transformer FFN first layer biases. Length = FfnDim.</summary>
    public double[]? FtTransformerBff1 { get; set; }

    /// <summary>Rec #390: FT-Transformer FFN second layer weights [FfnDim][EmbedDim].</summary>
    public double[][]? FtTransformerWff2 { get; set; }

    /// <summary>Rec #390: FT-Transformer FFN second layer biases. Length = EmbedDim.</summary>
    public double[]? FtTransformerBff2 { get; set; }

    /// <summary>Rec #390: FT-Transformer LayerNorm1 gamma (scale). Length = EmbedDim.</summary>
    public double[]? FtTransformerGamma1 { get; set; }

    /// <summary>Rec #390: FT-Transformer LayerNorm1 beta (shift). Length = EmbedDim.</summary>
    public double[]? FtTransformerBeta1 { get; set; }

    /// <summary>Rec #390: FT-Transformer LayerNorm2 gamma (scale). Length = EmbedDim.</summary>
    public double[]? FtTransformerGamma2 { get; set; }

    /// <summary>Rec #390: FT-Transformer LayerNorm2 beta (shift). Length = EmbedDim.</summary>
    public double[]? FtTransformerBeta2 { get; set; }

    /// <summary>Rec #390: FT-Transformer embedding dimension used during training.</summary>
    public int FtTransformerEmbedDim { get; set; }

    /// <summary>Rec #390: FT-Transformer number of attention heads used during training.</summary>
    public int FtTransformerNumHeads { get; set; }

    /// <summary>Rec #390: FT-Transformer FFN hidden dimension used during training.</summary>
    public int FtTransformerFfnDim { get; set; }

    /// <summary>Rec #390: Number of stacked transformer blocks used during training.</summary>
    public int FtTransformerNumLayers { get; set; }

    /// <summary>
    /// Maximum absolute difference between trainer forward-pass raw probability and deployed
    /// FT inference on audited samples.
    /// </summary>
    public double FtTransformerTrainInferenceParityMaxError { get; set; }

    /// <summary>
    /// Maximum absolute difference between trainer GBM raw probability and deployed
    /// GBM inference on audited samples.
    /// </summary>
    public double GbmTrainInferenceParityMaxError { get; set; }

    /// <summary>Rec #390: FT-Transformer additional layer weights (layers 1..N-1) serialised as JSON (legacy, kept for backward compat).</summary>
    public string? FtTransformerAdditionalLayersJson { get; set; }

    /// <summary>Rec #390: FT-Transformer additional layer weights (layers 1..N-1) serialised as binary with CRC32 trailer.</summary>
    public byte[]? FtTransformerAdditionalLayersBytes { get; set; }

    /// <summary>Rec #390: FT-Transformer per-head positional bias for layer 0. Shape [NumHeads][S*S].</summary>
    public double[][]? FtTransformerPosBias { get; set; }

    /// <summary>Rec #390: FT-Transformer learnable [CLS] token embedding. Length = EmbedDim.</summary>
    public double[]? FtTransformerClsToken { get; set; }

    /// <summary>Rec #390: FT-Transformer final LayerNorm gamma (pre-norm output). Length = EmbedDim.</summary>
    public double[]? FtTransformerGammaFinal { get; set; }

    /// <summary>Rec #390: FT-Transformer final LayerNorm beta (pre-norm output). Length = EmbedDim.</summary>
    public double[]? FtTransformerBetaFinal { get; set; }

    /// <summary>Rec #391: NODE oblivious decision tree leaf values (outer = layer, inner = ODT leaves).</summary>
    public double[][]? NodeLeafValues { get; set; }

    /// <summary>Rec #392: Mamba SSM A diagonal state parameters. Length = StateSize.</summary>
    public double[]? MambaStateA { get; set; }

    /// <summary>Rec #396: ICA top independent component mixing vector. Length = feature count.</summary>
    public double[]? IcaMixingVector { get; set; }

    /// <summary>Rec #397: NMF first basis vector (dictionary atom). Length = feature count.</summary>
    public double[]? NmfBasisVector { get; set; }

    /// <summary>Rec #415: Sparse PCA loadings for the first extracted component. Length = feature count.</summary>
    public double[]? SparsePcaLoadings { get; set; }

    /// <summary>Rec #411: Ordinal regression cutpoint thresholds between K classes. Length = K − 1.</summary>
    public double[]? OrdinalThresholds { get; set; }

    // ── Recs #416–445 ─────────────────────────────────────────────────────────

    /// <summary>Rec #416: NCDE hidden state trajectory (last 10 time steps). Length = HiddenDim × 10.</summary>
    public double[]? NcdeHiddenTrajectory { get; set; }

    /// <summary>Rec #418: DeepAR LSTM hidden state snapshot. Length = LstmHiddenDim.</summary>
    public double[]? DeepArLstmHidden { get; set; }

    /// <summary>Rec #422: MAF autoregressive mean/log-std parameters per feature. Length = 2×F.</summary>
    public double[]? MafArParams { get; set; }

    /// <summary>Rec #423: NSF spline knot positions per feature (outer = feature, inner = knots).</summary>
    public double[][]? NsfKnotPositions { get; set; }

    /// <summary>Rec #424: N-HiTS per-stack expansion coefficients. Outer = stack, inner = coeffs.</summary>
    public double[][]? NHitsStackCoeffs { get; set; }

    /// <summary>Rec #425: PatchTST CLS token embedding. Length = PatchEmbedDim.</summary>
    public double[]? PatchTstClsToken { get; set; }

    /// <summary>Rec #426: iTransformer variate attention weights. Length = F (num variates).</summary>
    public double[]? ITransformerVariateAttn { get; set; }

    /// <summary>Rec #427: Diffusion Policy learned noise schedule (β_t). Length = DiffusionSteps.</summary>
    public double[]? DiffusionPolicyBetas { get; set; }

    /// <summary>Rec #428: ENN epistemic index distribution parameters [mu, log-var]. Length = 2×EnnDim.</summary>
    public double[]? EnnEpistemicParams { get; set; }

    /// <summary>Rec #431: DDPG actor network output weights (last layer). Length = ActionDim×HiddenDim.</summary>
    public double[]? DdpgActorWeights { get; set; }

    /// <summary>Rec #440: S4D diagonal A matrix real parts. Length = StateSize.</summary>
    public double[]? S4dADiagonal { get; set; }

    /// <summary>Rec #444: DDPM learned β schedule. Length = DiffusionTimesteps.</summary>
    public double[]? DdpmBetaSchedule { get; set; }

    /// <summary>Rec #445: Crossformer router attention weights per segment. Length = NumSegments.</summary>
    public double[]? CrossformerRouterWeights { get; set; }

    // ── Recs #446–475 ────────────────────────────────────────────────────────

    /// <summary>Rec #446 TimesNet: learned period importance weights from FFT detection.</summary>
    public double[]? TimesNetPeriodWeights { get; set; }

    /// <summary>Rec #447 SVGP: inducing point locations in feature space [M × F].</summary>
    public double[][]? SvgpInducingPoints { get; set; }

    // ── True SVGP variational parameters (Titsias 2009) ──────────────────────

    /// <summary>
    /// SVGP variational mean m [M]. Variational posterior q(u) = N(m, S).
    /// Together with <see cref="SvgpInducingPoints"/> and <see cref="SvgpArdLengthScales"/>,
    /// this fully specifies the posterior mean: μ*(x) = K_xz K_mm^{-1} m.
    /// </summary>
    public double[]? SvgpVariationalMean { get; set; }

    /// <summary>
    /// SVGP log-diagonal of the Cholesky factor L_S [M], where S = L_S L_S^T (full-rank).
    /// Diagonal elements are stored as log values to ensure positivity.
    /// Used to compute posterior variance: σ²*(x) = k(x,x) − K_xz(K_mm^{-1} − K_mm^{-1}SK_mm^{-1})K_xz^T.
    /// </summary>
    public double[]? SvgpVariationalLogSDiag { get; set; }

    /// <summary>
    /// SVGP strictly-lower-triangular part of the Cholesky factor L_S, stored row-major [M×M].
    /// Together with <see cref="SvgpVariationalLogSDiag"/> this fully specifies the full-rank
    /// variational covariance S = L_S L_S^T (off-diagonal elements are unconstrained).
    /// Null for models trained with the legacy diagonal mean-field approximation.
    /// </summary>
    public double[]? SvgpVariationalLSOffDiag { get; set; }

    /// <summary>
    /// Per-feature ARD length scales [F] for the RBF kernel.
    /// K(x,x') = σ_f² exp(−½ Σ_d (x_d−x'_d)² / l_d²).
    /// Optimised jointly with the variational parameters via ELBO maximisation.
    /// </summary>
    public double[]? SvgpArdLengthScales { get; set; }

    /// <summary>Signal variance σ_f² of the ARD RBF kernel. Optimised via ELBO.</summary>
    public double SvgpSignalVariance { get; set; } = 1.0;

    /// <summary>Observation noise variance σ_noise² fitted during ELBO optimisation.</summary>
    public double SvgpNoiseVariance { get; set; } = 0.1;

    public SvgpDriftArtifact? SvgpDriftArtifact { get; set; }
    public SvgpWarmStartArtifact? SvgpWarmStartArtifact { get; set; }
    public SvgpAuditArtifact? SvgpAuditArtifact { get; set; }
    public double SvgpCalibrationResidualMean { get; set; }
    public double SvgpCalibrationResidualStd { get; set; }
    public double SvgpCalibrationResidualThreshold { get; set; }
    public double SvgpAdversarialAuc { get; set; }
    public double SvgpCalibrationLoss { get; set; }
    public double SvgpRefinementLoss { get; set; }
    public double SvgpPredictionStabilityScore { get; set; }
    public int SvgpSelectionSamples { get; set; }

    /// <summary>Rec #448 ESN: trained output layer weights from ridge regression.</summary>
    public double[]? EsnOutputWeights { get; set; }

    /// <summary>Rec #449 ELM: trained output layer weights from Moore-Penrose pseudoinverse.</summary>
    public double[]? ElmOutputWeights { get; set; }

    /// <summary>Rec #449 ELM: random input-layer weights [K × HiddenSize × FeatureCount] row-major per learner.</summary>
    public double[][]? ElmInputWeights { get; set; }

    /// <summary>Rec #449 ELM: random input-layer biases [K × HiddenSize] per learner.</summary>
    public double[][]? ElmInputBiases { get; set; }

    /// <summary>Rec #449 ELM: hidden layer size used during training.</summary>
    public int ElmHiddenDim { get; set; }

    /// <summary>
    /// Effective hidden-unit dropout rate used during ELM training.
    /// Persisted so MC-dropout inference can mirror the trained sparsity level.
    /// </summary>
    public double? ElmDropoutRate { get; set; }

    /// <summary>
    /// ELM: per-learner inverse Gram matrix P = (H^T H + λI)^{-1}, flattened row-major [K][H*H].
    /// Stored after initial training so that <c>ElmModelTrainer.UpdateOnline</c>
    /// can apply Sherman-Morrison rank-1 updates without a full retrain.
    /// Null for models trained before online update support was added.
    /// System.Text.Json cannot serialise <c>double[,]</c>, so we store flattened 1D arrays
    /// and use <see cref="ElmInverseGramDim"/> to recover the square dimension.
    /// </summary>
    public double[][]? ElmInverseGram { get; set; }

    /// <summary>
    /// Per-learner hidden dimension H for each inverse Gram matrix in <see cref="ElmInverseGram"/>.
    /// <c>ElmInverseGram[k]</c> has length <c>ElmInverseGramDim[k] * ElmInverseGramDim[k]</c>.
    /// </summary>
    public int[]? ElmInverseGramDim { get; set; }

    public ElmDriftArtifact? ElmDriftArtifact { get; set; }
    public ElmWarmStartArtifact? ElmWarmStartArtifact { get; set; }
    public ElmAuditArtifact? ElmAuditArtifact { get; set; }
    public double ElmCalibrationResidualMean { get; set; }
    public double ElmCalibrationResidualStd { get; set; }
    public double ElmCalibrationResidualThreshold { get; set; }

    public BaggedLogisticDriftArtifact? BaggedLogisticDriftArtifact { get; set; }
    public BaggedLogisticWarmStartArtifact? BaggedLogisticWarmStartArtifact { get; set; }
    public BaggedLogisticAuditArtifact? BaggedLogisticAuditArtifact { get; set; }
    public double BaggedLogisticCalibrationResidualMean { get; set; }
    public double BaggedLogisticCalibrationResidualStd { get; set; }
    public double BaggedLogisticCalibrationResidualThreshold { get; set; }

    public DannDriftArtifact? DannDriftArtifact { get; set; }
    public DannWarmStartArtifact? DannWarmStartArtifact { get; set; }
    public DannAuditArtifact? DannAuditArtifact { get; set; }
    public double DannCalibrationResidualMean { get; set; }
    public double DannCalibrationResidualStd { get; set; }
    public double DannCalibrationResidualThreshold { get; set; }

    /// <summary>Rec #450 DLinear: trend component linear projection weights.</summary>
    public double[]? DLinearTrendWeights { get; set; }

    /// <summary>Rec #450 DLinear: seasonal/residual component linear projection weights.</summary>
    public double[]? DLinearSeasonalWeights { get; set; }

    /// <summary>Rec #451 FEDformer: selected Fourier mode weights for frequency-enhanced attention.</summary>
    public double[]? FedformerModeWeights { get; set; }

    /// <summary>Rec #452 CARD: score network weights for conditional diffusion regression.</summary>
    public double[][]? CardScoreNetwork { get; set; }

    /// <summary>Rec #454 FITS: complex frequency coefficients for interpolation [K complex = 2K doubles].</summary>
    public double[]? FitsFreqCoeffs { get; set; }

    /// <summary>Rec #455 Autoformer: trend projection parameters and series mean.</summary>
    public double[]? AutoformerTrendParams { get; set; }

    /// <summary>Rec #458 RetNet: per-head retention decay rates gamma.</summary>
    public double[]? RetNetDecayRates { get; set; }

    /// <summary>Rec #459 KoopmanNet: learned Koopman operator matrix K (linearised dynamics).</summary>
    public double[][]? KoopmanMatrix { get; set; }

    /// <summary>Rec #460 EDL: evidence network final-layer weights for Dirichlet parameterisation.</summary>
    public double[]? EdlEvidenceWeights { get; set; }

    /// <summary>Rec #461 DSSM: learned transition matrix weights (time-varying SSM).</summary>
    public double[][]? DssmTransitionWeights { get; set; }

    /// <summary>Rec #462 LatentODE: ODE-RNN encoder weights for mapping observations to latent z0.</summary>
    public double[][]? LatentOdeEncoderWeights { get; set; }

    /// <summary>Rec #463 GRU-D: learned exponential decay rates per feature for irregular data.</summary>
    public double[]? GrudDecayRates { get; set; }

    /// <summary>Rec #464 WNN: DWT wavelet coefficient features from decomposed price series.</summary>
    public double[]? WnnWaveletCoeffs { get; set; }

    /// <summary>Rec #465 CVAE: latent space mean vector mu from encoder at last training step.</summary>
    public double[]? CvaeLatentMu { get; set; }

    /// <summary>Rec #467 Hyena: implicit filter weights for sub-quadratic convolution.</summary>
    public double[][]? HyenaFilterWeights { get; set; }

    /// <summary>Rec #468 Chronos: quantile bin boundaries used for time-series tokenisation.</summary>
    public double[]? ChronosQuantileBins { get; set; }

    /// <summary>Rec #471 GAT: multi-head attention weight matrices per graph layer.</summary>
    public double[][]? GatAttentionWeights { get; set; }

    /// <summary>Rec #472 PINN: learned PDE coefficients [theta (mean-reversion), mu (long-run mean), sigma (vol)].</summary>
    public double[]? PinnPdeCoeffs { get; set; }

    /// <summary>Rec #473 BNN-VI: variational posterior mean weights per layer.</summary>
    public double[][]? BnnWeightMeans { get; set; }

    /// <summary>Rec #474 RWKV: per-layer time decay vectors for retention mechanism.</summary>
    public double[][]? RwkvDecayVectors { get; set; }

    // Batch 18 trainers (#476–500)
    public double[][]? Td3ActorWeights { get; set; }       // TD3 (#476) — actor network weights
    public double[][]? Td3CriticWeights { get; set; }      // TD3 — critic network weights
    public double[][]? InformerWeights { get; set; }       // Informer (#477) — ProbSparse attention weights
    public double[][]? XlstmWeights { get; set; }          // xLSTM (#478) — exponential gate weights
    public double[][]? TsMaeWeights { get; set; }          // TS-MAE (#480) — masked autoencoder weights
    public double[][]? IqnWeights { get; set; }            // IQN (#481) — implicit quantile network weights
    public double[][]? DtWeights { get; set; }             // Decision Transformer (#482) — causal attn weights
    public double[][]? SngpWeights { get; set; }           // SNGP (#483) — spectral-normalized GP weights
    public double[][]? MegaWeights { get; set; }           // MEGA (#485) — multi-dim damped EMA weights
    public double[][]? TimeMixerWeights { get; set; }      // TimeMixer (#487) — mixing layer weights
    public double[][]? TsMixerWeights { get; set; }        // TSMixer (#488) — inter/intra mixing weights
    public double[][]? DreamerWeights { get; set; }        // Dreamer V3 (#490) — world-model weights
    public double[][]? TransXlWeights { get; set; }        // Transformer-XL (#492) — segment memory weights
    public double[][]? DiffTsWeights { get; set; }         // Diffusion-TS (#493) — score network weights
    public double[][]? RainbowWeights { get; set; }        // Rainbow DQN (#494) — noisy net + distributional
    public double[][]? IqlWeights { get; set; }            // IQL (#495) — implicit Q-learning weights
    public double[][]? CqlWeights { get; set; }            // CQL (#496) — conservative Q weights
    public double[][]? MtlWeights { get; set; }            // MTL (#499) — shared encoder weights
    public double[][]? DannWeights { get; set; }           // DANN (#500) — domain-adversarial weights

    // Batch 19 trainers (#501–510)
    public double[][]? NBeatsWeights { get; set; }         // N-BEATS (#501) — per-block MLP + backcast/forecast heads
    public double[][]? NLinearWeights { get; set; }        // NLinear (#502) — single normalised linear layer
    public double[][]? SciNetWeights { get; set; }         // SCINet (#503) — SCI-block interaction weights
    public double[][]? EfficientZeroWeights { get; set; }  // EfficientZero (#504) — world-model component weights
    public double[][]? MocoWeights { get; set; }           // MoCo v2 (#505) — online encoder + classifier weights

    // Batch 20 trainers (#506–510)
    public double[][]? LoraWeights { get; set; }           // LoRA (#506) — composed final weights [w_final, A]
    public double[][]? BekkWeights { get; set; }           // BEKK-GARCH (#507) — c, a, b parameter rows
    public double[][]? FarimaWeights { get; set; }         // FARIMA (#508) — [[best_d, best_phi]] single row
    public double[][]? QrfWeights { get; set; }            // Quantile RF (#509) — feature importance as single row
    public QrfDriftArtifact? QrfDriftArtifact { get; set; }
    public QrfWarmStartArtifact? QrfWarmStartArtifact { get; set; }
    public QrfAuditArtifact? QrfAuditArtifact { get; set; }
    public double QrfCalibrationResidualMean { get; set; }
    public double QrfCalibrationResidualStd { get; set; }
    public double QrfCalibrationResidualThreshold { get; set; }
    public double[][]? SiameseWeights { get; set; }        // Siamese (#510) — W1 rows followed by W2 rows

    // Batch 19 trainer snapshots
    public double[][]? GFlowNetFlows         { get; set; }
    public double[][]? SfAdamWeights         { get; set; }
    public double[][]? SnnWeights            { get; set; }
    public double[]?   GrokFastGradBuffer    { get; set; }
    public double[][]? RfFlowNet             { get; set; }
    public double[][]? PcGradWeights         { get; set; }
    public double[][]? IvaeWeights           { get; set; }
    public double[][]? MuonWeights           { get; set; }
    public double[][]? TttAuxWeights         { get; set; }
    public double[][]? HgnnIncidenceWeights  { get; set; }

    // Batch 20 trainer snapshots
    public double[][]? Mamba2Weights        { get; set; }
    public double[][]? GrpoWeights          { get; set; }
    public double[][]? DoraWeights          { get; set; }
    public double[][]? BitNetWeights        { get; set; }
    public double[][]? BasedWeights         { get; set; }
    public double[][]? VicRegWeights        { get; set; }
    public double[][]? FnoWeights           { get; set; }
    public double[][]? MonarchWeights       { get; set; }
    public double[][]? CoconutWeights       { get; set; }
    public double[][]? LagrangianRlWeights  { get; set; }

    // Batch 21 trainer snapshots
    public double[][]? DeltaNetWeights       { get; set; }
    public double[][]? DiffTransformerWeights { get; set; }
    public double[][]? TitansMemoryWeights   { get; set; }
    public double[][]? GlaGateWeights        { get; set; }
    public double[][]? Hgrn2LayerWeights     { get; set; }
    public double[][]? LinOssStateWeights    { get; set; }
    public double[][]? HedgehogPolyWeights   { get; set; }
    public double[][]? FlashFftConvWeights   { get; set; }
    public double[][]? GmmVaeMixWeights      { get; set; }
    public double[][]? SMoEExpertWeights     { get; set; }

    // ── MLP hidden layer weights (Robustness Round) ──────────────────────────

    /// <summary>
    /// Per-learner MLP hidden layer weights. MlpHiddenWeights[k] has shape [hiddenDim × inputDim]
    /// stored row-major. Null/empty = learner k is linear logistic regression (no hidden layer).
    /// </summary>
    public double[][]? MlpHiddenWeights { get; set; }

    /// <summary>
    /// Per-learner MLP hidden layer biases. MlpHiddenBiases[k] has length hiddenDim.
    /// Null/empty = no hidden layer.
    /// </summary>
    public double[][]? MlpHiddenBiases { get; set; }

    /// <summary>Number of hidden units in the MLP layer. 0 = linear logistic regression.</summary>
    public int MlpHiddenDim { get; set; }

    /// <summary>
    /// Number of learners that were sanitized (had NaN/Inf weights replaced) during
    /// the post-training sanity check. 0 = all learners were numerically healthy.
    /// </summary>
    public int SanitizedLearnerCount { get; set; }

    /// <summary>Conformal coverage target (1−α) used during training. Default 0.90.</summary>
    public double ConformalCoverage { get; set; } = 0.90;

    // ── TCN extended metrics (100/100 improvements) ─────────────────────

    /// <summary>Maximum Calibration Error (worst-bin |conf − acc|). Complements ECE with a worst-case view.</summary>
    public double MaxCalibrationError { get; set; }

    /// <summary>Class-wise ECE for Buy predictions (calibP ≥ 0.5).</summary>
    public double ClasswiseEceBuy { get; set; }

    /// <summary>Class-wise ECE for Sell predictions (calibP &lt; 0.5).</summary>
    public double ClasswiseEceSell { get; set; }

    /// <summary>Std dev of Platt A parameter across walk-forward folds. High variance = unstable raw scores.</summary>
    public double RecalibrationStabilityA { get; set; }

    /// <summary>Std dev of Platt B parameter across walk-forward folds.</summary>
    public double RecalibrationStabilityB { get; set; }

    /// <summary>ECE computed after full isotonic (PAVA) calibration. Measures residual calibration error post-PAVA.</summary>
    public double PostIsotonicEce { get; set; }

    /// <summary>Lag-1 autocorrelation of predicted probabilities on the test set. High values suggest lagged reactions.</summary>
    public double PredictionAutocorrelation { get; set; }

    /// <summary>Quantile summary of |calibP − 0.5| on the test set: [p10, p25, p50, p75, p90]. Measures confidence distribution.</summary>
    public double[] ConfidenceHistogramQuantiles { get; set; } = [];

    /// <summary>Accuracy decay rate per fold across walk-forward CV (linear regression slope). Negative = degrading.</summary>
    public double AccuracyDecayRate { get; set; }

    /// <summary>F1 decay rate per fold across walk-forward CV.</summary>
    public double F1DecayRate { get; set; }

    /// <summary>Monte Carlo permutation test p-value. Fraction of shuffled CVs that matched or exceeded observed accuracy.</summary>
    public double MonteCarloPermPValue { get; set; } = 1.0;

    /// <summary>Prediction Interval Coverage Probability — actual coverage of conformal intervals on test set.</summary>
    public double PicpCoverage { get; set; }

    /// <summary>Per-block max gradient norms recorded during training. Length = numBlocks. Monitors vanishing/exploding gradients.</summary>
    public double[] PerBlockGradientNorms { get; set; } = [];

    /// <summary>Approximate Shapley values per channel (top-k subsets). More accurate than permutation importance for interactions.</summary>
    public double[] ShapleyChannelValues { get; set; } = [];

    /// <summary>Beta calibration parameter c (3-param model). 0.0 = not fitted.</summary>
    public double BetaCalC { get; set; }

    /// <summary>EWC Fisher diagonal used during warm-start training. Stored for next-generation transfer.</summary>
    public double[]? EwcFisherDiagonal { get; set; }

    // ── Per-learner activations (mixed activation ensemble) ──────────────

    /// <summary>
    /// Per-learner activation function index (maps to <see cref="ElmActivation"/>).
    /// When mixed activations are enabled, each learner may use a different activation.
    /// Null/empty = all learners use the same activation (stored in hyperparams).
    /// </summary>
    public int[]? LearnerActivations { get; set; }

    // ── Drift detection statistics ───────────────────────────────────────

    /// <summary>
    /// Mean of ensemble raw probabilities on the calibration set at training time.
    /// Used at inference time to detect distribution shift: if the running mean of
    /// predictions diverges significantly from this value, the model is stale.
    /// </summary>
    public double DriftDetectionMeanProb { get; set; }

    /// <summary>
    /// Standard deviation of ensemble raw probabilities on the calibration set.
    /// Used with <see cref="DriftDetectionMeanProb"/> to compute a z-score at inference.
    /// </summary>
    public double DriftDetectionStdProb { get; set; }

    /// <summary>
    /// Z-score threshold above which inference-time predictions are considered drifted.
    /// Computed as the 95th percentile z-score on the calibration set.
    /// 0.0 = drift detection disabled.
    /// </summary>
    public double DriftScoreThreshold { get; set; }

    /// <summary>
    /// Per-feature means on the calibration set (post-standardisation).
    /// At inference time, compare incoming feature means against these to detect covariate shift.
    /// </summary>
    public double[] DriftDetectionFeatureMeans { get; set; } = [];

    /// <summary>
    /// Per-feature standard deviations on the calibration set (post-standardisation).
    /// </summary>
    public double[] DriftDetectionFeatureStds { get; set; } = [];

    // ── ROCKET improvements ──────────────────────────────────────────────

    /// <summary>ROCKET magnitude regressor weights trained on ROCKET-space features. Length = 2 × numKernels.</summary>
    public double[] RocketMagWeights { get; set; } = [];

    /// <summary>ROCKET magnitude regressor bias (ROCKET-space).</summary>
    public double RocketMagBias { get; set; }

    /// <summary>Magnitude prediction R² (correlation between predicted and actual magnitudes on test set).</summary>
    public double MagnitudeR2 { get; set; }

    /// <summary>Fast weight-based feature attribution (no permutation). Length = featureCount.</summary>
    public float[] FastFeatureAttribution { get; set; } = [];

    /// <summary>Synergistic feature pairs detected via ROCKET kernel co-activation analysis.</summary>
    public string[] SynergisticFeaturePairs { get; set; } = [];

    /// <summary>Per-kernel accuracy contribution (accuracy drop when kernel zeroed). Length = numKernels.</summary>
    public double[] PerKernelAccuracyContribution { get; set; } = [];

    /// <summary>Epoch at which early stopping fired in the main ridge training. 0 = ran all epochs.</summary>
    public int EarlyStoppingEpoch { get; set; }

    /// <summary>Final validation loss at the time early stopping fired.</summary>
    public double FinalValidationLoss { get; set; }

    /// <summary>Venn-ABERS lower/upper probability bounds on calibration set. Interleaved [p_lo, p_hi, ...].</summary>
    public double[] VennAbersCalBounds { get; set; } = [];

    /// <summary>Kernel entropy per test sample (mean Shannon entropy of PPV activations). 0 = not computed.</summary>
    public double MeanKernelEntropy { get; set; }

    // ── Magnitude regressor fold weights (prediction averaging) ──────────

    /// <summary>
    /// Per-fold augmented-space magnitude weights from walk-forward CV.
    /// When non-empty, magnitude prediction averages across all folds rather than
    /// using a single weight-averaged model.
    /// </summary>
    public double[][]? MagAugWeightsFolds { get; set; }

    /// <summary>
    /// Per-fold augmented-space magnitude biases from walk-forward CV.
    /// </summary>
    public double[]? MagAugBiasFolds { get; set; }

    public RocketDriftArtifact? RocketDriftArtifact { get; set; }
    public RocketWarmStartArtifact? RocketWarmStartArtifact { get; set; }
    public RocketAuditArtifact? RocketAuditArtifact { get; set; }
    public double RocketCalibrationResidualMean { get; set; }
    public double RocketCalibrationResidualStd { get; set; }
    public double RocketCalibrationResidualThreshold { get; set; }
}
