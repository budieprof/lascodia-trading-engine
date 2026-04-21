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

    /// <summary>When false, the worker loops but performs no training work.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Consecutive cycle failures per pair before a DataQualityIssue alert is raised.</summary>
    public int ConsecutiveFailAlertThreshold { get; set; } = 3;

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
    /// Encoder architecture the worker will produce. <see cref="CpcEncoderType.Linear"/> is
    /// the default — single-step <c>ReLU(W_e · x)</c>. Switch to <see cref="CpcEncoderType.Tcn"/>
    /// once live data justifies the extra capacity — the TCN captures a ~7-step receptive
    /// field of past context and is measurably slower to train but preserves train/inference
    /// parity the same way the linear encoder does.
    /// </summary>
    public CpcEncoderType EncoderType { get; set; } = CpcEncoderType.Linear;
}
