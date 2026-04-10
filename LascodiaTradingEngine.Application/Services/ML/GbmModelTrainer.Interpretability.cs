using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  TREESHAP, PARTIAL DEPENDENCE (Items 31, 32)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 31: TreeSHAP baseline = mean prediction over training set.</summary>
    private static double ComputeTreeShapBaseline(List<GbmTree> trees, double baseLogOdds, double lr,
        List<TrainingSample> trainSet, int featureCount)
    {
        if (trainSet.Count == 0) return 0;
        double sum = 0;
        foreach (var s in trainSet) sum += GbmProb(s.Features, trees, baseLogOdds, lr, featureCount);
        return sum / trainSet.Count;
    }

    /// <summary>Item 32: Partial dependence for top features (marginal response curves).</summary>
    private static double[][] ComputePartialDependence(
        List<TrainingSample> trainSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, int[] topFeatureIndices, int gridPoints = 20)
    {
        if (trainSet.Count < 10 || topFeatureIndices.Length == 0) return [];
        int subsample = Math.Min(trainSet.Count, 200);
        var result = new double[topFeatureIndices.Length][];

        for (int fi = 0; fi < topFeatureIndices.Length; fi++)
        {
            int fIdx = topFeatureIndices[fi];
            if (fIdx >= featureCount) continue;

            // Get feature range
            float fmin = float.MaxValue, fmax = float.MinValue;
            for (int i = 0; i < subsample; i++)
            {
                float v = trainSet[i].Features[fIdx];
                if (v < fmin) fmin = v; if (v > fmax) fmax = v;
            }

            var pdp = new double[gridPoints * 2]; // [gridValue, avgPred, ...]
            float step = (fmax - fmin) / (gridPoints - 1);

            for (int g = 0; g < gridPoints; g++)
            {
                float gridVal = fmin + g * step;
                double avgPred = 0;
                var scratch = new float[trainSet[0].Features.Length];
                for (int i = 0; i < subsample; i++)
                {
                    Array.Copy(trainSet[i].Features, scratch, scratch.Length);
                    scratch[fIdx] = gridVal;
                    avgPred += GbmProb(scratch, trees, baseLogOdds, lr, featureCount);
                }
                pdp[g * 2] = gridVal;
                pdp[g * 2 + 1] = avgPred / subsample;
            }
            result[fi] = pdp;
        }
        return result;
    }
}
