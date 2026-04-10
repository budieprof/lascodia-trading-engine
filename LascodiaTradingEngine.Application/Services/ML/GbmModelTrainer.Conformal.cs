using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    /// <summary>Item 8: Conformal q-hats in probability space for Buy/Sell prediction sets.</summary>
    private static (double Overall, double Buy, double Sell) ComputeConformalQHats(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates, double alpha)
    {
        if (calSet.Count < 10) return (0.5, 0.5, 0.5);
        var scores = new List<double>(calSet.Count);
        var buyScores = new List<double>(calSet.Count);
        var sellScores = new List<double>(calSet.Count);
        for (int i = 0; i < calSet.Count; i++)
        {
            double calibP = GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double score = calSet[i].Direction > 0 ? 1.0 - calibP : calibP;
            score = Math.Clamp(score, 0.0, 1.0);
            scores.Add(score);
            if (calSet[i].Direction > 0) buyScores.Add(score); else sellScores.Add(score);
        }

        static double Quantile(List<double> values, double alphaValue)
        {
            if (values.Count == 0) return 0.5;
            values.Sort();
            int qIdx = (int)Math.Ceiling((1.0 - alphaValue) * (values.Count + 1)) - 1;
            qIdx = Math.Clamp(qIdx, 0, values.Count - 1);
            return Math.Clamp(values[qIdx], 1e-6, 1.0 - 1e-6);
        }

        double overall = Quantile(scores, alpha);
        double buy = buyScores.Count > 0 ? Quantile(buyScores, alpha) : overall;
        double sell = sellScores.Count > 0 ? Quantile(sellScores, alpha) : overall;
        return (overall, buy, sell);
    }

    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet, List<GbmTree> trees, List<HashSet<int>> bagMasks,
        double baseLogOdds, double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (trainSet.Count < 10 || trees.Count < 2 || bagMasks.Count != trees.Count) return [];
        var residuals = new List<double>(trainSet.Count);
        for (int i = 0; i < trainSet.Count; i++)
        {
            double oobScore = baseLogOdds; int oobTreeCount = 0;
            for (int t = 0; t < trees.Count; t++)
            {
                if (bagMasks[t].Contains(i)) continue;
                oobScore += GetTreeLearningRate(t, lr, perTreeLearningRates) * Predict(trees[t], trainSet[i].Features);
                oobTreeCount++;
            }
            if (oobTreeCount == 0) continue;
            // Keep jackknife residuals in raw-probability space for the same reason as OOB accuracy:
            // subset-tree predictions should not pass through full-ensemble calibration parameters.
            double oobP = Math.Clamp(Sigmoid(oobScore), 1e-7, 1.0 - 1e-7);
            double y = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(y - oobP));
        }
        residuals.Sort();
        return [..residuals];
    }

    /// <summary>Item 9: Validate Jackknife+ empirical coverage on calibration set.</summary>
    private static double ValidateJackknifeCoverage(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double[] jackknifeResiduals, double alpha)
    {
        if (calSet.Count < 10 || jackknifeResiduals.Length < 5) return 0;
        int qIdx = (int)Math.Ceiling((1.0 - alpha) * jackknifeResiduals.Length) - 1;
        qIdx = Math.Clamp(qIdx, 0, jackknifeResiduals.Length - 1);
        double qHat = jackknifeResiduals[qIdx];

        int covered = 0;
        foreach (var s in calSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double y = s.Direction > 0 ? 1.0 : 0.0;
            if (Math.Abs(y - p) <= qHat) covered++;
        }
        return (double)covered / calSet.Count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  APPROXIMATE VENN-ABERS BOUNDS (Item 7)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[][] ComputeVennAbers(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count < 10)
            return [];

        // Persist approximate Venn-Abers bounds for diagnostics. This is not used by the
        // live scorer, so we keep the artifact explicit rather than implying exact Venn-Abers.
        var rawProbs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            rawProbs[i] = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);

        return TcnModelTrainer.FitVennAbers(calSet, rawProbs);
    }
}
