using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Abstraction over the self-supervised pre-training phase that learns a compact
/// embedding of the candle feature space before supervised fine-tuning.
/// </summary>
/// <remarks>
/// The masked candle autoencoder randomly masks 20% of input feature slots and
/// reconstructs them via a bottleneck encoder-decoder. The encoder weights are
/// then serialised as a <see cref="PretrainingSnapshot"/> and used to warm-start
/// the supervised learner in the next training run.
///
/// Pre-training is triggered by <c>MLTrainingWorker</c> when
/// <see cref="MLTrainingRun.TotalSamples"/> is below the configured
/// <c>MinSamplesForDirectSupervisedTraining</c> threshold, or when
/// <c>HyperparamOverrides.IsPretrainingRun</c> is <c>true</c>.
/// </remarks>
public interface ISelfSupervisedPretrainer
{
    /// <summary>
    /// Runs the masked-autoencoder pre-training loop on the supplied unlabelled samples.
    /// Returns a snapshot of the encoder weights ready to warm-start a supervised run.
    /// </summary>
    /// <param name="samples">
    /// Unlabelled feature vectors. Only <see cref="TrainingSample.Features"/> are used;
    /// direction labels are ignored.
    /// </param>
    /// <param name="hp">Training hyperparameters for epoch count, learning rate, etc.</param>
    /// <param name="maskFraction">
    /// Fraction of features to mask per sample during reconstruction training.
    /// Default 0.20 (20%). Must be in (0, 1).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="PretrainingSnapshot"/> containing the encoder weight matrix,
    /// reconstruction loss curve, and feature importance rankings from the decoder.
    /// </returns>
    Task<PretrainingSnapshot> PretrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        double               maskFraction = 0.20,
        CancellationToken    ct           = default);
}

/// <summary>
/// Encoder weights produced by <see cref="ISelfSupervisedPretrainer"/> and
/// used to warm-start a subsequent supervised training run.
/// </summary>
/// <param name="EncoderWeights">
/// 2-D weight matrix [hiddenDim × featureCount] mapping raw features to latent space.
/// </param>
/// <param name="HiddenDim">Number of latent dimensions in the encoder bottleneck.</param>
/// <param name="ReconstructionLoss">Final masked-reconstruction MSE on the training set.</param>
/// <param name="FeatureImportanceByReconstruction">
/// Per-feature reconstruction difficulty (average MSE when that feature is masked).
/// Higher = harder to reconstruct = more informative feature.
/// </param>
/// <param name="TrainedAt">UTC timestamp when pre-training completed.</param>
public record PretrainingSnapshot(
    float[][]  EncoderWeights,
    int        HiddenDim,
    double     ReconstructionLoss,
    float[]    FeatureImportanceByReconstruction,
    DateTime   TrainedAt);
