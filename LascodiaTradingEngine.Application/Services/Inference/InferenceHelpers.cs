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
    /// MetaWeights (stacking) → GES weights → persisted learner-accuracy weights
    /// → CalAccuracy softmax fallback → plain average.
    /// </summary>
    internal static double AggregateProbs(
        double[] probs, int count,
        double[]? metaWeights, double metaBias,
        double[]? gesWeights, double[]? learnerAccuracyWeights, double[]? calAccuracies)
    {
        count = Math.Clamp(count, 0, probs.Length);
        if (count <= 0)
            return 0.5;

        if (metaWeights is { Length: > 0 } mw && mw.Length == count)
        {
            double metaZ = double.IsFinite(metaBias) ? metaBias : 0.0;
            for (int t = 0; t < count; t++)
                metaZ += (double.IsFinite(mw[t]) ? mw[t] : 0.0) * ClampProbability(probs[t]);
            return ClampProbability(MLFeatureHelper.Sigmoid(metaZ));
        }

        if (gesWeights is { Length: > 0 } gw && gw.Length == count)
        {
            double wSum = 0, pSum = 0;
            for (int t = 0; t < count; t++)
            {
                double weight = SanitizeNonNegative(gw[t]);
                wSum += weight;
                pSum += weight * ClampProbability(probs[t]);
            }
            return wSum > 1e-10 ? ClampProbability(pSum / wSum) : AverageFirstCount(probs, count);
        }

        if (learnerAccuracyWeights is { Length: > 0 } law && law.Length == count)
        {
            double wSum = 0, pSum = 0;
            for (int t = 0; t < count; t++)
            {
                double weight = SanitizeNonNegative(law[t]);
                wSum += weight;
                pSum += weight * ClampProbability(probs[t]);
            }
            return wSum > 1e-10 ? ClampProbability(pSum / wSum) : AverageFirstCount(probs, count);
        }

        if (calAccuracies is { Length: > 0 } ca && ca.Length == count)
        {
            const double Alpha = 4.0;
            double[] sanitized = new double[count];
            for (int t = 0; t < count; t++)
                sanitized[t] = ClampProbability(ca[t]);

            double maxAcc = sanitized.Max();
            double sumExp = sanitized.Sum(a => Math.Exp(Alpha * (a - maxAcc)));
            double wSum = 0, pSum = 0;
            for (int t = 0; t < count; t++)
            {
                double w = sumExp > 1e-10 ? Math.Exp(Alpha * (sanitized[t] - maxAcc)) / sumExp : 0.0;
                wSum += w;
                pSum += w * ClampProbability(probs[t]);
            }
            return wSum > 1e-10 ? ClampProbability(pSum / wSum) : AverageFirstCount(probs, count);
        }

        return AverageFirstCount(probs, count);
    }

    /// <summary>
    /// Applies global Platt/temperature scaling and isotonic calibration to a raw probability.
    /// Used by consensus filter and multi-TF blend. The main scoring path applies additional
    /// class-conditional Platt and model age decay on top of this.
    /// </summary>
    internal static double ApplyBasicCalibration(double rawProb, ModelSnapshot snap)
    {
        TcnCalibrationArtifact? tcnCalibration = string.Equals(snap.Type, "TCN", StringComparison.OrdinalIgnoreCase)
            ? TcnSnapshotSupport.ResolveCalibrationArtifact(snap)
            : null;
        double rawLogit = MLFeatureHelper.Logit(ClampLogitProbability(rawProb));
        double temperatureScale = SanitizeTemperatureScale(tcnCalibration?.TemperatureScale ?? snap.TemperatureScale);
        double plattA = SanitizeFiniteOrDefault(tcnCalibration?.GlobalPlattA ?? snap.PlattA, 1.0);
        double plattB = SanitizeFiniteOrDefault(tcnCalibration?.GlobalPlattB ?? snap.PlattB, 0.0);
        double calibP = temperatureScale > 0.0
            ? MLFeatureHelper.Sigmoid(rawLogit / temperatureScale)
            : MLFeatureHelper.Sigmoid(plattA * rawLogit + plattB);

        double[] isotonicBreakpoints = tcnCalibration?.IsotonicBreakpoints ?? snap.IsotonicBreakpoints;
        if (isotonicBreakpoints.Length >= 4)
            calibP = ApplyIsotonicCalibrationSafe(calibP, isotonicBreakpoints);

        return ClampProbability(calibP);
    }

    /// <summary>
    /// Applies the same deployed probability calibration stack as the live scorer:
    /// temperature/global Platt, class-conditional Platt, isotonic, then model-age decay.
    /// </summary>
    internal static double ApplyDeployedCalibration(double rawProb, ModelSnapshot snap)
    {
        TcnCalibrationArtifact? tcnCalibration = string.Equals(snap.Type, "TCN", StringComparison.OrdinalIgnoreCase)
            ? TcnSnapshotSupport.ResolveCalibrationArtifact(snap)
            : null;
        return ApplyDeployedCalibration(
            rawProb,
            tcnCalibration?.GlobalPlattA ?? snap.PlattA,
            tcnCalibration?.GlobalPlattB ?? snap.PlattB,
            tcnCalibration?.TemperatureScale ?? snap.TemperatureScale,
            tcnCalibration?.BuyBranchPlattA ?? snap.PlattABuy,
            tcnCalibration?.BuyBranchPlattB ?? snap.PlattBBuy,
            tcnCalibration?.SellBranchPlattA ?? snap.PlattASell,
            tcnCalibration?.SellBranchPlattB ?? snap.PlattBSell,
            tcnCalibration?.ConditionalRoutingThreshold ?? snap.ConditionalCalibrationRoutingThreshold,
            tcnCalibration?.IsotonicBreakpoints ?? snap.IsotonicBreakpoints,
            snap.AgeDecayLambda,
            snap.TrainedAtUtc,
            applyAgeDecay: true);
    }

    internal static double ApplyDeployedCalibration(
        double rawProb,
        double plattA,
        double plattB,
        double temperatureScale,
        double plattABuy,
        double plattBBuy,
        double plattASell,
        double plattBSell,
        double routingThreshold,
        double[]? isotonicBreakpoints = null,
        double ageDecayLambda = 0.0,
        DateTime trainedAtUtc = default,
        bool applyAgeDecay = false,
        DateTime? nowUtc = null)
    {
        double rawLogit = MLFeatureHelper.Logit(ClampLogitProbability(rawProb));
        double safeTemperatureScale = SanitizeTemperatureScale(temperatureScale);
        double safePlattA = SanitizeFiniteOrDefault(plattA, 1.0);
        double safePlattB = SanitizeFiniteOrDefault(plattB, 0.0);
        double safePlattABuy = SanitizeFiniteOrDefault(plattABuy, 0.0);
        double safePlattBBuy = SanitizeFiniteOrDefault(plattBBuy, 0.0);
        double safePlattASell = SanitizeFiniteOrDefault(plattASell, 0.0);
        double safePlattBSell = SanitizeFiniteOrDefault(plattBSell, 0.0);
        double safeRoutingThreshold = SanitizeFiniteOrDefault(routingThreshold, 0.5);
        double globalCalibP = safeTemperatureScale > 0.0
            ? MLFeatureHelper.Sigmoid(rawLogit / safeTemperatureScale)
            : MLFeatureHelper.Sigmoid(safePlattA * rawLogit + safePlattB);
        double calibP = ApplyConditionalCalibration(
            rawLogit, globalCalibP,
            safePlattABuy, safePlattBBuy,
            safePlattASell, safePlattBSell,
            safeRoutingThreshold);

        if (isotonicBreakpoints is { Length: >= 4 })
            calibP = ApplyIsotonicCalibrationSafe(calibP, isotonicBreakpoints);

        double safeAgeDecayLambda = SanitizeNonNegative(ageDecayLambda);
        if (applyAgeDecay && safeAgeDecayLambda > 0.0 && trainedAtUtc != default)
        {
            DateTime effectiveNowUtc = nowUtc ?? DateTime.UtcNow;
            double daysSinceTrain = (effectiveNowUtc - trainedAtUtc).TotalDays;
            double decayFactor    = Math.Exp(-safeAgeDecayLambda * Math.Max(0.0, daysSinceTrain));
            calibP = 0.5 + (calibP - 0.5) * decayFactor;
        }

        return ClampProbability(calibP);
    }

    internal static bool HasMeaningfulConditionalCalibration(double plattA, double plattB)
    {
        double safeA = SanitizeFiniteOrDefault(plattA, 0.0);
        double safeB = SanitizeFiniteOrDefault(plattB, 0.0);
        if (Math.Abs(safeA) <= 1e-12 && Math.Abs(safeB) <= 1e-12)
            return false;

        return Math.Abs(safeA - 1.0) > 1e-9 || Math.Abs(safeB) > 1e-9;
    }

    internal static double ApplyConditionalCalibration(
        double rawLogit,
        double globalCalibP,
        double plattABuy,
        double plattBBuy,
        double plattASell,
        double plattBSell,
        double routingThreshold = 0.5)
    {
        double effectiveRoutingThreshold = SanitizeFiniteOrDefault(routingThreshold, 0.5);
        effectiveRoutingThreshold = Math.Clamp(effectiveRoutingThreshold, 0.01, 0.99);

        if (globalCalibP >= effectiveRoutingThreshold && HasMeaningfulConditionalCalibration(plattABuy, plattBBuy))
            return ClampProbability(MLFeatureHelper.Sigmoid(plattABuy * rawLogit + plattBBuy));

        if (globalCalibP < effectiveRoutingThreshold && HasMeaningfulConditionalCalibration(plattASell, plattBSell))
            return ClampProbability(MLFeatureHelper.Sigmoid(plattASell * rawLogit + plattBSell));

        return ClampProbability(globalCalibP);
    }

    /// <summary>
    /// Rebuilds any persisted feature-pipeline transforms that live between
    /// standardisation and inference. This keeps the deployed scorer aligned with
    /// the exact preprocessing layout the trainer serialized into the snapshot.
    /// </summary>
    internal static void ApplyModelSpecificFeatureTransforms(float[] features, ModelSnapshot snap)
    {
        FeatureTransformDescriptor[] descriptors = string.Equals(snap.Type, "TABNET", StringComparison.OrdinalIgnoreCase)
            ? TabNetSnapshotSupport.ResolveFeaturePipelineDescriptors(snap)
            : snap.FeaturePipelineDescriptors ?? [];

        foreach (var descriptor in descriptors)
        {
            if (FeaturePipelineTransformSupport.TryApplyInPlace(features, descriptor))
                continue;

            if (!string.Equals(descriptor.Kind, TabNetSnapshotSupport.PolyInteractionsTransform, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(descriptor.Operation, "PRODUCT", StringComparison.OrdinalIgnoreCase))
                continue;

            int rawFeatureCount = descriptor.InputFeatureCount > 0
                ? Math.Min(descriptor.InputFeatureCount, features.Length)
                : Math.Min(snap.TabNetRawFeatureCount > 0 ? snap.TabNetRawFeatureCount : features.Length, features.Length);
            if (rawFeatureCount >= features.Length || descriptor.SourceIndexGroups.Length == 0)
                continue;

            int outputIndex = Math.Max(rawFeatureCount, descriptor.OutputStartIndex);
            for (int g = 0; g < descriptor.SourceIndexGroups.Length && outputIndex < features.Length; g++)
            {
                var group = descriptor.SourceIndexGroups[g];
                if (group.Length == 0)
                    continue;

                float product = 1f;
                bool valid = true;
                for (int i = 0; i < group.Length; i++)
                {
                    int sourceIndex = group[i];
                    if (sourceIndex < 0 || sourceIndex >= rawFeatureCount)
                    {
                        valid = false;
                        break;
                    }
                    product *= features[sourceIndex];
                }

                if (valid)
                    features[outputIndex] = product;
                outputIndex++;
            }
        }

        if (descriptors.Length > 0 || !string.Equals(snap.Type, "TABNET", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (string transform in snap.FeaturePipelineTransforms ?? [])
        {
            if (!string.Equals(transform, TabNetSnapshotSupport.PolyInteractionsTransform, StringComparison.OrdinalIgnoreCase))
                continue;

            if (snap.TabNetPolyTopFeatureIndices is not { Length: > 1 } topIdx)
                continue;

            int rawFeatureCount = snap.TabNetRawFeatureCount > 0
                ? Math.Min(snap.TabNetRawFeatureCount, features.Length)
                : features.Length;
            if (rawFeatureCount >= features.Length)
                continue;

            int k = rawFeatureCount;
            for (int a = 0; a < topIdx.Length && k < features.Length; a++)
            {
                int leftIdx = topIdx[a];
                if (leftIdx < 0 || leftIdx >= rawFeatureCount)
                    continue;

                for (int b = a + 1; b < topIdx.Length && k < features.Length; b++)
                {
                    int rightIdx = topIdx[b];
                    if (rightIdx < 0 || rightIdx >= rawFeatureCount)
                        continue;

                    features[k++] = features[leftIdx] * features[rightIdx];
                }
            }
        }
    }

    private static double AverageFirstCount(double[] probs, int count)
    {
        if (count <= 0)
            return 0.5;

        double sum = 0.0;
        for (int i = 0; i < count; i++)
            sum += ClampProbability(probs[i]);
        return ClampProbability(sum / count);
    }

    private static double ApplyIsotonicCalibrationSafe(double probability, double[] breakpoints)
    {
        double clampedProbability = ClampProbability(probability);
        if (breakpoints.Length < 2)
            return clampedProbability;

        var clean = new List<(double X, double Y)>(breakpoints.Length / 2);
        for (int i = 0; i + 1 < breakpoints.Length; i += 2)
        {
            if (!double.IsFinite(breakpoints[i]) || !double.IsFinite(breakpoints[i + 1]))
                continue;

            double x = ClampProbability(breakpoints[i]);
            double y = ClampProbability(breakpoints[i + 1]);
            if (clean.Count > 0)
            {
                var last = clean[^1];
                if (x < last.X)
                    continue;

                if (Math.Abs(x - last.X) <= 1e-12)
                {
                    clean[^1] = (x, y);
                    continue;
                }
            }

            clean.Add((x, y));
        }

        if (clean.Count == 0)
            return clampedProbability;
        if (clean.Count == 1)
            return clean[0].Y;
        if (clampedProbability <= clean[0].X)
            return clean[0].Y;

        for (int i = 0; i < clean.Count - 1; i++)
        {
            var (x0, y0) = clean[i];
            var (x1, y1) = clean[i + 1];
            if (clampedProbability > x1)
                continue;

            double t = (x1 - x0) > 1e-10 ? (clampedProbability - x0) / (x1 - x0) : 0.5;
            return ClampProbability(y0 + t * (y1 - y0));
        }

        return clean[^1].Y;
    }

    private static double ClampProbability(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 0.0, 1.0);
    }

    private static double ClampLogitProbability(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 1e-7, 1.0 - 1e-7);
    }

    private static double SanitizeNonNegative(double value)
    {
        if (!double.IsFinite(value) || value < 0.0)
            return 0.0;

        return value;
    }

    private static double SanitizeFiniteOrDefault(double value, double fallback)
    {
        return double.IsFinite(value) ? value : fallback;
    }

    private static double SanitizeTemperatureScale(double value)
    {
        return double.IsFinite(value) && value > 0.0 && value < 10.0 ? value : 0.0;
    }
}
