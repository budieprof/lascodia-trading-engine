namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Identifies the neural / statistical architecture used to train an <see cref="Entities.MLModel"/>.
/// Stored on <c>MLModel</c> and <c>MLTrainingRun</c> so the scorer can deserialise the
/// correct model format and the operator can query which architecture is in production.
/// Used as keyed-service keys in DI — every <see cref="Common.Interfaces.IMLModelTrainer"/>
/// registration is keyed by the corresponding enum value.
/// </summary>
/// <remarks>
/// Only production-grade (A+) architectures are included. Each covers a distinct
/// model family or capability:
/// <list type="bullet">
///   <item><see cref="BaggedLogistic"/> — default bagged ensemble (fastest, safest fallback).</item>
///   <item><see cref="TemporalConvNet"/> — causal dilated 1-D convolutions over sequence data.</item>
///   <item><see cref="Gbm"/> — gradient-boosted decision trees for non-linear feature interactions.</item>
///   <item><see cref="Elm"/> — extreme learning machine with analytic solve (fastest training).</item>
///   <item><see cref="AdaBoost"/> — adaptive boosting with hard-example focus.</item>
///   <item><see cref="Rocket"/> — random convolutional kernels for temporal pattern extraction.</item>
///   <item><see cref="TabNet"/> — attention-based tabular feature selection.</item>
///   <item><see cref="FtTransformer"/> — feature-tokenizer transformer for heterogeneous inputs.</item>
///   <item><see cref="Smote"/> — SMOTE oversampling for class-imbalanced regimes.</item>
///   <item><see cref="QuantileRf"/> — quantile random forest for prediction intervals.</item>
///   <item><see cref="Svgp"/> — sparse variational Gaussian process for epistemic uncertainty.</item>
///   <item><see cref="Dann"/> — domain-adversarial neural network for regime-shift robustness.</item>
/// </list>
/// </remarks>
public enum LearnerArchitecture
{
    /// <summary>Bagged logistic-regression ensemble (default, lowest latency).</summary>
    BaggedLogistic = 0,

    /// <summary>Temporal Convolutional Network — 1-D dilated causal convolutions over the lookback window.</summary>
    TemporalConvNet = 1,

    /// <summary>Gradient Boosting Machine weak-learner trainer.</summary>
    Gbm = 3,

    /// <summary>AdaBoost adaptive boosting ensemble trainer.</summary>
    AdaBoost = 6,

    /// <summary>Sparse Variational Gaussian Process (SVGP) uncertainty trainer.</summary>
    Svgp = 30,

    /// <summary>Extreme Learning Machine (ELM) single-hidden-layer analytic-solve trainer.</summary>
    Elm = 32,

    /// <summary>Domain-Adversarial Neural Network (DANN) regime-shift adaptation trainer.</summary>
    Dann = 69,

    /// <summary>ROCKET random convolutional kernel transform trainer.</summary>
    Rocket = 74,

    /// <summary>TabNet attention-based tabular trainer.</summary>
    TabNet = 75,

    /// <summary>Feature Tokenizer + Transformer (FT-Transformer) tabular trainer.</summary>
    FtTransformer = 76,

    /// <summary>SMOTE synthetic minority over-sampling trainer.</summary>
    Smote = 81,

    /// <summary>Quantile Regression Random Forest trainer.</summary>
    QuantileRf = 90,
}
