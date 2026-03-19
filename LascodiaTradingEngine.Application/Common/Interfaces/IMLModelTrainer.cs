using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Abstraction over the ML training algorithm. Implementations provide the
/// actual model fitting, cross-validation, and evaluation logic.
/// </summary>
public interface IMLModelTrainer
{
    /// <summary>
    /// Trains a model from the supplied labelled samples using the given hyperparameters.
    /// </summary>
    /// <param name="samples">Feature vectors with direction labels (1 = Buy, -1 = Sell) and magnitude.</param>
    /// <param name="hp">Training hyperparameters loaded from EngineConfig.</param>
    /// <param name="ct">Cancellation token — implementations must honour this promptly.</param>
    /// <returns>
    /// A <see cref="TrainingResult"/> containing final out-of-sample metrics,
    /// walk-forward CV summary, and the serialised model bytes ready for storage.
    /// </returns>
    /// <param name="warmStart">
    /// Optional snapshot from the previous model whose weights are used to initialise the
    /// ensemble instead of zeros. Enables fast incremental updates for
    /// <c>AutoDegrading</c>-triggered runs — convergence typically needs half the epochs.
    /// <c>null</c> forces a full cold-start training run.
    /// </param>
    Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default);
}
