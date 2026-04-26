using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

/// <summary>
/// Invariant tests for <see cref="CpcPretrainerWorker.SplitSequences"/>. Parameterised rather
/// than property-based (no FsCheck in the test csproj) — but the InlineData matrix covers the
/// same edge cases: zero-count, single-count, small validation splits, saturation, and the
/// interaction between MinValidationSequences and ValidationSplit.
/// </summary>
public class CpcPretrainerWorkerSplitSequencesTest
{
    [Theory]
    [InlineData(0,   0.20, 20, 0, 0)]
    [InlineData(1,   0.20, 20, 1, 0)]
    [InlineData(2,   0.20, 20, 1, 1)]
    [InlineData(10,  0.20, 20, 1, 9)]   // MinValidation dominates; clamped to count-1
    [InlineData(25,  0.20, 5,  20, 5)]
    [InlineData(25,  0.20, 10, 15, 10)] // ceil(25*0.2)=5, max with MinValidation=10
    [InlineData(100, 0.10, 5,  90, 10)]
    [InlineData(100, 0.50, 1,  50, 50)]
    [InlineData(100, 0.25, 1,  75, 25)]
    [InlineData(1000, 0.05, 1, 950, 50)]
    public void SplitSequences_MaintainsInvariants(int sequenceCount, double validationSplit, int minValidation, int expectedTrain, int expectedValidation)
    {
        var sequences = Enumerable.Range(0, sequenceCount)
            .Select(i => new[] { new[] { (float)i } })
            .ToArray();
        var config = BuildConfig(validationSplit, minValidation);

        var split = CpcPretrainerWorker.SplitSequences(sequences, config);

        Assert.Equal(expectedTrain, split.Training.Count);
        Assert.Equal(expectedValidation, split.Validation.Count);
        Assert.Equal(sequenceCount, split.Training.Count + split.Validation.Count);

        // Training precedes validation in time-ordered chronological splits.
        if (split.Training.Count > 0 && split.Validation.Count > 0)
        {
            var lastTrainMarker = split.Training[^1][0][0];
            var firstValidationMarker = split.Validation[0][0][0];
            Assert.True(lastTrainMarker < firstValidationMarker);
        }
    }

    private static MLCpcRuntimeConfig BuildConfig(double validationSplit, int minValidation)
    {
        var options = new MLCpcOptions
        {
            ValidationSplit = validationSplit,
            MinValidationSequences = minValidation,
        };
        // Only ValidationSplit and MinValidationSequences participate in SplitSequences logic,
        // so the remaining fields carry defaults from MLCpcOptions.
        return new MLCpcRuntimeConfig(
            PollSeconds: options.PollIntervalSeconds,
            RetrainIntervalHours: options.RetrainIntervalHours,
            MaxPairsPerCycle: options.MaxPairsPerCycle,
            EmbeddingDim: options.EmbeddingDim,
            PredictionSteps: options.PredictionSteps,
            SequenceLength: options.SequenceLength,
            SequenceStride: options.SequenceStride,
            MaxSequences: options.MaxSequences,
            TrainingCandles: options.TrainingCandles,
            MinCandles: options.MinCandles,
            MinImprovement: options.MinImprovement,
            MaxAcceptableLoss: options.MaxAcceptableLoss,
            ValidationSplit: options.ValidationSplit,
            MinValidationSequences: options.MinValidationSequences,
            MaxValidationLoss: options.MaxValidationLoss,
            MinValidationEmbeddingL2Norm: options.MinValidationEmbeddingL2Norm,
            MinValidationEmbeddingVariance: options.MinValidationEmbeddingVariance,
            EnableDownstreamProbeGate: options.EnableDownstreamProbeGate,
            MinDownstreamProbeSamples: options.MinDownstreamProbeSamples,
            MinDownstreamProbeBalancedAccuracy: options.MinDownstreamProbeBalancedAccuracy,
            MinDownstreamProbeImprovement: options.MinDownstreamProbeImprovement,
            StaleEncoderAlertHours: options.StaleEncoderAlertHours,
            Enabled: options.Enabled,
            SystemicPauseActive: false,
            ConsecutiveFailAlertThreshold: options.ConsecutiveFailAlertThreshold,
            LockTimeoutSeconds: options.LockTimeoutSeconds,
            TrainPerRegime: options.TrainPerRegime,
            MinCandlesPerRegime: options.MinCandlesPerRegime,
            RegimeCandleBackfillMultiplier: options.RegimeCandleBackfillMultiplier,
            EncoderType: options.EncoderType,
            EnableRepresentationDriftGate: options.EnableRepresentationDriftGate,
            MinCentroidCosineDistance: options.MinCentroidCosineDistance,
            MaxRepresentationMeanPsi: options.MaxRepresentationMeanPsi,
            EnableArchitectureSwitchGate: options.EnableArchitectureSwitchGate,
            MaxArchitectureSwitchAccuracyRegression: options.MaxArchitectureSwitchAccuracyRegression,
            EnableAdversarialValidationGate: options.EnableAdversarialValidationGate,
            MaxAdversarialValidationAuc: options.MaxAdversarialValidationAuc,
            MinAdversarialValidationSamples: options.MinAdversarialValidationSamples,
            ConfigurationDriftAlertCycles: options.ConfigurationDriftAlertCycles,
            SystemicPauseAlertHours: options.SystemicPauseAlertHours,
            PollJitterSeconds: options.PollJitterSeconds,
            FailureBackoffCapShift: options.FailureBackoffCapShift,
            UseCycleLock: options.UseCycleLock,
            CycleLockTimeoutSeconds: options.CycleLockTimeoutSeconds,
            FleetSystemicConsecutiveZeroPromotionCycles: options.FleetSystemicConsecutiveZeroPromotionCycles,
            OverridesEnabled: options.OverridesEnabled);
    }
}
