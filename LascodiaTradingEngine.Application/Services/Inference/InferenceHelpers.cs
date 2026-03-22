using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Shared pure-function helpers used by inference engines and
/// the scoring pipeline (consensus filter, multi-TF blend).
/// </summary>
internal static class InferenceHelpers
{
    /// <summary>
    /// Aggregates per-learner probabilities using the priority chain:
    /// MetaWeights (stacking) → GES weights → CalAccuracy softmax → plain average.
    /// </summary>
    internal static double AggregateProbs(
        double[] probs, int count,
        double[]? metaWeights, double metaBias,
        double[]? gesWeights, double[]? calAccuracies)
    {
        if (metaWeights is { Length: > 0 } mw && mw.Length == count)
        {
            double metaZ = metaBias;
            for (int t = 0; t < count; t++) metaZ += mw[t] * probs[t];
            return MLFeatureHelper.Sigmoid(metaZ);
        }

        if (gesWeights is { Length: > 0 } gw && gw.Length == count)
        {
            double wSum = 0, pSum = 0;
            for (int t = 0; t < count; t++) { wSum += gw[t]; pSum += gw[t] * probs[t]; }
            return wSum > 1e-10 ? pSum / wSum : probs.Average();
        }

        if (calAccuracies is { Length: > 0 } ca && ca.Length == count)
        {
            const double Alpha = 4.0;
            double maxAcc = ca.Max();
            double sumExp = ca.Sum(a => Math.Exp(Alpha * (a - maxAcc)));
            double wSum = 0, pSum = 0;
            for (int t = 0; t < count; t++)
            {
                double w = Math.Exp(Alpha * (ca[t] - maxAcc)) / sumExp;
                wSum += w; pSum += w * probs[t];
            }
            return wSum > 1e-10 ? pSum / wSum : probs.Average();
        }

        return probs.Average();
    }

    /// <summary>
    /// Applies global Platt/temperature scaling and isotonic calibration to a raw probability.
    /// Used by consensus filter and multi-TF blend. The main scoring path applies additional
    /// class-conditional Platt and model age decay on top of this.
    /// </summary>
    internal static double ApplyBasicCalibration(double rawProb, ModelSnapshot snap)
    {
        double rawLogit = MLFeatureHelper.Logit(rawProb);
        double calibP = snap.TemperatureScale > 0.0 && snap.TemperatureScale < 10.0
            ? MLFeatureHelper.Sigmoid(rawLogit / snap.TemperatureScale)
            : MLFeatureHelper.Sigmoid(snap.PlattA * rawLogit + snap.PlattB);

        if (snap.IsotonicBreakpoints.Length >= 4)
            calibP = BaggedLogisticTrainer.ApplyIsotonicCalibration(calibP, snap.IsotonicBreakpoints);

        return calibP;
    }
}
