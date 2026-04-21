using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLCpcOptions"/> at startup.</summary>
public class MLCpcOptionsValidator : IValidateOptions<MLCpcOptions>
{
    public ValidateOptionsResult Validate(string? name, MLCpcOptions o)
    {
        var errors = new List<string>();

        if (o.PollIntervalSeconds is < 300 or > 86_400)
            errors.Add("MLCpcOptions.PollIntervalSeconds must be in [300, 86400].");
        if (o.RetrainIntervalHours < 1)
            errors.Add("MLCpcOptions.RetrainIntervalHours must be >= 1.");
        if (o.MaxPairsPerCycle < 1)
            errors.Add("MLCpcOptions.MaxPairsPerCycle must be >= 1.");
        if (o.EmbeddingDim is < 4 or > 128)
            errors.Add("MLCpcOptions.EmbeddingDim must be in [4, 128].");
        if (o.PredictionSteps is < 1 or > 10)
            errors.Add("MLCpcOptions.PredictionSteps must be in [1, 10].");
        if (o.SequenceLength < o.PredictionSteps + 2)
            errors.Add("MLCpcOptions.SequenceLength must exceed PredictionSteps by at least 2.");
        if (o.SequenceStride < 1)
            errors.Add("MLCpcOptions.SequenceStride must be >= 1.");
        if (o.MaxSequences < 1)
            errors.Add("MLCpcOptions.MaxSequences must be >= 1.");
        if (o.TrainingCandles < o.MinCandles)
            errors.Add("MLCpcOptions.TrainingCandles must be >= MinCandles.");
        if (o.MinCandles < o.SequenceLength)
            errors.Add("MLCpcOptions.MinCandles must be >= SequenceLength.");
        if (!double.IsFinite(o.MinImprovement) || o.MinImprovement is < 0.0 or > 0.5)
            errors.Add("MLCpcOptions.MinImprovement must be a finite value in [0.0, 0.5].");
        if (!double.IsFinite(o.MaxAcceptableLoss) || o.MaxAcceptableLoss <= 0.0)
            errors.Add("MLCpcOptions.MaxAcceptableLoss must be a finite value > 0.");
        if (!double.IsFinite(o.ValidationSplit) || o.ValidationSplit is < 0.05 or > 0.5)
            errors.Add("MLCpcOptions.ValidationSplit must be a finite value in [0.05, 0.5].");
        if (o.MinValidationSequences < 1)
            errors.Add("MLCpcOptions.MinValidationSequences must be >= 1.");
        if (!double.IsFinite(o.MaxValidationLoss) || o.MaxValidationLoss <= 0.0)
            errors.Add("MLCpcOptions.MaxValidationLoss must be a finite value > 0.");
        if (o.ConsecutiveFailAlertThreshold < 1)
            errors.Add("MLCpcOptions.ConsecutiveFailAlertThreshold must be >= 1.");
        if (o.MinCandlesPerRegime < o.SequenceLength)
            errors.Add("MLCpcOptions.MinCandlesPerRegime must be >= SequenceLength.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
