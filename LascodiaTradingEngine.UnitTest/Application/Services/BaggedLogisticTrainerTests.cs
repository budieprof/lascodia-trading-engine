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
}
