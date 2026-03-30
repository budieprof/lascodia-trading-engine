using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class ElmTrainerHelpersTests
{
    [Fact]
    public void ComputeEce_Uses_Observed_Class_Frequency_Not_Hard_Label_Accuracy()
    {
        var samples = new List<TrainingSample>
        {
            new([0.1f], 0, 1f),
            new([0.1f], -1, 1f),
        };

        double ece = ElmEvaluationHelper.ComputeEce(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0]);

        Assert.Equal(0.1, ece, precision: 6);
    }

    [Fact]
    public void ComputeEce_Treats_NonFinite_Probabilities_As_Neutral()
    {
        var samples = new List<TrainingSample>
        {
            new([0.1f], 1, 1f),
            new([0.2f], 0, 1f),
        };

        double ece = ElmEvaluationHelper.ComputeEce(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleCalibProb: (_, _, _, _, _, _, _, _, _, _, _) => double.NaN);

        Assert.Equal(0.0, ece, precision: 6);
    }

    [Fact]
    public void ComputePermutationImportance_Clamps_To_Available_Feature_Width()
    {
        var samples = new List<TrainingSample>
        {
            new([0.1f], 1, 1f),
            new([0.9f], 0, 1f),
        };

        float[] importance = ElmEvaluationHelper.ComputePermutationImportance(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 3,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0],
            ct: CancellationToken.None);

        Assert.Equal(3, importance.Length);
        Assert.Equal(0f, importance[1]);
        Assert.Equal(0f, importance[2]);
    }

    [Fact]
    public void ComputePermutationImportance_Handles_Ragged_Samples_When_First_Row_Is_Wider()
    {
        var samples = new List<TrainingSample>
        {
            new([0.1f, 0.2f], 1, 1f),
            new([0.9f], 0, 1f),
        };

        float[] importance = ElmEvaluationHelper.ComputePermutationImportance(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 2,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0],
            ct: CancellationToken.None);

        Assert.Equal(2, importance.Length);
        Assert.All(importance, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void ComputePermutationImportance_Does_Not_Let_One_Truncated_Row_Hide_A_Higher_Feature()
    {
        var samples = new List<TrainingSample>
        {
            new([0f, 1f], 1, 1f),
            new([0f, 0f], 0, 1f),
            new([0f], 0, 1f),
            new([0f, 1f], 1, 1f),
        };

        float[] importance = ElmEvaluationHelper.ComputePermutationImportance(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 2,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features.Length > 1 ? features[1] : 0f,
            ct: CancellationToken.None);

        Assert.True(importance[1] > 0f);
    }

    [Fact]
    public void ComputeCalPermutationImportance_Clamps_To_Available_Feature_Width()
    {
        var samples = new List<TrainingSample>
        {
            new([0.1f], 1, 1f),
            new([0.9f], 0, 1f),
        };

        double[] importance = ElmEvaluationHelper.ComputeCalPermutationImportance(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            featureCount: 3,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleRawProb: (features, _, _, _, _, _, _, _, _) => features[0],
            ct: CancellationToken.None);

        Assert.Equal(3, importance.Length);
        Assert.Equal(0.0, importance[1], precision: 6);
        Assert.Equal(0.0, importance[2], precision: 6);
    }

    [Fact]
    public void ComputeCalPermutationImportance_Handles_Ragged_Samples_When_First_Row_Is_Wider()
    {
        var samples = new List<TrainingSample>
        {
            new([0.1f, 0.2f], 1, 1f),
            new([0.9f], 0, 1f),
        };

        double[] importance = ElmEvaluationHelper.ComputeCalPermutationImportance(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            featureCount: 2,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleRawProb: (features, _, _, _, _, _, _, _, _) => features[0],
            ct: CancellationToken.None);

        Assert.Equal(2, importance.Length);
        Assert.All(importance, value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void ComputeCalPermutationImportance_Does_Not_Let_One_Truncated_Row_Hide_A_Higher_Feature()
    {
        var samples = new List<TrainingSample>
        {
            new([0f, 0.9f], 1, 1f),
            new([0f, 0.1f], 0, 1f),
            new([0f], 0, 1f),
            new([0f, 0.9f], 1, 1f),
        };

        double[] importance = ElmEvaluationHelper.ComputeCalPermutationImportance(
            samples,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            featureCount: 2,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleRawProb: (features, _, _, _, _, _, _, _, _) => features.Length > 1 ? features[1] : 0f,
            ct: CancellationToken.None);

        Assert.True(importance[1] > 0.0);
    }

    [Fact]
    public void StratifiedBiasedBootstrap_Uses_Larger_Class_For_Odd_Remainder()
    {
        var train = new List<TrainingSample>
        {
            new([1f], 1, 1f),
            new([2f], 1, 1f),
            new([3f], 0, 1f),
            new([4f], 0, 1f),
            new([5f], 0, 1f),
        };

        var bootstrap = ElmBootstrapHelper.StratifiedBiasedBootstrap(
            train,
            temporalWeights: [0.2, 0.2, 0.2, 0.2, 0.2],
            count: 5,
            seed: 7);

        Assert.Equal(5, bootstrap.Count);
        Assert.Equal(2, bootstrap.Count(s => s.Direction > 0));
        Assert.Equal(3, bootstrap.Count(s => s.Direction <= 0));
    }

    [Fact]
    public void GenerateBiasedFeatureSubset_Clamps_Negative_Importance_Scores()
    {
        int[] subset = ElmBootstrapHelper.GenerateBiasedFeatureSubset(
            featureCount: 4,
            ratio: 0.5,
            importanceScores: [-10.0, -5.0, 0.0, 2.0],
            seed: 11);

        Assert.Equal(2, subset.Length);
        Assert.Equal(subset.Distinct().Count(), subset.Length);
        Assert.All(subset, index => Assert.InRange(index, 0, 3));
    }

    [Fact]
    public void GenerateBiasedFeatureSubset_Sanitises_NonFinite_Importance_Scores()
    {
        int[] subset = ElmBootstrapHelper.GenerateBiasedFeatureSubset(
            featureCount: 4,
            ratio: 0.5,
            importanceScores: [double.NaN, double.PositiveInfinity, 1.0, 2.0],
            seed: 11);

        Assert.Equal(2, subset.Length);
        Assert.Equal(subset.Distinct().Count(), subset.Length);
        Assert.All(subset, index => Assert.InRange(index, 0, 3));
    }

    [Fact]
    public void ComputeCovariateShiftWeights_Uses_Extreme_Breakpoints_And_Checked_Features()
    {
        var samples = new List<TrainingSample>
        {
            new([5f, 0f], 1, 1f),
            new([0.5f, 0f], 0, 1f),
        };

        double[] weights = ElmBootstrapHelper.ComputeCovariateShiftWeights(
            samples,
            parentBp:
            [
                [-1.0, 0.0, 1.0],
            ],
            featureCount: 2);

        Assert.True(weights[0] > weights[1]);
        Assert.Equal(1.0, weights.Sum(), precision: 6);
    }

    [Fact]
    public void ComputeCovariateShiftWeights_Clamps_To_Available_Feature_Width()
    {
        var samples = new List<TrainingSample>
        {
            new([5f], 1, 1f),
            new([0.5f], 0, 1f),
        };

        double[] weights = ElmBootstrapHelper.ComputeCovariateShiftWeights(
            samples,
            parentBp:
            [
                [-1.0, 0.0, 1.0],
                [0.0, 1.0],
            ],
            featureCount: 3);

        Assert.Equal(2, weights.Length);
        Assert.True(weights[0] > weights[1]);
        Assert.Equal(1.0, weights.Sum(), precision: 6);
    }

    [Fact]
    public void ComputeCovariateShiftWeights_Sanitises_NonFinite_Features_And_Breakpoints()
    {
        var samples = new List<TrainingSample>
        {
            new([float.NaN, 2f], 1, 1f),
            new([1f, 3f], 0, 1f),
        };

        double[] weights = ElmBootstrapHelper.ComputeCovariateShiftWeights(
            samples,
            parentBp:
            [
                [double.NaN, double.PositiveInfinity],
                [0.0, 2.5],
            ],
            featureCount: 2);

        Assert.Equal(2, weights.Length);
        Assert.All(weights, weight => Assert.True(double.IsFinite(weight) && weight >= 0.0));
        Assert.Equal(1.0, weights.Sum(), precision: 6);
    }

    [Fact]
    public void ComputeCovariateShiftWeights_Uses_Min_And_Max_When_Breakpoints_Are_Unsorted()
    {
        var samples = new List<TrainingSample>
        {
            new([2.5f], 1, 1f),
            new([0.5f], 0, 1f),
        };

        double[] weights = ElmBootstrapHelper.ComputeCovariateShiftWeights(
            samples,
            parentBp:
            [
                [3.0, 1.0, 2.0],
            ],
            featureCount: 1);

        Assert.True(weights[1] > weights[0]);
        Assert.Equal(1.0, weights.Sum(), precision: 6);
    }

    [Fact]
    public void FitPlattScaling_Returns_Identity_For_Single_Class_Calibration_Set()
    {
        var calSet = new List<TrainingSample>
        {
            new([0.2f], 1, 1f),
            new([0.3f], 1, 1f),
            new([0.4f], 1, 1f),
            new([0.5f], 1, 1f),
            new([0.6f], 1, 1f),
        };

        var (a, b) = ElmCalibrationHelper.FitPlattScaling(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleRawProb: (features, _, _, _, _, _, _, _, _) => features[0]);

        Assert.Equal(1.0, a, precision: 6);
        Assert.Equal(0.0, b, precision: 6);
    }

    [Fact]
    public void FitPlattScaling_Keeps_Params_Finite_When_Raw_Probabilities_Are_NonFinite()
    {
        var calSet = new List<TrainingSample>
        {
            new([0.2f], 1, 1f),
            new([0.3f], 0, 1f),
            new([0.4f], 1, 1f),
            new([0.5f], 0, 1f),
            new([0.6f], 1, 1f),
        };

        var (a, b) = ElmCalibrationHelper.FitPlattScaling(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleRawProb: (_, _, _, _, _, _, _, _, _) => double.NaN);

        Assert.True(double.IsFinite(a));
        Assert.True(double.IsFinite(b));
    }

    [Fact]
    public void FitPlattScalingCV_Falls_Back_When_Fold_Count_Is_Invalid()
    {
        var calSet = new List<TrainingSample>
        {
            new([0.2f], 1, 1f),
            new([0.3f], 0, 1f),
            new([0.4f], 1, 1f),
            new([0.5f], 0, 1f),
            new([0.6f], 1, 1f),
        };

        var (a, b) = ElmCalibrationHelper.FitPlattScalingCV(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleRawProb: (features, _, _, _, _, _, _, _, _) => features[0],
            cvFolds: 0);

        Assert.True(double.IsFinite(a));
        Assert.True(double.IsFinite(b));
    }

    [Fact]
    public void ElmInferenceEngine_Uses_PerLearner_Output_Dimension_When_Global_HiddenDim_Is_Smaller()
    {
        var engine = new ElmInferenceEngine();
        var snapshot = new ModelSnapshot
        {
            Type = "elm",
            Weights = [[0.0, 5.0]],
            Biases = [0.0],
            ElmInputWeights = [[0.0, 0.0]],
            ElmInputBiases = [[0.0, 10.0]],
            ElmHiddenDim = 1,
            LearnerActivations = [(int)ElmActivation.Sigmoid],
        };

        var result = engine.RunInference(
            features: [0f],
            featureCount: 1,
            snapshot,
            candleWindow: [],
            modelId: 1,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.NotNull(result);
        Assert.True(result.Value.Probability > 0.98);
    }

    [Fact]
    public void ElmInferenceEngine_Skips_Invalid_Learners_And_Uses_Valid_Ensemble_Members()
    {
        var engine = new ElmInferenceEngine();
        var snapshot = new ModelSnapshot
        {
            Type = "elm",
            Weights = [null!, [10.0]],
            Biases = [0.0, 0.0],
            ElmInputWeights = [null!, [0.0]],
            ElmInputBiases = [null!, [0.0]],
            ElmHiddenDim = 1,
        };

        var result = engine.RunInference(
            features: [0f],
            featureCount: 1,
            snapshot,
            candleWindow: [],
            modelId: 1,
            mcDropoutSamples: 3,
            mcDropoutSeed: 7);

        Assert.NotNull(result);
        Assert.True(result.Value.Probability > 0.99);
        Assert.NotNull(result.Value.McDropoutMean);
    }

    [Fact]
    public void CountNonStationaryFeatures_Uses_Adf_PValue_Threshold()
    {
        var samples = new List<TrainingSample>();
        var rng = new Random(42);
        double randomWalk = 0.0;

        for (int i = 0; i < 200; i++)
        {
            float stationary = (float)(rng.NextDouble() * 2.0 - 1.0);
            randomWalk += rng.NextDouble() * 2.0 - 1.0;

            samples.Add(new([stationary, (float)randomWalk], i % 2 == 0 ? 1 : 0, 1f));
        }

        int nonStationary = ElmEvaluationHelper.CountNonStationaryFeatures(samples, featureCount: 2);

        Assert.Equal(1, nonStationary);
    }

    [Fact]
    public void CountNonStationaryFeatures_Clamps_To_Available_Feature_Width()
    {
        var samples = Enumerable.Range(0, 30)
            .Select(i => new TrainingSample([(float)(i % 2)], i % 2 == 0 ? 1 : 0, 1f))
            .ToList();

        int nonStationary = ElmEvaluationHelper.CountNonStationaryFeatures(samples, featureCount: 3);

        Assert.InRange(nonStationary, 0, 1);
    }

    [Fact]
    public void ComputeEquityCurveStats_Registers_Immediate_Loss_As_Drawdown()
    {
        var (maxDrawdown, sharpe) = ElmMathHelper.ComputeEquityCurveStats(
            [(-1, 1)]);

        Assert.Equal(1.0, maxDrawdown, precision: 6);
        Assert.Equal(0.0, sharpe, precision: 6);
    }

    [Fact]
    public void ComputeSharpe_Sanitises_NonFinite_Returns_And_Annualisation()
    {
        double sharpe = ElmMathHelper.ComputeSharpe(
            [double.NaN, 1.0, -1.0],
            annualisationFactor: double.NaN);

        Assert.True(double.IsFinite(sharpe));
        Assert.Equal(0.0, sharpe, precision: 6);
    }

    [Fact]
    public void StdDev_Sanitises_NonFinite_Values_And_Mean()
    {
        double std = ElmMathHelper.StdDev(
            [double.NaN, 1.0, -1.0],
            mean: double.NaN);

        Assert.True(double.IsFinite(std));
        Assert.Equal(1.0, std, precision: 6);
    }

    [Fact]
    public void ComputeSharpeTrend_Sanitises_NonFinite_Fold_Values()
    {
        double trend = ElmMathHelper.ComputeSharpeTrend([double.NaN, 1.0, 2.0]);

        Assert.True(double.IsFinite(trend));
        Assert.Equal(1.0, trend, precision: 6);
    }

    [Fact]
    public void ComputeEquityCurveStats_Sanitises_NonFinite_Annualisation()
    {
        var (maxDrawdown, sharpe) = ElmMathHelper.ComputeEquityCurveStats(
            [(1, 1), (-1, 1)],
            annualisationFactor: double.NaN);

        Assert.Equal(0.5, maxDrawdown, precision: 6);
        Assert.True(double.IsFinite(sharpe));
    }

    [Fact]
    public void DotProductSimd_Clamps_To_Available_Weights_Row()
    {
        double dot = ElmMathHelper.DotProductSimd(
            weights: [2.0],
            weightOffset: 0,
            features: [3f, 7f],
            subset: [0, 1],
            subsetLen: 2);

        Assert.Equal(6.0, dot, precision: 6);
    }

    [Fact]
    public void DotProductSimdContiguous_Clamps_To_Available_Weights_Row()
    {
        double dot = ElmMathHelper.DotProductSimdContiguous(
            weights: [2.0],
            weightOffset: 0,
            features: [3f, 7f],
            length: 2);

        Assert.Equal(6.0, dot, precision: 6);
    }

    [Fact]
    public void ShermanMorrisonUpdate_Rejects_Negative_Denominator_From_Invalid_Inverse()
    {
        bool updated = ElmMathHelper.ShermanMorrisonUpdate(
            inverseGramFlat: [-2.0],
            gramDim: 1,
            coefficients: [0.0],
            featureVector: [1.0],
            target: 1.0);

        Assert.False(updated);
    }

    [Fact]
    public void ShermanMorrisonUpdate_Rejects_NonFinite_InverseGram()
    {
        bool updated = ElmMathHelper.ShermanMorrisonUpdate(
            inverseGramFlat: [double.NaN],
            gramDim: 1,
            coefficients: [0.0],
            featureVector: [1.0],
            target: 1.0);

        Assert.False(updated);
    }

    [Fact]
    public void ShermanMorrisonUpdate_Does_Not_Corrupt_State_When_Update_Becomes_NonFinite()
    {
        double[] inverseGram = [1e308];
        double[] coefficients = [0.0];

        bool updated = ElmMathHelper.ShermanMorrisonUpdate(
            inverseGramFlat: inverseGram,
            gramDim: 1,
            coefficients: coefficients,
            featureVector: [1e308],
            target: 1.0);

        Assert.False(updated);
        Assert.Equal([1e308], inverseGram, new PrecisionComparer(6));
        Assert.Equal([0.0], coefficients, new PrecisionComparer(6));
    }

    [Fact]
    public void CholeskySolve_Rejects_NonFinite_Matrix_Instead_Of_Returning_NaN_Solution()
    {
        double[,] matrix = { { double.NaN } };
        double[] rhs = [1.0];
        double[] solution = [0.0];

        bool solved = ElmMathHelper.CholeskySolve(matrix, rhs, solution, n: 1);

        Assert.False(solved);
        Assert.Equal(0.0, solution[0], precision: 6);
    }

    [Fact]
    public void TryInvertSpd_Rejects_NonFinite_Matrix_Instead_Of_Returning_NaN_Inverse()
    {
        double[,] matrix = { { double.NaN } };
        double[,] inverse = { { 0.0 } };

        bool inverted = ElmMathHelper.TryInvertSpd(matrix, inverse, n: 1);

        Assert.False(inverted);
        Assert.Equal(0.0, inverse[0, 0], precision: 6);
    }

    [Fact]
    public void ShermanMorrisonUpdate_Rolls_Back_When_Weight_Update_Becomes_NonFinite()
    {
        double[] inverseGram = [1.0];
        double[] coefficients = [1e308];

        bool updated = ElmMathHelper.ShermanMorrisonUpdate(
            inverseGramFlat: inverseGram,
            gramDim: 1,
            coefficients: coefficients,
            featureVector: [1e308],
            target: 0.0);

        Assert.False(updated);
        Assert.Equal([1.0], inverseGram, new PrecisionComparer(6));
        Assert.Equal([1e308], coefficients, new PrecisionComparer(6));
    }

    [Fact]
    public void ComputeAvgKellyFraction_Uses_Probability_Edge_Not_Label_Magnitude_Buckets()
    {
        var calSet = new List<TrainingSample>
        {
            new([0.6f], 1, 10f),
            new([0.6f], 0, 1f),
        };

        double avgKelly = ElmCalibrationHelper.ComputeAvgKellyFraction(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0]);

        Assert.Equal(0.1, avgKelly, precision: 6);
    }

    [Fact]
    public void ComputeOptimalThreshold_Uses_MagnitudeWeighted_Directional_Boundary()
    {
        var calSet = new List<TrainingSample>();
        calSet.AddRange(Enumerable.Repeat(new TrainingSample([0.95f], 1, 1f), 4));
        calSet.AddRange(Enumerable.Repeat(new TrainingSample([0.55f], 0, 50f), 3));
        calSet.AddRange(Enumerable.Repeat(new TrainingSample([0.35f], 0, 1f), 3));

        double threshold = ElmCalibrationHelper.ComputeOptimalThreshold(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            searchMin: 50,
            searchMax: 60,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0]);

        Assert.Equal(0.56, threshold, precision: 6);
    }

    [Fact]
    public void ComputeOptimalThreshold_Keeps_Default_When_Calibration_Set_Is_Too_Small()
    {
        var calSet = new List<TrainingSample>
        {
            new([0.95f], 1, 1f),
            new([0.55f], 0, 50f),
            new([0.35f], 0, 1f),
        };

        double threshold = ElmCalibrationHelper.ComputeOptimalThreshold(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            searchMin: 50,
            searchMax: 60,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0]);

        Assert.Equal(0.5, threshold, precision: 6);
    }

    [Fact]
    public void ComputeOptimalThreshold_Remains_Finite_When_Magnitudes_Are_NonFinite()
    {
        var calSet = Enumerable.Range(0, 12)
            .Select(i => new TrainingSample(
                [(float)(i % 2 == 0 ? 0.8 : 0.2)],
                i % 2 == 0 ? 1 : 0,
                i == 0 ? float.NaN : 1f))
            .ToList();

        double threshold = ElmCalibrationHelper.ComputeOptimalThreshold(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            searchMin: 40,
            searchMax: 60,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0]);

        Assert.True(double.IsFinite(threshold));
        Assert.InRange(threshold, 0.40, 0.60);
    }

    [Fact]
    public void ComputeDecisionBoundaryStats_Returns_Finite_Values_For_NonFinite_Raw_Probabilities()
    {
        var calSet = new List<TrainingSample>
        {
            new([0.2f], 1, 1f),
            new([0.3f], 0, 1f),
        };

        var (mean, std) = ElmCalibrationHelper.ComputeDecisionBoundaryStats(
            calSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            ensembleRawProb: (_, _, _, _, _, _, _, _, _) => double.NaN);

        Assert.True(double.IsFinite(mean));
        Assert.True(double.IsFinite(std));
    }

    [Fact]
    public void ApplyIsotonicCalibration_Sanitises_Malformed_Breakpoints_And_NonFinite_Input()
    {
        double calibrated = ElmCalibrationHelper.ApplyIsotonicCalibration(
            double.NaN,
            [double.NaN, 0.1, 0.25, -0.5, 0.75, 2.0, 0.5]);

        Assert.Equal(0.5, calibrated, precision: 6);
    }

    [Fact]
    public void ApplyTemperatureAwareCalibration_Sanitises_NonFinite_Parameters()
    {
        MethodInfo applyTemperatureAwareCalibration = typeof(ElmCalibrationHelper).GetMethod(
            "ApplyTemperatureAwareCalibration",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double calibrated = (double)applyTemperatureAwareCalibration.Invoke(null,
        [
            0.8,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            null!,
            double.PositiveInfinity,
            DateTime.UtcNow,
        ])!;

        Assert.Equal(0.8, calibrated, precision: 6);
    }

    [Fact]
    public void BuildNormalisedCdf_Falls_Back_To_Uniform_When_Weights_Are_Degenerate()
    {
        double[] cdf = ElmBootstrapHelper.BuildNormalisedCdf([0.0, 0.0, double.NaN]);

        Assert.Equal([1.0 / 3.0, 2.0 / 3.0, 1.0], cdf, new PrecisionComparer(6));
    }

    [Fact]
    public void ComputeDensityRatioWeights_Sanitises_NonFinite_Features_And_Remains_Normalised()
    {
        var train = Enumerable.Range(0, 40)
            .Select(i => new TrainingSample(
                [i == 0 ? float.NaN : (float)i],
                i < 30 ? 0 : 1,
                1f))
            .ToList();

        double[] weights = ElmBootstrapHelper.ComputeDensityRatioWeights(
            train,
            featureCount: 1,
            recentWindowDays: 20,
            barsPerDay: 1);

        Assert.Equal(40, weights.Length);
        Assert.All(weights, weight => Assert.True(double.IsFinite(weight) && weight >= 0.0));
        Assert.Equal(1.0, weights.Sum(), precision: 6);
    }

    [Fact]
    public void ComputeTemporalWeights_Remains_Finite_For_Large_Lambda()
    {
        double[] weights = ElmBootstrapHelper.ComputeTemporalWeights(count: 4, lambda: 5000.0);

        Assert.Equal(4, weights.Length);
        Assert.All(weights, weight => Assert.True(double.IsFinite(weight) && weight >= 0.0));
        Assert.Equal(1.0, weights.Sum(), precision: 6);
    }

    [Fact]
    public void ComputeTemporalWeights_Negative_Lambda_Falls_Back_To_Uniform()
    {
        double[] weights = ElmBootstrapHelper.ComputeTemporalWeights(count: 4, lambda: -3.0);

        Assert.Equal([0.25, 0.25, 0.25, 0.25], weights, new PrecisionComparer(6));
    }

    [Fact]
    public void GenerateFeatureSubset_Clamps_Ratio_To_Valid_Range()
    {
        int[] subset = ElmBootstrapHelper.GenerateFeatureSubset(featureCount: 3, ratio: 2.0, seed: 17);

        Assert.Equal([0, 1, 2], subset);
    }

    [Fact]
    public void GenerateFeatureSubsetFromPool_Returns_Empty_For_Empty_Pool()
    {
        int[] subset = ElmBootstrapHelper.GenerateFeatureSubsetFromPool([], ratio: 0.5, seed: 17);

        Assert.Empty(subset);
    }

    [Fact]
    public void GenerateFeatureSubsetFromPool_Ignores_Invalid_And_Duplicate_Entries()
    {
        int[] subset = ElmBootstrapHelper.GenerateFeatureSubsetFromPool([2, -1, 2, 4], ratio: 1.0, seed: 17);

        Assert.Equal([2, 4], subset);
    }

    [Fact]
    public void GenerateBiasedFeatureSubsetFromPool_Ignores_Invalid_Entries_And_NonFinite_Importance()
    {
        int[] subset = ElmBootstrapHelper.GenerateBiasedFeatureSubsetFromPool(
            pool: [3, -1, 3, 1],
            ratio: 1.0,
            importanceScores: [0.0, double.NaN, 0.0, double.PositiveInfinity],
            seed: 17);

        Assert.Equal([1, 3], subset);
    }

    [Fact]
    public void GenerateSmoteSamples_Handles_Ragged_And_NonFinite_Minority_Samples()
    {
        var synthetic = ElmBootstrapHelper.GenerateSmoteSamples(
            minoritySamples:
            [
                new TrainingSample([float.NaN, 2f], 1, float.NaN),
                new TrainingSample([3f], 1, 4f),
            ],
            syntheticCount: 2,
            kNeighbors: 1,
            seed: 5);

        Assert.Equal(2, synthetic.Count);
        Assert.All(synthetic, item =>
        {
            Assert.True(item.IsSynthetic);
            Assert.Single(item.Sample.Features);
            Assert.All(item.Sample.Features, value => Assert.True(float.IsFinite(value)));
            Assert.True(float.IsFinite(item.Sample.Magnitude));
        });
    }

    [Fact]
    public void BuildFeatureMask_Normalises_Positive_Importance_Before_Applying_Threshold()
    {
        bool[] mask = ElmBootstrapHelper.BuildFeatureMask(
            importance: [10f, 1f, 1f],
            threshold: 0.5,
            featureCount: 3);

        Assert.Equal([true, false, false], mask);
    }

    [Fact]
    public void BuildFeatureMask_Returns_Empty_When_FeatureCount_Is_Zero()
    {
        bool[] mask = ElmBootstrapHelper.BuildFeatureMask(
            importance: [10f, 1f, 1f],
            threshold: 0.5,
            featureCount: 0);

        Assert.Empty(mask);
    }

    [Fact]
    public void BuildFeatureMask_Keeps_Missing_Importance_Entries_Active()
    {
        bool[] mask = ElmBootstrapHelper.BuildFeatureMask(
            importance: [0.2f],
            threshold: 1.0,
            featureCount: 3);

        Assert.Equal([true, true, true], mask);
    }

    [Fact]
    public void ComputeConformalQHat_Sanitises_NonFinite_Alpha()
    {
        double qHat = ElmCalibrationHelper.ComputeConformalQHat(
            calSet:
            [
                new TrainingSample([0.2f], 1, 1f),
                new TrainingSample([0.3f], 0, 1f),
                new TrainingSample([0.4f], 1, 1f),
                new TrainingSample([0.5f], 0, 1f),
                new TrainingSample([0.6f], 1, 1f),
            ],
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            plattA: 1.0,
            plattB: 0.0,
            isotonicBp: [],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            alpha: double.NaN,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0]);

        Assert.True(double.IsFinite(qHat));
        Assert.InRange(qHat, 0.0, 1.0);
    }

    [Fact]
    public void AggregateProbs_Returns_Neutral_When_No_Learners_Are_Present()
    {
        double prob = InferenceHelpers.AggregateProbs(
            probs: [],
            count: 0,
            metaWeights: null,
            metaBias: 0.0,
            gesWeights: null,
            learnerAccuracyWeights: null,
            calAccuracies: null);

        Assert.Equal(0.5, prob, precision: 6);
    }

    [Fact]
    public void ElmInferenceEngine_Reuses_First_Activation_When_Array_Is_Shorter_Than_Learner_Count()
    {
        var engine = new ElmInferenceEngine();
        var snapshot = new ModelSnapshot
        {
            Type = "elm",
            Weights = [[10.0], [10.0]],
            Biases = [0.0, 0.0],
            ElmInputWeights = [[1.0], [1.0]],
            ElmInputBiases = [[0.0], [0.0]],
            ElmHiddenDim = 1,
            LearnerActivations = [(int)ElmActivation.Relu],
        };

        var result = engine.RunInference(
            features: [0.2f],
            featureCount: 1,
            snapshot,
            candleWindow: [],
            modelId: 1,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.NotNull(result);
        Assert.InRange(result.Value.Probability, 0.87, 0.89);
    }

    [Fact]
    public void ElmInferenceEngine_Ignores_Negative_Feature_Subset_Indices_Instead_Of_Crashing()
    {
        var engine = new ElmInferenceEngine();
        var snapshot = new ModelSnapshot
        {
            Type = "elm",
            Weights = [[10.0]],
            Biases = [0.0],
            ElmInputWeights = [[5.0]],
            ElmInputBiases = [[0.0]],
            ElmHiddenDim = 1,
            LearnerActivations = [(int)ElmActivation.Relu],
            FeatureSubsetIndices = [[-1]],
        };

        var result = engine.RunInference(
            features: [1f],
            featureCount: 1,
            snapshot,
            candleWindow: [],
            modelId: 1,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.NotNull(result);
        Assert.Equal(0.5, result.Value.Probability, precision: 6);
    }

    [Fact]
    public void ElmInferenceEngine_Returns_Neutral_When_Learner_Output_Is_NonFinite()
    {
        var engine = new ElmInferenceEngine();
        var snapshot = new ModelSnapshot
        {
            Type = "elm",
            Weights = [[double.NaN]],
            Biases = [0.0],
            ElmInputWeights = [[0.0]],
            ElmInputBiases = [[0.0]],
            ElmHiddenDim = 1,
        };

        var result = engine.RunInference(
            features: [1f],
            featureCount: 1,
            snapshot,
            candleWindow: [],
            modelId: 1,
            mcDropoutSamples: 2,
            mcDropoutSeed: 1);

        Assert.NotNull(result);
        Assert.Equal(0.5, result.Value.Probability, precision: 6);
        Assert.Equal(0.0, result.Value.EnsembleStd, precision: 6);
        Assert.Equal(0.5m, result.Value.McDropoutMean);
    }

    [Fact]
    public void UpdateOnline_Reuses_First_Activation_When_Array_Is_Shorter_Than_Learner_Count()
    {
        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var snapshot = new ModelSnapshot
        {
            Weights = [[0.0], [0.0]],
            Biases = [0.0, 0.0],
            ElmInputWeights = [[1.0], [1.0]],
            ElmInputBiases = [[0.0], [0.0]],
            ElmInverseGram =
            [
                [1.0, 0.0, 0.0, 1.0],
                [1.0, 0.0, 0.0, 1.0],
            ],
            ElmInverseGramDim = [2, 2],
            Means = [0f],
            Stds = [1f],
            LearnerActivations = [(int)ElmActivation.Relu],
        };

        bool updated = trainer.UpdateOnline(snapshot, new TrainingSample([-1f], 1, 1f));

        Assert.True(updated);
        Assert.Equal(0.0, snapshot.Weights[0][0], precision: 6);
        Assert.Equal(0.0, snapshot.Weights[1][0], precision: 6);
        Assert.Equal(0.5, snapshot.Biases[0], precision: 6);
        Assert.Equal(0.5, snapshot.Biases[1], precision: 6);
    }

    [Fact]
    public void UpdateOnline_Treats_Empty_Subset_Metadata_As_Full_Feature_Use()
    {
        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var snapshot = new ModelSnapshot
        {
            Weights = [[0.0]],
            Biases = [0.0],
            ElmInputWeights = [[1.0]],
            ElmInputBiases = [[0.0]],
            ElmInverseGram = [[1.0, 0.0, 0.0, 1.0]],
            ElmInverseGramDim = [2],
            Means = [0f],
            Stds = [1f],
            FeatureSubsetIndices = [[]],
        };

        bool updated = trainer.UpdateOnline(snapshot, new TrainingSample([1f], 1, 1f));

        Assert.True(updated);
        Assert.NotEqual(0.0, snapshot.Biases[0]);
        Assert.Equal(1, snapshot.TrainSamples);
    }

    [Fact]
    public void UpdateOnline_Uses_Augmented_Inverse_With_Label_Smoothing_And_Feature_Mask()
    {
        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var snapshot = new ModelSnapshot
        {
            Weights = [[0.0]],
            Biases = [0.0],
            ElmInputWeights = [[2.0]],
            ElmInputBiases = [[0.0]],
            ElmInverseGram = [[1.0, 0.0, 0.0, 1.0]],
            ElmInverseGramDim = [2],
            Means = [0f],
            Stds = [1f],
            ActiveFeatureMask = [false],
            LearnerActivations = [(int)ElmActivation.Relu],
            AdaptiveLabelSmoothing = 0.1,
            TrainSamples = 10,
        };

        bool updated = trainer.UpdateOnline(snapshot, new TrainingSample([3f], 1, 1f));

        Assert.True(updated);
        Assert.Equal(0.0, snapshot.Weights[0][0], precision: 6);
        Assert.Equal(0.45, snapshot.Biases[0], precision: 6);
    }

    [Fact]
    public void UpdateOnline_Treats_NonFinite_Label_Smoothing_As_Zero()
    {
        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var snapshot = new ModelSnapshot
        {
            Weights = [[0.0]],
            Biases = [0.0],
            ElmInputWeights = [[0.0]],
            ElmInputBiases = [[0.0]],
            ElmInverseGram = [[1.0, 0.0, 0.0, 1.0]],
            ElmInverseGramDim = [2],
            Means = [0f],
            Stds = [1f],
            AdaptiveLabelSmoothing = double.NaN,
            TrainSamples = 10,
        };

        bool updated = trainer.UpdateOnline(snapshot, new TrainingSample([1f], 1, 1f));

        Assert.True(updated);
        Assert.Equal(4.0 / 9.0, snapshot.Biases[0], precision: 6);
    }

    [Fact]
    public void UpdateOnline_Skips_NonFinite_InverseGram_Without_Corrupting_State()
    {
        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var snapshot = new ModelSnapshot
        {
            Weights = [[0.0]],
            Biases = [0.0],
            ElmInputWeights = [[0.0]],
            ElmInputBiases = [[0.0]],
            ElmInverseGram = [[double.NaN, 0.0, 0.0, 1.0]],
            ElmInverseGramDim = [2],
            Means = [0f],
            Stds = [1f],
        };

        bool updated = trainer.UpdateOnline(snapshot, new TrainingSample([1f], 1, 1f));

        Assert.False(updated);
        Assert.Equal(0.0, snapshot.Weights[0][0], precision: 6);
        Assert.Equal(0.0, snapshot.Biases[0], precision: 6);
        Assert.Equal(0, snapshot.TrainSamples);
    }

    [Fact]
    public void ApplyProductionCalibration_Keeps_NonFinite_Raw_Probabilities_Neutral_And_Finite()
    {
        MethodInfo applyCalibration = typeof(ElmModelTrainer).GetMethod(
            "ApplyProductionCalibration",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double calibrated = (double)applyCalibration.Invoke(null,
        [
            double.NaN,
            1.0,
            0.0,
            0.0,
            2.0,
            0.0,
            0.0,
            0.0,
        ])!;

        Assert.Equal(0.5, calibrated, precision: 6);
    }

    [Fact]
    public void ComputeMetaLabelScore_Sanitises_NonFinite_Weights_And_Truncated_Features()
    {
        MethodInfo computeMetaLabelScore = typeof(ElmModelTrainer).GetMethod(
            "ComputeMetaLabelScore",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double score = (double)computeMetaLabelScore.Invoke(null,
        [
            0.8,
            double.NaN,
            new[] { float.NaN },
            5,
            new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 4.0, 5.0, 6.0, 7.0 },
            double.NaN,
        ])!;

        Assert.Equal(0.5, score, precision: 6);
    }

    [Fact]
    public void EnsembleCalibProb_Returns_Neutral_When_No_Learners_Are_Present()
    {
        MethodInfo ensembleCalibProb = typeof(ElmModelTrainer).GetMethod(
            "EnsembleCalibProb",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double calibrated = (double)ensembleCalibProb.Invoke(null,
        [
            new[] { 1f },
            Array.Empty<double[]>(),
            Array.Empty<double>(),
            Array.Empty<double[]>(),
            Array.Empty<double[]>(),
            5.0,
            5.0,
            1,
            1,
            null!,
            null!,
            Array.Empty<int>(),
            Array.Empty<ElmActivation>(),
            null!,
            0.0,
        ])!;

        Assert.Equal(0.5, calibrated, precision: 6);
    }

    [Fact]
    public void EnsembleCalibProb_Returns_Finite_Value_When_Ensemble_Output_Is_NonFinite()
    {
        MethodInfo ensembleCalibProb = typeof(ElmModelTrainer).GetMethod(
            "EnsembleCalibProb",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double calibrated = (double)ensembleCalibProb.Invoke(null,
        [
            new[] { 1f },
            new[] { new[] { double.NaN } },
            new[] { 0.0 },
            new[] { new[] { 0.0 } },
            new[] { new[] { 0.0 } },
            1.0,
            0.0,
            1,
            1,
            null!,
            null!,
            new[] { 1 },
            new[] { ElmActivation.Sigmoid },
            null!,
            0.0,
        ])!;

        Assert.Equal(0.5, calibrated, precision: 6);
    }

    [Fact]
    public void ComputeEnsembleStd_Returns_Finite_Value_When_Learner_Probabilities_Are_NonFinite()
    {
        MethodInfo computeStd = typeof(ElmModelTrainer).GetMethod(
            "ComputeEnsembleStd",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double std = (double)computeStd.Invoke(null,
        [
            new[] { 1f },
            new[] { new[] { double.NaN }, new[] { double.NaN } },
            new[] { 0.0, 0.0 },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            1,
            null!,
            new[] { 1, 1 },
            new[] { ElmActivation.Sigmoid, ElmActivation.Sigmoid },
            null!,
            null!,
            0.0,
        ])!;

        Assert.Equal(0.0, std, precision: 6);
    }

    [Fact]
    public void UpdateOnline_Skips_Invalid_Samples_Before_Poisoning_Model_State()
    {
        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var snapshot = new ModelSnapshot
        {
            Weights = [[0.0]],
            Biases = [0.0],
            ElmInputWeights = [[1.0]],
            ElmInputBiases = [[0.0]],
            ElmInverseGram = [[1.0, 0.0, 0.0, 1.0]],
            ElmInverseGramDim = [2],
            Means = [0f],
            Stds = [1f],
        };

        bool invalidDirectionUpdated = trainer.UpdateOnline(snapshot, new TrainingSample([1f], 2, 1f));
        bool nonFiniteFeatureUpdated = trainer.UpdateOnline(snapshot, new TrainingSample([float.NaN], 1, 1f));

        Assert.False(invalidDirectionUpdated);
        Assert.False(nonFiniteFeatureUpdated);
        Assert.Equal(0.0, snapshot.Weights[0][0], precision: 6);
        Assert.Equal(0.0, snapshot.Biases[0], precision: 6);
        Assert.Equal(0, snapshot.TrainSamples);
    }

    [Fact]
    public void ComputeRedundantFeaturePairIndices_Clamps_To_Available_Feature_Width()
    {
        var samples = Enumerable.Range(0, 25)
            .Select(i => new TrainingSample([(float)i], i % 2 == 0 ? 1 : 0, 1f))
            .ToList();

        var redundant = ElmEvaluationHelper.ComputeRedundantFeaturePairIndices(
            samples,
            featureCount: 4,
            threshold: 0.1);

        Assert.Empty(redundant);
    }

    [Fact]
    public void ComputeRedundantFeaturePairIndices_Sanitises_NonFinite_Feature_Values()
    {
        var samples = Enumerable.Range(0, 25)
            .Select(i => new TrainingSample(
                [i == 0 ? float.NaN : (float)i, (float)(i % 3)],
                i % 2 == 0 ? 1 : 0,
                1f))
            .ToList();

        var redundant = ElmEvaluationHelper.ComputeRedundantFeaturePairIndices(
            samples,
            featureCount: 2,
            threshold: 0.1);

        Assert.All(redundant, pair =>
        {
            Assert.InRange(pair.IndexI, 0, 1);
            Assert.InRange(pair.IndexJ, 0, 1);
        });
    }

    [Fact]
    public void ComputeRedundantFeaturePairIndices_Does_Not_Throw_When_A_Higher_Feature_Is_Missing_In_One_Row()
    {
        var samples = new List<TrainingSample>();
        for (int i = 0; i < 30; i++)
        {
            samples.Add(i == 0
                ? new TrainingSample([0f], i % 2, 1f)
                : new TrainingSample([0f, i], i % 2, 1f));
        }

        var redundant = ElmEvaluationHelper.ComputeRedundantFeaturePairIndices(
            samples,
            featureCount: 2,
            threshold: 0.1);

        Assert.All(redundant, pair =>
        {
            Assert.InRange(pair.IndexI, 0, 1);
            Assert.InRange(pair.IndexJ, 0, 1);
        });
    }

    [Fact]
    public void CountNonStationaryFeatures_Sanitises_NonFinite_Feature_Values()
    {
        var samples = Enumerable.Range(0, 40)
            .Select(i => new TrainingSample(
                [i == 0 ? float.NaN : (float)Math.Sin(i), (float)i],
                i % 2 == 0 ? 1 : 0,
                1f))
            .ToList();

        int nonStationary = ElmEvaluationHelper.CountNonStationaryFeatures(samples, featureCount: 2);

        Assert.InRange(nonStationary, 0, 2);
    }

    [Fact]
    public void CountNonStationaryFeatures_Does_Not_Let_One_Truncated_Row_Hide_A_NonStationary_Feature()
    {
        var samples = new List<TrainingSample>();
        double randomWalk = 0.0;
        var rng = new Random(17);

        for (int i = 0; i < 120; i++)
        {
            randomWalk += rng.NextDouble() - 0.5;
            float stationary = (float)(rng.NextDouble() - 0.5);
            samples.Add(i == 0
                ? new TrainingSample([stationary], i % 2, 1f)
                : new TrainingSample([stationary, (float)randomWalk], i % 2, 1f));
        }

        int nonStationary = ElmEvaluationHelper.CountNonStationaryFeatures(samples, featureCount: 2);

        Assert.Equal(1, nonStationary);
    }

    [Fact]
    public void ComputeEnsembleDiversity_Handles_Truncated_FeatureSubset_Metadata()
    {
        var calSet = new List<TrainingSample>
        {
            new([1f], 1, 1f),
        };

        double diversity = ElmEvaluationHelper.ComputeEnsembleDiversity(
            calSet,
            weights: [new[] { 0.0 }, new[] { 0.0 }],
            biases: [0.0, 0.0],
            inputWeights: [new[] { 0.0 }, new[] { 0.0 }],
            inputBiases: [new[] { 0.0 }, new[] { 0.0 }],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: [new[] { 0 }],
            elmLearnerProb: (_, _, _, _, _, _, _, _, learnerIdx) => learnerIdx == 0 ? 0.6 : 0.4);

        Assert.Equal(1.0, diversity, precision: 6);
    }

    [Fact]
    public void ComputeEnsembleDiversity_Treats_NonFinite_Learner_Probabilities_As_Neutral()
    {
        var calSet = new List<TrainingSample>
        {
            new([1f], 1, 1f),
        };

        double diversity = ElmEvaluationHelper.ComputeEnsembleDiversity(
            calSet,
            weights: [new[] { 0.0 }, new[] { 0.0 }],
            biases: [0.0, 0.0],
            inputWeights: [new[] { 0.0 }, new[] { 0.0 }],
            inputBiases: [new[] { 0.0 }, new[] { 0.0 }],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            elmLearnerProb: (_, _, _, _, _, _, _, _, learnerIdx) => learnerIdx == 0 ? double.NaN : 0.9);

        Assert.Equal(0.0, diversity, precision: 6);
    }

    [Fact]
    public void ComputeEnsembleDiversity_Skips_Truncated_Core_Metadata_Instead_Of_Throwing()
    {
        var calSet = new List<TrainingSample>
        {
            new([1f], 1, 1f),
            new([0f], 0, 1f),
        };

        double diversity = ElmEvaluationHelper.ComputeEnsembleDiversity(
            calSet,
            weights: [new[] { 0.0 }, new[] { 0.0 }],
            biases: [0.0],
            inputWeights: [new[] { 0.0 }, new[] { 0.0 }],
            inputBiases: [new[] { 0.0 }, new[] { 0.0 }],
            featureCount: 1,
            hiddenSize: 1,
            featureSubsets: null,
            elmLearnerProb: (_, _, _, _, _, _, _, _, _) => 0.8);

        Assert.Equal(0.0, diversity, precision: 6);
    }

    [Fact]
    public void EvaluateEnsemble_Remains_Finite_When_Magnitude_Data_Is_NonFinite()
    {
        var testSet = new List<TrainingSample>
        {
            new([0.6f], 1, float.NaN),
            new([0.4f], 0, 1f),
        };

        EvalMetrics metrics = ElmEvaluationHelper.EvaluateEnsemble(
            testSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            magWeights: [],
            magBias: 0.0,
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 0,
            featureSubsets: null,
            magAugWeights: Array.Empty<double>(),
            magAugBias: 0.0,
            sharpeAnnualisationFactor: 252.0,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0],
            predictMagnitudeAug: (_, _, _, _, _, _, _, _) => double.NaN);

        Assert.True(double.IsFinite(metrics.ExpectedValue));
        Assert.True(double.IsFinite(metrics.MagnitudeRmse));
        Assert.True(double.IsFinite(metrics.SharpeRatio));
    }

    [Fact]
    public void EvaluateEnsemble_Uses_Configured_Decision_Threshold()
    {
        var testSet = new List<TrainingSample>
        {
            new([0.6f], 0, 1f),
        };

        EvalMetrics metrics = ElmEvaluationHelper.EvaluateEnsemble(
            testSet,
            weights: [],
            biases: [],
            inputWeights: [],
            inputBiases: [],
            magWeights: [],
            magBias: 0.0,
            plattA: 1.0,
            plattB: 0.0,
            featureCount: 1,
            hiddenSize: 0,
            featureSubsets: null,
            magAugWeights: Array.Empty<double>(),
            magAugBias: 0.0,
            sharpeAnnualisationFactor: 252.0,
            ensembleCalibProb: (features, _, _, _, _, _, _, _, _, _, _) => features[0],
            predictMagnitudeAug: (_, _, _, _, _, _, _, _) => 0.0,
            decisionThreshold: 0.7);

        Assert.Equal(1.0, metrics.Accuracy, precision: 6);
        Assert.Equal(1.0, metrics.ExpectedValue, precision: 6);
        Assert.Equal(1, metrics.TN);
    }

    [Fact]
    public void ComputeDurbinWatson_Remains_Finite_When_Magnitude_Predictions_Are_NonFinite()
    {
        double durbinWatson = ElmEvaluationHelper.ComputeDurbinWatson(
            train:
            [
                new TrainingSample([1f], 1, 1f),
                new TrainingSample([2f], 0, 2f),
                new TrainingSample([3f], 1, 3f),
            ],
            magWeights: [double.NaN],
            magBias: double.NaN,
            featureCount: 1,
            magAugWeights: null,
            magAugBias: 0.0,
            hiddenSize: 0,
            elmInputWeights: null,
            elmInputBiases: null,
            featureSubsets: null,
            predictMagnitudeAug: (_, _, _, _, _, _, _, _) => double.NaN);

        Assert.True(double.IsFinite(durbinWatson));
        Assert.InRange(durbinWatson, 0.0, double.MaxValue);
    }

    [Fact]
    public void ComputeLearnerCalibrationStats_Reuses_First_Activation_When_Array_Is_Shorter_Than_Learner_Count()
    {
        MethodInfo computeStats = typeof(ElmModelTrainer).GetMethod(
            "ComputeLearnerCalibrationStats",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var calSet = new List<TrainingSample>
        {
            new([-1f], 0, 1f),
        };

        dynamic result = computeStats.Invoke(trainer,
        [
            calSet,
            new[] { new[] { -2.0 }, new[] { -2.0 } },
            new[] { 0.0, 0.0 },
            new[] { new[] { 1.0 }, new[] { 1.0 } },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            1,
            null!,
            new[] { 1, 1 },
            new[] { ElmActivation.Relu },
        ])!;

        double[] accuracies = result.Item1;

        Assert.Equal([0.0, 0.0], accuracies, new PrecisionComparer(6));
    }

    [Fact]
    public void ComputeLearnerCalibrationStats_Handles_Truncated_Metadata_Arrays()
    {
        MethodInfo computeStats = typeof(ElmModelTrainer).GetMethod(
            "ComputeLearnerCalibrationStats",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var calSet = new List<TrainingSample>
        {
            new([1f], 1, 1f),
        };

        dynamic result = computeStats.Invoke(trainer,
        [
            calSet,
            new[] { new[] { 10.0 }, new[] { 10.0 } },
            new[] { 0.0, 0.0 },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            1,
            new[] { new[] { 0 } },
            new[] { 1 },
            new[] { ElmActivation.Sigmoid },
        ])!;

        double[] accuracies = result.Item1;

        Assert.Equal([1.0, 1.0], accuracies, new PrecisionComparer(6));
    }

    [Fact]
    public void ComputeLearnerCalibrationStats_Returns_Empty_When_No_Learners_Are_Present()
    {
        MethodInfo computeStats = typeof(ElmModelTrainer).GetMethod(
            "ComputeLearnerCalibrationStats",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var calSet = new List<TrainingSample>
        {
            new([1f], 1, 1f),
        };

        dynamic result = computeStats.Invoke(trainer,
        [
            calSet,
            Array.Empty<double[]>(),
            Array.Empty<double>(),
            Array.Empty<double[]>(),
            Array.Empty<double[]>(),
            1,
            null!,
            Array.Empty<int>(),
            Array.Empty<ElmActivation>(),
        ])!;

        double[] accuracies = result.Item1;
        double[]? accuracyWeights = result.Item2;

        Assert.Empty(accuracies);
        Assert.Null(accuracyWeights);
    }

    [Fact]
    public void FitElmMagnitudeRegressorCv_Purges_Lookback_Gap_Before_Fold_Training()
    {
        MethodInfo fitMagCv = typeof(ElmModelTrainer).GetMethod(
            "FitElmMagnitudeRegressorCV",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var train = Enumerable.Range(0, 70)
            .Select(i => new TrainingSample([(float)(i % 5)], i % 2 == 0 ? 1 : 0, i % 3))
            .ToList();

        dynamic result = fitMagCv.Invoke(null,
        [
            train,
            1,
            1,
            new[] { new[] { 0.0 } },
            new[] { new[] { 0.0 } },
            null!,
            new[] { ElmActivation.Sigmoid },
            0.001,
            10,
            2,
            2,
            0,
            CancellationToken.None,
        ])!;

        double[][]? foldWeights = result.Item5;
        double[]? foldBiases = result.Item6;

        Assert.Null(foldWeights);
        Assert.Null(foldBiases);
    }

    [Fact]
    public void PredictMagnitudeAug_Handles_Truncated_Augmented_Weights()
    {
        MethodInfo predictMagnitudeAug = typeof(ElmModelTrainer).GetMethod(
            "PredictMagnitudeAug",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double prediction = (double)predictMagnitudeAug.Invoke(null,
        [
            new[] { 2f, 3f },
            new[] { 1.5 },
            0.25,
            2,
            1,
            new[] { new[] { 0.0 } },
            new[] { new[] { 0.0 } },
            null!,
            new[] { ElmActivation.Sigmoid },
        ])!;

        Assert.Equal(3.25, prediction, precision: 6);
    }

    [Fact]
    public void PredictMagnitudeAug_Handles_Truncated_InputBias_Metadata()
    {
        MethodInfo predictMagnitudeAug = typeof(ElmModelTrainer).GetMethod(
            "PredictMagnitudeAug",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double prediction = (double)predictMagnitudeAug.Invoke(null,
        [
            new[] { 2f },
            new[] { 0.0, 1.5 },
            0.25,
            1,
            1,
            new[] { new[] { 2.0 }, new[] { 4.0 } },
            new[] { new[] { 0.5 } },
            null!,
            new[] { ElmActivation.Sigmoid },
        ])!;

        Assert.True(double.IsFinite(prediction));
        Assert.InRange(prediction, 0.25, 2.0);
    }

    [Fact]
    public void MlSignalScorer_PredictElmMagnitudeAug_Treats_Empty_Subset_Metadata_As_Full_Feature_Use()
    {
        MethodInfo predictElmMagnitudeAug = typeof(MLSignalScorer).GetMethod(
            "PredictElmMagnitudeAug",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var snapshot = new ModelSnapshot
        {
            ElmHiddenDim = 1,
            FeatureSubsetIndices = [[]],
            LearnerActivations = [(int)ElmActivation.Relu],
        };

        double prediction = (double)predictElmMagnitudeAug.Invoke(null,
        [
            new[] { 2f },
            1,
            new[] { 0.0, 1.5 },
            0.25,
            snapshot,
            new[] { new[] { 2.0 } },
            new[] { new[] { 0.0 } },
        ])!;

        Assert.Equal(6.25, prediction, precision: 6);
    }

    [Fact]
    public void ProjectAugWeightsToFeatureSpace_Handles_Empty_Train_And_Truncated_Augmented_Weights()
    {
        MethodInfo projectAugWeights = typeof(ElmModelTrainer).GetMethod(
            "ProjectAugWeightsToFeatureSpace",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double[] projected = (double[])projectAugWeights.Invoke(null,
        [
            new[] { 2.0 },
            2,
            1,
            1,
            new List<TrainingSample>(),
            new[] { new[] { 0.0 } },
            new[] { new[] { 0.0 } },
            null!,
            new[] { ElmActivation.Sigmoid },
        ])!;

        Assert.Equal([2.0, 0.0], projected, new PrecisionComparer(6));
    }

    [Fact]
    public void ProjectAugWeightsToFeatureSpace_Ignores_Negative_Subset_Indices()
    {
        MethodInfo projectAugWeights = typeof(ElmModelTrainer).GetMethod(
            "ProjectAugWeightsToFeatureSpace",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double[] projected = (double[])projectAugWeights.Invoke(null,
        [
            new[] { 0.0, 2.0 },
            1,
            1,
            1,
            new List<TrainingSample> { new([1f], 1, 1f) },
            new[] { new[] { 3.0 } },
            new[] { new[] { 0.0 } },
            new[] { new[] { -1 } },
            new[] { ElmActivation.Sigmoid },
        ])!;

        Assert.Equal([0.0], projected, new PrecisionComparer(6));
    }

    [Fact]
    public void RemapWarmStartForPruning_Skips_Zero_Overlap_Learners_Instead_Of_Injecting_Zero_Init()
    {
        MethodInfo remap = typeof(ElmModelTrainer).GetMethod(
            "RemapWarmStartForPruning",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var warmStart = new ModelSnapshot
        {
            ElmInputWeights = [[1.0, 2.0, 3.0, 4.0]],
            ElmInputBiases = [[0.1, 0.2]],
            FeatureSubsetIndices = [[1, 3]],
            FeatureImportanceScores = [0.5, 0.5, 0.0, 0.0],
            GenerationNumber = 2,
        };

        var remapped = (ModelSnapshot?)remap.Invoke(null, [warmStart, new[] { true, false, true, false }, 4, 2]);

        Assert.NotNull(remapped);
        Assert.Empty(remapped!.ElmInputWeights[0]);
        Assert.Empty(remapped.ElmInputBiases[0]);
        Assert.NotNull(remapped.FeatureSubsetIndices);
        Assert.Empty(remapped.FeatureSubsetIndices![0]);
    }

    [Fact]
    public void RemapWarmStartForPruning_Preserves_PerLearner_Hidden_Size()
    {
        MethodInfo remap = typeof(ElmModelTrainer).GetMethod(
            "RemapWarmStartForPruning",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var warmStart = new ModelSnapshot
        {
            ElmInputWeights = [[1.0, 2.0, 3.0, 4.0, 5.0, 6.0]],
            ElmInputBiases = [[0.1, 0.2, 0.3]],
            FeatureSubsetIndices = [[0, 2]],
            FeatureImportanceScores = [1.0, 0.0, 1.0],
            GenerationNumber = 3,
        };

        var remapped = (ModelSnapshot?)remap.Invoke(null, [warmStart, new[] { true, false, true }, 3, 1]);

        Assert.NotNull(remapped);
        Assert.Equal(6, remapped!.ElmInputWeights[0].Length);
        Assert.Equal(3, remapped.ElmInputBiases[0].Length);
        Assert.Equal([0, 2], remapped.FeatureSubsetIndices![0]);
    }

    [Fact]
    public void RemapWarmStartForPruning_Ignores_Negative_Subset_Indices()
    {
        MethodInfo remap = typeof(ElmModelTrainer).GetMethod(
            "RemapWarmStartForPruning",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var warmStart = new ModelSnapshot
        {
            ElmInputWeights = [[1.0, 2.0]],
            ElmInputBiases = [[0.1]],
            FeatureSubsetIndices = [[-1, 0]],
        };

        var remapped = (ModelSnapshot?)remap.Invoke(null, [warmStart, new[] { true }, 1, 1]);

        Assert.NotNull(remapped);
        Assert.Equal([0], remapped!.FeatureSubsetIndices![0]);
        Assert.Equal([2.0], remapped.ElmInputWeights[0], new PrecisionComparer(6));
    }

    [Fact]
    public void RemapWarmStartForPruning_Treats_Empty_Subset_Metadata_As_Full_Feature_Use()
    {
        MethodInfo remap = typeof(ElmModelTrainer).GetMethod(
            "RemapWarmStartForPruning",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var warmStart = new ModelSnapshot
        {
            ElmInputWeights = [[1.0, 2.0, 3.0]],
            ElmInputBiases = [[0.1]],
            FeatureSubsetIndices = [[]],
            FeatureImportanceScores = [1.0, 0.0, 1.0],
        };

        var remapped = (ModelSnapshot?)remap.Invoke(null, [warmStart, new[] { true, false, true }, 3, 1]);

        Assert.NotNull(remapped);
        Assert.Equal([0, 2], remapped!.FeatureSubsetIndices![0]);
        Assert.Equal([1.0, 3.0], remapped.ElmInputWeights[0], new PrecisionComparer(6));
    }

    [Fact]
    public void EnsembleCalibProb_Handles_Truncated_Metadata_Arrays()
    {
        MethodInfo ensembleCalibProb = typeof(ElmModelTrainer).GetMethod(
            "EnsembleCalibProb",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double calibrated = (double)ensembleCalibProb.Invoke(null,
        [
            new[] { 1f },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            new[] { 0.0, 0.0 },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            1.0,
            0.0,
            1,
            1,
            new[] { new[] { 0 } },
            null!,
            new[] { 1 },
            new[] { ElmActivation.Sigmoid },
            null!,
            0.0,
        ])!;

        Assert.Equal(0.5, calibrated, precision: 6);
    }

    [Fact]
    public void ComputeEnsembleStd_Skips_Learners_With_Truncated_Core_Metadata()
    {
        MethodInfo computeStd = typeof(ElmModelTrainer).GetMethod(
            "ComputeEnsembleStd",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double std = (double)computeStd.Invoke(null,
        [
            new[] { 1f },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            new[] { 0.0 },
            new[] { new[] { 0.0 }, new[] { 0.0 } },
            new[] { new[] { 0.0 } },
            1,
            null!,
            new[] { 1, 1 },
            new[] { ElmActivation.Sigmoid },
            null!,
            null!,
            0.0,
        ])!;

        Assert.Equal(0.0, std, precision: 6);
    }

    [Fact]
    public void ComputeEnsembleStd_Uses_Original_LearnerWeight_Indices_When_Invalid_Learners_Are_Skipped()
    {
        MethodInfo computeStd = typeof(ElmModelTrainer).GetMethod(
            "ComputeEnsembleStd",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double std = (double)computeStd.Invoke(null,
        [
            new[] { 1f },
            new[] { Array.Empty<double>(), new[] { 0.0 }, new[] { 0.0 } },
            new[] { 0.0, 0.0, 0.0 },
            new[] { Array.Empty<double>(), new[] { 0.0 }, new[] { 0.0 } },
            new[] { Array.Empty<double>(), new[] { 0.0 }, new[] { 0.0 } },
            1,
            null!,
            new[] { 1, 1, 1 },
            new[] { ElmActivation.Sigmoid, ElmActivation.Sigmoid, ElmActivation.Sigmoid },
            new[] { 10.0, 1.0, 1.0 },
            null!,
            0.0,
        ])!;

        Assert.Equal(0.0, std, precision: 6);
    }

    [Fact]
    public void ComputeEnsembleStd_Averages_Only_Valid_Learners_When_No_Weights_Are_Provided()
    {
        MethodInfo computeStd = typeof(ElmModelTrainer).GetMethod(
            "ComputeEnsembleStd",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        double std = (double)computeStd.Invoke(null,
        [
            new[] { 1f },
            new[] { Array.Empty<double>(), new[] { 0.0 }, new[] { 0.0 } },
            new[] { 0.0, 0.0, 0.0 },
            new[] { Array.Empty<double>(), new[] { 0.0 }, new[] { 0.0 } },
            new[] { Array.Empty<double>(), new[] { 0.0 }, new[] { 0.0 } },
            1,
            null!,
            new[] { 1, 1, 1 },
            new[] { ElmActivation.Sigmoid, ElmActivation.Sigmoid, ElmActivation.Sigmoid },
            null!,
            null!,
            0.0,
        ])!;

        Assert.Equal(0.0, std, precision: 6);
    }

    [Fact]
    public void UpdateOnline_Returns_False_When_InverseGram_Metadata_Is_Missing()
    {
        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        var snapshot = new ModelSnapshot
        {
            Weights = [[0.0]],
            Biases = [0.0],
            ElmInputWeights = [[1.0]],
            ElmInputBiases = [[0.0]],
            ElmInverseGram = null,
            Means = [0f],
            Stds = [1f],
        };

        bool updated = trainer.UpdateOnline(snapshot, new TrainingSample([1f], 1, 1f));

        Assert.False(updated);
        Assert.Equal(0, snapshot.TrainSamples);
    }

    [Fact]
    public void BiasedBootstrap_Clamps_Mismatched_Weight_Arrays_To_Training_Count()
    {
        var train = new List<TrainingSample>
        {
            new([1f], 1, 1f),
            new([2f], 0, 1f),
        };

        var bootstrap = ElmBootstrapHelper.BiasedBootstrap(
            train,
            temporalWeights: [0.1, 0.2, 0.7],
            count: 20,
            seed: 7);

        Assert.Equal(20, bootstrap.Count);
        Assert.All(bootstrap, sample => Assert.Contains(sample, train));
    }

    [Fact]
    public void ReplayBootstrapIndices_Clamps_Mismatched_Weight_Arrays_To_Training_Count()
    {
        var train = new List<TrainingSample>
        {
            new([1f], 1, 1f),
            new([2f], 0, 1f),
        };

        var indices = ElmBootstrapHelper.ReplayBootstrapIndices(
            train,
            temporalWeights: [0.1, 0.2, 0.7],
            count: 20,
            seed: 7);

        Assert.All(indices, index => Assert.InRange(index, 0, train.Count - 1));
    }

    [Fact]
    public void BiasedBootstrap_Assigns_Default_Weight_To_Missing_Tail_Entries()
    {
        var train = new List<TrainingSample>
        {
            new([1f], 1, 1f),
            new([2f], 1, 1f),
        };

        var bootstrap = ElmBootstrapHelper.BiasedBootstrap(
            train,
            temporalWeights: [0.2],
            count: 50,
            seed: 7);

        Assert.Contains(train[1], bootstrap);
    }

    [Fact]
    public void SanitizeLearnerOutputs_Also_Cleans_Pruned_Replacement_Learners()
    {
        MethodInfo sanitize = typeof(ElmModelTrainer).GetMethod(
            "SanitizeLearnerOutputs",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var trainer = new ElmModelTrainer(NullLogger<ElmModelTrainer>.Instance);
        double[][] weights = [[double.NaN], [1.0]];
        double[] biases = [1.0, 2.0];

        int sanitized = (int)sanitize.Invoke(trainer, [weights, biases, "test"])!;

        Assert.Equal(1, sanitized);
        Assert.Equal([0.0], weights[0]);
        Assert.Equal(0.0, biases[0], precision: 6);
        Assert.Equal([1.0], weights[1]);
        Assert.Equal(2.0, biases[1], precision: 6);
    }

    private sealed class PrecisionComparer(int precision) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= Math.Pow(10, -precision);

        public int GetHashCode(double obj) => 0;
    }
}
