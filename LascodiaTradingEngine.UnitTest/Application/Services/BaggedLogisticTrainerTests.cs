using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class BaggedLogisticTrainerTests
{
    [Fact]
    public void ValidateTrainingSamples_Throws_For_Empty_Input()
    {
        var samples = new List<TrainingSample>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaggedLogisticTrainer.ValidateTrainingSamples(samples));

        Assert.Contains("no training samples", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTrainingSamples_Throws_For_Zero_Features()
    {
        var samples = new List<TrainingSample>
        {
            new([], 1, 1f),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaggedLogisticTrainer.ValidateTrainingSamples(samples));

        Assert.Contains("at least one feature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTrainingSamples_Throws_For_Invalid_Direction_Label()
    {
        var samples = new List<TrainingSample>
        {
            new([1f], 2, 1f),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaggedLogisticTrainer.ValidateTrainingSamples(samples));

        Assert.Contains("invalid direction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTrainingSamples_Throws_For_NonFinite_Feature()
    {
        var samples = new List<TrainingSample>
        {
            new([float.NaN], 1, 1f),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaggedLogisticTrainer.ValidateTrainingSamples(samples));

        Assert.Contains("non-finite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTrainingSamples_Throws_For_NonFinite_Magnitude()
    {
        var samples = new List<TrainingSample>
        {
            new([1f], 1, float.PositiveInfinity),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BaggedLogisticTrainer.ValidateTrainingSamples(samples));

        Assert.Contains("non-finite magnitude", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeStandardizationStats_Uses_Only_Fit_Samples()
    {
        var fitSamples = new List<TrainingSample>
        {
            new([1f, 10f], 1, 1f),
            new([1f, 20f], 0, 1f),
            new([1f, 30f], 1, 1f),
        };

        var (means, stds) = BaggedLogisticTrainer.ComputeStandardizationStats(fitSamples);

        Assert.Equal(1f, means[0], precision: 6);
        Assert.Equal(20f, means[1], precision: 6);
        Assert.True(stds[1] > 0f);
    }

    [Fact]
    public void ComputeFinalSplitBoundaries_Allocates_LeakageSafe_Gaps_Between_Splits()
    {
        var (trainStdEnd, calStart, calEnd, testStart) =
            BaggedLogisticTrainer.ComputeFinalSplitBoundaries(
                sampleCount: 180,
                embargo: 5,
                purgeHorizonBars: 0,
                lookbackWindow: 30);

        Assert.Equal(78, trainStdEnd);
        Assert.Equal(112, calStart);
        Assert.Equal(123, calEnd);
        Assert.Equal(157, testStart);
    }

    [Fact]
    public void ComputeDensityRatioRecentCount_Uses_Configured_BarsPerDay()
    {
        int recentCount = BaggedLogisticTrainer.ComputeDensityRatioRecentCount(
            sampleCount: 1000,
            recentWindowDays: 2,
            barsPerDay: 96);

        Assert.Equal(192, recentCount);
    }

    [Fact]
    public void ComputeIncrementalRecentSampleCount_Uses_Configured_BarsPerDay()
    {
        int recentCount = BaggedLogisticTrainer.ComputeIncrementalRecentSampleCount(
            sampleCount: 500,
            recentWindowDays: 2,
            barsPerDay: 96);

        Assert.Equal(192, recentCount);
    }

    [Theory]
    [InlineData(1, 0.80, 0.40)]
    [InlineData(-1, 0.80, 1.60)]
    [InlineData(-1, 0.50, 1.00)]
    public void ComputeAsymmetricErrorWeight_Uses_Project_Label_Encoding(
        int direction,
        double fpCostWeight,
        double expected)
    {
        double actual = BaggedLogisticTrainer.ComputeAsymmetricErrorWeight(direction, fpCostWeight);

        Assert.Equal(expected, actual, precision: 6);
    }

    [Fact]
    public void ComputeEnsembleValidationPlan_Disables_Holdout_For_Small_Sets()
    {
        var plan = BaggedLogisticTrainer.ComputeEnsembleValidationPlan(sampleCount: 20);

        Assert.False(plan.UseValidationHoldout);
        Assert.Equal(0, plan.ValSize);
    }

    [Fact]
    public void TryCopyWarmStartMlpHiddenWeights_Remaps_By_Feature_Index()
    {
        double[] source = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0];
        double[] destination = new double[source.Length];
        int[] oldSubset = [0, 2, 4];
        int[] newSubset = [4, 2, 0];

        bool copied = BaggedLogisticTrainer.TryCopyWarmStartMlpHiddenWeights(
            source, destination, hiddenDim: 2, oldSubset, newSubset);

        Assert.True(copied);
        Assert.Equal([30.0, 20.0, 10.0, 60.0, 50.0, 40.0], destination);
    }

    [Fact]
    public void TryCopyWarmStartMlpHiddenBiases_Rejects_Length_Mismatch()
    {
        bool copied = BaggedLogisticTrainer.TryCopyWarmStartMlpHiddenBiases(
            source: [1.0, 2.0],
            destination: new double[3]);

        Assert.False(copied);
    }

    [Fact]
    public void SanitizeLearners_Zeroes_Output_And_Hidden_Parameters_When_NonFinite()
    {
        double[][] weights = [[1.0, double.NaN], [2.0, 3.0]];
        double[] biases = [1.0, 2.0];
        double[][] hiddenW = [[1.0, 2.0], [3.0, 4.0]];
        double[][] hiddenB = [[1.0, 2.0], [3.0, 4.0]];

        int sanitized = BaggedLogisticTrainer.SanitizeLearners(weights, biases, hiddenW, hiddenB);

        Assert.Equal(1, sanitized);
        Assert.Equal([0.0, 0.0], weights[0]);
        Assert.Equal(0.0, biases[0]);
        Assert.Equal([0.0, 0.0], hiddenW[0]);
        Assert.Equal([0.0, 0.0], hiddenB[0]);
        Assert.Equal([2.0, 3.0], weights[1]);
        Assert.Equal(2.0, biases[1]);
    }

    [Fact]
    public void ProjectLearnerToFeatureSpace_Maps_Mlp_Weights_Back_To_Raw_Features()
    {
        double[][] weights = [[2.0, 3.0]];
        double[][] hiddenW = [[5.0, 7.0, 11.0, 13.0]];
        int[][] subsets = [[1, 3]];

        var projection = BaggedLogisticTrainer.ProjectLearnerToFeatureSpace(
            learnerIndex: 0,
            weights,
            featureCount: 4,
            subsets,
            hiddenW,
            mlpHiddenDim: 2);

        Assert.Equal(0.0, projection[0], precision: 6);
        Assert.Equal(43.0, projection[1], precision: 6);
        Assert.Equal(0.0, projection[2], precision: 6);
        Assert.Equal(53.0, projection[3], precision: 6);
    }

    [Fact]
    public void ProjectLearnerToFeatureSpace_Distributes_Polynomial_Features_To_Source_Inputs()
    {
        double[][] weights = [[0.0, 0.0, 0.0, 0.0, 0.0, 6.0]];

        var projection = BaggedLogisticTrainer.ProjectLearnerToFeatureSpace(
            learnerIndex: 0,
            weights,
            featureCount: 5);

        Assert.Equal(3.0, projection[0], precision: 6);
        Assert.Equal(3.0, projection[1], precision: 6);
        Assert.Equal(0.0, projection[2], precision: 6);
        Assert.Equal(0.0, projection[3], precision: 6);
        Assert.Equal(0.0, projection[4], precision: 6);
    }

    [Fact]
    public void ProjectLearnerToFeatureSpace_Skips_Unmapped_Mlp_Columns_When_Subset_Is_Truncated()
    {
        double[][] weights = [[10.0]];
        double[][] hiddenW = [[2.0, 100.0]];
        int[][] subsets = [[0]];

        var projection = BaggedLogisticTrainer.ProjectLearnerToFeatureSpace(
            learnerIndex: 0,
            weights,
            featureCount: 2,
            subsets,
            hiddenW,
            mlpHiddenDim: 1);

        Assert.Equal(20.0, projection[0], precision: 6);
        Assert.Equal(0.0, projection[1], precision: 6);
    }

    [Fact]
    public void CopyRawFeatureWindow_Clears_Stale_Tail_Entries()
    {
        double[] destination = [0.0, 0.0, 9.0, 8.0, 7.0, 6.0, 5.0];

        BaggedLogisticTrainer.CopyRawFeatureWindow(
            destination,
            source: [1f, 2f],
            destinationOffset: 2,
            maxRawFeatures: 5);

        Assert.Equal([0.0, 0.0, 1.0, 2.0, 0.0, 0.0, 0.0], destination);
    }

    [Fact]
    public void GetFeatureDisplayName_Falls_Back_For_Out_Of_Range_Index()
    {
        string name = BaggedLogisticTrainer.GetFeatureDisplayName(999);

        Assert.Equal("Feature999", name);
    }

    [Fact]
    public void BuildFeatureNames_Uses_Fallback_Names_Beyond_Known_Metadata()
    {
        string[] names = BaggedLogisticTrainer.BuildFeatureNames(MLFeatureHelper.FeatureNames.Length + 2);

        Assert.Equal(MLFeatureHelper.FeatureNames.Length + 2, names.Length);
        Assert.Equal($"Feature{MLFeatureHelper.FeatureNames.Length}", names[^2]);
        Assert.Equal($"Feature{MLFeatureHelper.FeatureNames.Length + 1}", names[^1]);
    }

    [Fact]
    public void ComputeMlpHiddenBackpropSignals_Uses_Frozen_Output_Weights_And_Relu_Gate()
    {
        var signals = BaggedLogisticTrainer.ComputeMlpHiddenBackpropSignals(
            totalErr: 2.0,
            outputWeights: [3.0, 5.0],
            hiddenActivations: [4.0, 0.0],
            hiddenDim: 2);

        Assert.Equal([6.0, 0.0], signals);
    }

    [Fact]
    public void ApplyGlobalPlattCalibration_Clamps_Extreme_Raw_Probabilities()
    {
        double calibrated = BaggedLogisticTrainer.ApplyGlobalPlattCalibration(
            rawProbability: 1.0,
            plattA: 1.0,
            plattB: 0.0);

        Assert.True(double.IsFinite(calibrated));
        Assert.InRange(calibrated, 0.999999, 0.99999995);
    }

    [Fact]
    public void ComputeLearnerCalAccuracies_Credits_Correct_Sell_Predictions()
    {
        var calSet = new List<TrainingSample>
        {
            new([0f], -1, 1f),
            new([0f],  1, 1f),
        };

        double[][] weights =
        [
            [0.0],
            [0.0],
        ];
        double[] biases =
        [
            MLFeatureHelper.Logit(0.1),
            MLFeatureHelper.Logit(0.9),
        ];

        var accuracies = BaggedLogisticTrainer.ComputeLearnerCalAccuracies(
            calSet,
            weights,
            biases,
            featureCount: 1,
            featureSubsets: null);

        Assert.Equal(0.5, accuracies[0], precision: 6);
        Assert.Equal(0.5, accuracies[1], precision: 6);
    }

    [Fact]
    public void ComputeLearnerCalAccuracies_Treats_Null_Subset_Entries_As_Full_Feature_Use()
    {
        var calSet = new List<TrainingSample>
        {
            new([1f], 1, 1f),
        };

        double[][] weights = [[2.0]];
        double[] biases = [0.0];
        int[][] featureSubsets = [null!];

        var accuracies = BaggedLogisticTrainer.ComputeLearnerCalAccuracies(
            calSet,
            weights,
            biases,
            featureCount: 1,
            featureSubsets);

        Assert.Equal(1.0, accuracies[0], precision: 6);
    }

    [Fact]
    public void ComputeLearnerProbability_Ignores_Invalid_Linear_Subset_Indices()
    {
        double probability = BaggedLogisticTrainer.ComputeLearnerProbability(
            features: [1f],
            learnerIndex: 0,
            weights: [[2.0]],
            biases: [0.0],
            featureCount: 1,
            subsets: [[0, 4, -1]],
            polyLearnerStartIndex: 1,
            mlpHiddenW: null,
            mlpHiddenB: null,
            mlpHiddenDim: 0);

        Assert.True(probability > 0.88);
    }

    [Fact]
    public void ComputeLearnerProbability_Ignores_Invalid_Mlp_Subset_Indices()
    {
        double probability = BaggedLogisticTrainer.ComputeLearnerProbability(
            features: [1f],
            learnerIndex: 0,
            weights: [[3.0]],
            biases: [0.0],
            featureCount: 1,
            subsets: [[0, 5]],
            polyLearnerStartIndex: 1,
            mlpHiddenW: [[2.0, 7.0]],
            mlpHiddenB: [[0.0]],
            mlpHiddenDim: 1);

        Assert.True(probability > 0.99);
    }

    [Fact]
    public void ComputeLearnerProbability_Tolerates_Truncated_MlpHiddenBiases()
    {
        double probability = BaggedLogisticTrainer.ComputeLearnerProbability(
            features: [1f],
            learnerIndex: 0,
            weights: [[1.0, 9.0]],
            biases: [0.0],
            featureCount: 1,
            subsets: null,
            polyLearnerStartIndex: 1,
            mlpHiddenW: [[2.0, 100.0]],
            mlpHiddenB: [[0.0]],
            mlpHiddenDim: 2);

        Assert.Equal(MLFeatureHelper.Sigmoid(2.0), probability, precision: 6);
    }

    [Fact]
    public void ComputeLogLossSubset_Ignores_Invalid_Subset_Indices()
    {
        var samples = new List<TrainingSample>
        {
            new([1f], 1, 1f),
        };

        double loss = BaggedLogisticTrainer.ComputeLogLossSubset(
            samples,
            w: [2.0],
            b: 0.0,
            subset: [0, 8, -1]);

        Assert.True(double.IsFinite(loss));
        Assert.True(loss < 0.2);
    }

    [Fact]
    public void ComputeLearnerCalAccuracies_Tolerates_Short_Bias_Array()
    {
        var calSet = new List<TrainingSample>
        {
            new([1f], 1, 1f),
        };

        var accuracies = BaggedLogisticTrainer.ComputeLearnerCalAccuracies(
            calSet,
            weights: [[2.0]],
            biases: [],
            featureCount: 1,
            featureSubsets: null);

        Assert.Equal(1.0, accuracies[0], precision: 6);
    }

    [Fact]
    public void ComputeEquityCurveStats_Registers_Drawdown_For_Immediate_Losses()
    {
        var predictions = new (int Predicted, int Actual)[]
        {
            (1, -1),
            (1, -1),
        };

        var (maxDrawdown, sharpe) = BaggedLogisticTrainer.ComputeEquityCurveStats(predictions);

        Assert.True(maxDrawdown > 1.0);
        Assert.True(double.IsFinite(sharpe));
    }

    [Fact]
    public void ComputeEquityCurveStats_Treats_Abstentions_As_Flat_Returns()
    {
        var predictions = new (int Predicted, int Actual)[]
        {
            (0, 1),
            (1, 1),
        };

        var (maxDrawdown, sharpe) = BaggedLogisticTrainer.ComputeEquityCurveStats(predictions);

        Assert.Equal(0.0, maxDrawdown, precision: 6);
        Assert.True(double.IsFinite(sharpe));
    }

    [Fact]
    public void BuildLearnerAccuracyWeights_Excludes_Inactive_Learners()
    {
        double[] calAccuracies = [0.8, 0.6, 0.4];
        bool[] activeLearners = [true, false, true];

        var weights = BaggedLogisticTrainer.BuildLearnerAccuracyWeights(calAccuracies, activeLearners);

        Assert.Equal(2.0 / 3.0, weights[0], precision: 6);
        Assert.Equal(0.0, weights[1], precision: 6);
        Assert.Equal(1.0 / 3.0, weights[2], precision: 6);
    }

    [Fact]
    public void AggregateSelectedLearnerProbs_Uses_Stacker_With_Neutral_Fill_For_Missing_Oob_Learners()
    {
        double[] probs = [0.2, 0.8];
        var meta = new BaggedLogisticTrainer.MetaLearner([8.0, -8.0], 0.0);

        double aggregated = BaggedLogisticTrainer.AggregateSelectedLearnerProbs(
            probs,
            learnerIndices: [0],
            meta);

        Assert.Equal(MLFeatureHelper.Sigmoid(8.0 * 0.2 - 8.0 * 0.5), aggregated, precision: 6);
    }

    [Fact]
    public void RunGreedyEnsembleSelection_Excludes_Inactive_Learners()
    {
        var calSet = Enumerable.Range(0, 12)
            .Select(_ => new TrainingSample([0f], 1, 1f))
            .ToList();

        double[][] weights = [[0.0], [0.0]];
        double[] biases =
        [
            MLFeatureHelper.Logit(0.9),
            MLFeatureHelper.Logit(0.1)
        ];

        var gesWeights = BaggedLogisticTrainer.RunGreedyEnsembleSelection(
            calSet,
            weights,
            biases,
            featureCount: 1,
            subsets: null,
            activeLearners: [false, true]);

        Assert.Equal(0.0, gesWeights[0], precision: 6);
        Assert.True(gesWeights[1] > 0.999);
    }

    [Fact]
    public void ComputeEnsembleDiversity_Excludes_Inactive_Learners()
    {
        double[][] weights =
        [
            [1.0, 0.0],
            [1.0, 0.0],
            [0.0, 1.0],
        ];

        double diversity = BaggedLogisticTrainer.ComputeEnsembleDiversity(
            weights,
            featureCount: 2,
            subsets: null,
            activeLearners: [true, true, false]);

        Assert.Equal(1.0, diversity, precision: 6);
    }

    [Fact]
    public void ComputeMeanProjectedFeatureImportance_Excludes_Inactive_Learners()
    {
        double[][] weights =
        [
            [2.0, 0.0],
            [0.0, 0.0],
        ];
        double[] biases = [1.0, 0.0];

        var importance = BaggedLogisticTrainer.ComputeMeanProjectedFeatureImportance(
            weights,
            biases,
            featureCount: 2);

        Assert.Equal(2.0, importance[0], precision: 6);
        Assert.Equal(0.0, importance[1], precision: 6);
    }

    [Theory]
    [InlineData(0.55, 1, 0.60, false)]
    [InlineData(0.65, 1, 0.60, true)]
    [InlineData(0.35, 0, 0.40, true)]
    public void IsPredictionCorrect_Uses_Provided_Threshold(
        double probability,
        int direction,
        double threshold,
        bool expected)
    {
        bool actual = BaggedLogisticTrainer.IsPredictionCorrect(probability, direction, threshold);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildNormalisedCdf_Returns_Empty_For_Empty_Input()
    {
        double[] cdf = BaggedLogisticTrainer.BuildNormalisedCdf([]);

        Assert.Empty(cdf);
    }

    [Fact]
    public void BuildNormalisedCdf_Falls_Back_To_Uniform_For_NonFinite_Weights()
    {
        double[] cdf = BaggedLogisticTrainer.BuildNormalisedCdf([double.NaN, double.PositiveInfinity]);

        Assert.Equal([0.5, 1.0], cdf);
    }

    [Fact]
    public void BuildNormalisedCdf_Clamps_Negative_Weights_To_Zero()
    {
        double[] cdf = BaggedLogisticTrainer.BuildNormalisedCdf([-5.0, 3.0]);

        Assert.Equal([0.0, 1.0], cdf);
    }

    [Fact]
    public void SampleFromCdf_Returns_Zero_For_Empty_Cdf()
    {
        int index = BaggedLogisticTrainer.SampleFromCdf([], new Random(1));

        Assert.Equal(0, index);
    }

    [Fact]
    public void ComputeTemporalWeights_Falls_Back_To_Uniform_For_NonFinite_Lambda()
    {
        double[] weights = BaggedLogisticTrainer.ComputeTemporalWeights(3, double.NaN);

        Assert.Equal(1.0 / 3.0, weights[0], precision: 6);
        Assert.Equal(1.0 / 3.0, weights[1], precision: 6);
        Assert.Equal(1.0 / 3.0, weights[2], precision: 6);
    }

    [Fact]
    public void ComputeTemporalWeights_Remains_Finite_For_Large_Negative_Lambda()
    {
        double[] weights = BaggedLogisticTrainer.ComputeTemporalWeights(3, -10000.0);

        Assert.All(weights, weight => Assert.True(double.IsFinite(weight)));
        Assert.Equal(1.0, weights.Sum(), precision: 6);
    }

    [Fact]
    public void ComputeSharpe_Returns_Zero_For_NonFinite_Buffer()
    {
        double sharpe = BaggedLogisticTrainer.ComputeSharpe([1.0, double.NaN], 2);

        Assert.Equal(0.0, sharpe, precision: 6);
    }

    [Fact]
    public void ComputeDecisionBoundaryStats_Skips_NonFinite_Probability_Values()
    {
        var calSet = new List<TrainingSample>
        {
            new([0f], 1, 1f),
        };

        var (mean, std) = BaggedLogisticTrainer.ComputeDecisionBoundaryStats(
            calSet,
            _ => double.NaN);

        Assert.Equal(0.0, mean, precision: 6);
        Assert.Equal(0.0, std, precision: 6);
    }

    [Fact]
    public void ComputeDurbinWatson_Returns_Default_For_NonFinite_Residuals()
    {
        var trainSet = new List<TrainingSample>
        {
            new([1f], 1, float.PositiveInfinity),
            new([2f], 1, 1f),
            new([3f], 1, 1f),
            new([4f], 1, 1f),
            new([5f], 1, 1f),
            new([6f], 1, 1f),
            new([7f], 1, 1f),
            new([8f], 1, 1f),
            new([9f], 1, 1f),
            new([10f], 1, 1f),
        };

        double dw = BaggedLogisticTrainer.ComputeDurbinWatson(
            trainSet,
            magWeights: [1.0],
            magBias: 0.0,
            featureCount: 1);

        Assert.Equal(2.0, dw, precision: 6);
    }

    [Fact]
    public void ComputeActiveLearnerMask_Does_Not_Activate_Zero_Learner_When_Bias_Is_Missing()
    {
        bool[] active = BaggedLogisticTrainer.ComputeActiveLearnerMask(
            weights: [[0.0], [1.0]],
            biases: []);

        Assert.False(active[0]);
        Assert.True(active[1]);
    }

    [Fact]
    public void GenerateFeatureSubset_Returns_Empty_For_Zero_Features()
    {
        int[] subset = BaggedLogisticTrainer.GenerateFeatureSubset(
            featureCount: 0,
            ratio: 0.5,
            seed: 1);

        Assert.Empty(subset);
    }

    [Fact]
    public void GenerateFeatureSubset_Clamps_Oversized_Ratio_To_FeatureCount()
    {
        int[] subset = BaggedLogisticTrainer.GenerateFeatureSubset(
            featureCount: 3,
            ratio: 2.0,
            seed: 1);

        Assert.Equal([0, 1, 2], subset);
    }

    [Fact]
    public void GenerateBiasedFeatureSubset_Returns_Empty_For_Zero_Features()
    {
        int[] subset = BaggedLogisticTrainer.GenerateBiasedFeatureSubset(
            featureCount: 0,
            ratio: 0.5,
            importanceScores: [],
            seed: 1);

        Assert.Empty(subset);
    }

    [Fact]
    public void GenerateBiasedFeatureSubset_Clamps_Oversized_Ratio_To_FeatureCount()
    {
        int[] subset = BaggedLogisticTrainer.GenerateBiasedFeatureSubset(
            featureCount: 3,
            ratio: 2.0,
            importanceScores: [0.1, 0.2, 0.3],
            seed: 1);

        Assert.Equal(3, subset.Length);
        Assert.Equal([0, 1, 2], subset);
    }

    [Fact]
    public void GenerateBiasedFeatureSubset_Ignores_NonFinite_And_Negative_ImportanceScores()
    {
        int[] subset = BaggedLogisticTrainer.GenerateBiasedFeatureSubset(
            featureCount: 3,
            ratio: 1.0,
            importanceScores: [double.NaN, -5.0, 0.2],
            seed: 1);

        Assert.Equal([0, 1, 2], subset);
    }

    [Fact]
    public void BuildFeatureMask_Keeps_Missing_Importance_Entries_Active()
    {
        bool[] mask = BaggedLogisticTrainer.BuildFeatureMask(
            importance: [0.1f],
            threshold: 1.0,
            featureCount: 3);

        Assert.Equal([false, true, true], mask);
    }
}
