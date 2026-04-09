using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Text.Json;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class MLSignalScorerTests
{
    // ────────────────────────────────────────────────────────────────────────
    //  IsTcnModel
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsTcnModel_Returns_True_For_Valid_TCN_Snapshot()
    {
        var snap = new ModelSnapshot
        {
            Type = "TCN",
            ConvWeightsJson = "{\"ConvW\":[]}",
            Version = "5.0"
        };

        Assert.True(new TcnInferenceEngine().CanHandle(snap));
    }

    [Fact]
    public void IsTcnModel_Returns_False_When_ConvWeightsJson_Missing()
    {
        var snap = new ModelSnapshot { Type = "TCN", Version = "5.0" };

        Assert.False(new TcnInferenceEngine().CanHandle(snap));
    }

    [Fact]
    public void IsTcnModel_Returns_False_When_Version_Below_5()
    {
        var snap = new ModelSnapshot
        {
            Type = "TCN",
            ConvWeightsJson = "{\"ConvW\":[]}",
            Version = "4.9"
        };

        Assert.False(new TcnInferenceEngine().CanHandle(snap));
    }

    [Fact]
    public void IsTcnModel_Returns_False_For_Non_TCN_Type()
    {
        var snap = new ModelSnapshot
        {
            Type = "quantilerf",
            ConvWeightsJson = "{\"ConvW\":[]}",
            Version = "5.0"
        };

        Assert.False(new TcnInferenceEngine().CanHandle(snap));
    }

    [Fact]
    public void IsTcnModel_Uses_Semantic_Version_Parsing()
    {
        var snap = new ModelSnapshot
        {
            Type = "TCN",
            ConvWeightsJson = "{\"ConvW\":[]}",
            Version = "10.0"
        };

        Assert.True(new TcnInferenceEngine().CanHandle(snap));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  IsQrfModel
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsQrfModel_Returns_True_For_QuantileRF_With_Trees()
    {
        var snap = new ModelSnapshot
        {
            Type = "quantilerf",
            GbmTreesJson = "[{\"Nodes\":[]}]"
        };

        Assert.True(new QrfInferenceEngine(null!).CanHandle(snap));
    }

    [Fact]
    public void IsQrfModel_Returns_False_When_No_Trees()
    {
        var snap = new ModelSnapshot { Type = "quantilerf" };

        Assert.False(new QrfInferenceEngine(null!).CanHandle(snap));
    }

    [Fact]
    public void IsQrfModel_Returns_False_For_Non_QRF_Type()
    {
        var snap = new ModelSnapshot
        {
            Type = "TCN",
            GbmTreesJson = "[{\"Nodes\":[]}]"
        };

        Assert.False(new QrfInferenceEngine(null!).CanHandle(snap));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ApplyBasicCalibration
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyBasicCalibration_With_Default_Platt_Returns_Reasonable_Probability()
    {
        var snap = new ModelSnapshot { PlattA = 1.0, PlattB = 0.0 };

        double result = InferenceHelpers.ApplyBasicCalibration(0.7, snap);

        // With PlattA=1, PlattB=0: sigmoid(logit(0.7)) ≈ 0.7 (identity)
        Assert.InRange(result, 0.69, 0.71);
    }

    [Fact]
    public void ApplyBasicCalibration_Uses_Temperature_Scaling_When_Set()
    {
        var snap = new ModelSnapshot { TemperatureScale = 2.0 };

        double result = InferenceHelpers.ApplyBasicCalibration(0.9, snap);

        // Temperature > 1 softens probabilities toward 0.5
        Assert.True(result < 0.9, "Temperature scaling should soften extreme probabilities");
        Assert.True(result > 0.5, "Should still be above 0.5 for input > 0.5");
    }

    [Fact]
    public void ApplyBasicCalibration_Prefers_Temperature_Over_Platt()
    {
        var snap = new ModelSnapshot
        {
            TemperatureScale = 2.0,
            PlattA = 0.5,
            PlattB = -0.1
        };

        double withTemp = InferenceHelpers.ApplyBasicCalibration(0.8, snap);

        // Now remove temperature, should use Platt instead
        snap.TemperatureScale = 0.0;
        double withPlatt = InferenceHelpers.ApplyBasicCalibration(0.8, snap);

        Assert.NotEqual(withTemp, withPlatt);
    }

    [Fact]
    public void ApplyBasicCalibration_Applies_Isotonic_When_Breakpoints_Present()
    {
        var snap = new ModelSnapshot
        {
            PlattA = 1.0,
            PlattB = 0.0,
            // Isotonic breakpoints need at least 4 values: [x0, y0, x1, y1, ...]
            IsotonicBreakpoints = [0.0, 0.0, 0.3, 0.2, 0.7, 0.8, 1.0, 1.0]
        };

        double result = InferenceHelpers.ApplyBasicCalibration(0.5, snap);

        // With isotonic calibration applied, result will differ from raw Platt
        Assert.InRange(result, 0.0, 1.0);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  AggregateProbs
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AggregateProbs_Falls_Back_To_Average_When_No_Weights()
    {
        double[] probs = [0.2, 0.4, 0.6, 0.8];

        double result = InferenceHelpers.AggregateProbs(probs, 4, null, 0.0, null, null, null);

        Assert.Equal(0.5, result, precision: 10);
    }

    [Fact]
    public void AggregateProbs_Uses_MetaWeights_When_Count_Matches()
    {
        double[] probs = [0.3, 0.7];
        double[] metaW = [1.0, 1.0];

        double result = InferenceHelpers.AggregateProbs(probs, 2, metaW, 0.0, null, null, null);

        // Meta-learner applies sigmoid(metaBias + sum(w_k * p_k))
        // sigmoid(0 + 1.0*0.3 + 1.0*0.7) = sigmoid(1.0) ≈ 0.7311
        Assert.InRange(result, 0.72, 0.74);
    }

    [Fact]
    public void AggregateProbs_Uses_GES_When_No_MetaWeights()
    {
        double[] probs = [0.2, 0.8];
        double[] ges   = [3.0, 1.0]; // heavily favour first learner

        double result = InferenceHelpers.AggregateProbs(probs, 2, null, 0.0, ges, null, null);

        // Weighted avg: (3*0.2 + 1*0.8) / 4 = 1.4/4 = 0.35
        Assert.Equal(0.35, result, precision: 10);
    }

    [Fact]
    public void AggregateProbs_Uses_CalAccuracies_When_No_GES()
    {
        double[] probs = [0.3, 0.7];
        double[] calAcc = [0.9, 0.5]; // first learner much more accurate

        double result = InferenceHelpers.AggregateProbs(probs, 2, null, 0.0, null, null, calAcc);

        // Softmax-weighted by accuracy: first learner gets higher weight
        Assert.True(result < 0.5, "Should skew toward first learner (0.3) since it has higher accuracy");
    }

    [Fact]
    public void AggregateProbs_Priority_MetaWeights_Over_GES_Over_CalAccuracies()
    {
        double[] probs  = [0.3, 0.7];
        double[] metaW  = [1.0, 1.0];
        double[] ges    = [3.0, 1.0];
        double[] calAcc = [0.9, 0.5];

        // Meta should win when all are present
        double withAll  = InferenceHelpers.AggregateProbs(probs, 2, metaW, 0.0, ges, null, calAcc);
        double metaOnly = InferenceHelpers.AggregateProbs(probs, 2, metaW, 0.0, null, null, null);

        Assert.Equal(metaOnly, withAll, precision: 10);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  StandardiseFeatures
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StandardiseFeatures_Applies_ZScore_Correctly()
    {
        float[] raw   = [10f, 20f, 30f];
        float[] means = [10f, 20f, 30f];
        float[] stds  = [2f,  5f,  10f];

        float[] result = MLSignalScorer.StandardiseFeatures(raw, means, stds, 3);

        // (raw - mean) / std = 0 for all
        Assert.Equal(0f, result[0]);
        Assert.Equal(0f, result[1]);
        Assert.Equal(0f, result[2]);
    }

    [Fact]
    public void StandardiseFeatures_Handles_Near_Zero_Std()
    {
        float[] raw   = [5f];
        float[] means = [3f];
        float[] stds  = [0f]; // zero std — should default to 1

        float[] result = MLSignalScorer.StandardiseFeatures(raw, means, stds, 1);

        // (5 - 3) / 1 = 2
        Assert.Equal(2f, result[0]);
    }

    [Fact]
    public void StandardiseFeatures_Handles_Mismatched_Lengths()
    {
        float[] raw   = [10f, 20f];
        float[] means = [5f]; // shorter than featureCount
        float[] stds  = [2f]; // shorter than featureCount

        float[] result = MLSignalScorer.StandardiseFeatures(raw, means, stds, 2);

        // First feature: (10 - 5) / 2 = 2.5
        Assert.Equal(2.5f, result[0]);
        // Second feature: mean defaults to 0, std defaults to 1 → (20 - 0) / 1 = 20
        Assert.Equal(20f, result[1]);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  RunTcnForwardPass
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{}")]           // valid JSON but missing ConvW/HeadW/HeadB
    [InlineData("{\"ConvW\":null,\"HeadW\":null,\"HeadB\":null}")]
    public void RunTcnForwardPass_Returns_Null_For_Invalid_ConvWeightsJson(string json)
    {
        var snap = new ModelSnapshot
        {
            Type = "TCN",
            ConvWeightsJson = json,
            Version = "5.0"
        };

        var method = typeof(TcnInferenceEngine).GetMethod(
            "RunTcnForwardPass",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [snap, new List<Candle>()]);
        Assert.Null(result);
    }

    [Fact]
    public void TcnInferenceEngine_RunInference_Applies_Channel_Mask_And_Returns_Magnitude()
    {
        var snapshot = CreateMinimalTcnSnapshot(channelMask: [false, true, true, true, true, true, true, true, true]);
        var candles = CreateCandleWindow();

        var result = new TcnInferenceEngine().RunInference(
            features: new float[MLFeatureHelper.FeatureCount],
            featureCount: MLFeatureHelper.FeatureCount,
            snapshot,
            candles,
            modelId: 1,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.True(result.HasValue);
        Assert.True(result.Value.Magnitude.HasValue);
        Assert.Equal(2.5, result.Value.Magnitude.Value, precision: 6);
        Assert.NotNull(result.Value.ModelSpaceValues);
        Assert.Equal(MLFeatureHelper.SequenceChannelCount, result.Value.ModelSpaceValues!.Length);
        Assert.Equal(0.0, result.Value.ModelSpaceValues[0], precision: 6);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ApplyBasicCalibration — edge cases
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void ApplyBasicCalibration_Output_Always_Between_0_And_1(double input)
    {
        var snap = new ModelSnapshot { PlattA = 2.0, PlattB = -0.5 };

        double result = InferenceHelpers.ApplyBasicCalibration(input, snap);

        Assert.InRange(result, 0.0, 1.0);
    }

    [Fact]
    public void ApplyBasicCalibration_Identity_With_Default_Platt()
    {
        // PlattA=1, PlattB=0 → sigmoid(logit(p)) = p (identity)
        var snap = new ModelSnapshot { PlattA = 1.0, PlattB = 0.0 };

        double result = InferenceHelpers.ApplyBasicCalibration(0.3, snap);

        Assert.Equal(0.3, result, precision: 5);
    }

    [Fact]
    public void ApplyBasicCalibration_Treats_NonFinite_Raw_Probability_As_Neutral()
    {
        var snap = new ModelSnapshot { PlattA = 2.0, PlattB = -1.0 };

        double result = InferenceHelpers.ApplyBasicCalibration(double.NaN, snap);

        Assert.InRange(result, 0.26, 0.27);
    }

    [Fact]
    public void ApplyDeployedCalibration_Sanitises_Malformed_Isotonic_And_NonFinite_Raw_Probability()
    {
        var snap = new ModelSnapshot
        {
            PlattA = 1.0,
            PlattB = 0.0,
            IsotonicBreakpoints = [double.NaN, 0.1, 0.25, -0.5, 0.75, 2.0, 0.5]
        };

        double result = InferenceHelpers.ApplyDeployedCalibration(double.NaN, snap);

        Assert.Equal(0.5, result, precision: 6);
    }

    [Fact]
    public void ApplyDeployedCalibration_Sanitises_NonFinite_Calibration_Parameters()
    {
        var snap = new ModelSnapshot
        {
            TemperatureScale = double.PositiveInfinity,
            PlattA = double.NaN,
            PlattB = double.NaN,
            PlattABuy = double.NaN,
            PlattBBuy = double.NaN,
            PlattASell = double.NaN,
            PlattBSell = double.NaN,
            AgeDecayLambda = double.PositiveInfinity,
            TrainedAtUtc = DateTime.UtcNow
        };

        double result = InferenceHelpers.ApplyDeployedCalibration(0.8, snap);

        Assert.Equal(0.8, result, precision: 6);
    }

    [Fact]
    public void ApplyDeployedCalibration_Ignores_Identity_Conditional_Branches()
    {
        var snap = new ModelSnapshot
        {
            PlattA = 2.0,
            PlattB = -0.75,
            PlattABuy = 1.0,
            PlattBBuy = 0.0,
            PlattASell = 1.0,
            PlattBSell = 0.0
        };

        double result = InferenceHelpers.ApplyDeployedCalibration(0.8, snap);
        double expected = InferenceHelpers.ApplyBasicCalibration(0.8, snap);

        Assert.Equal(expected, result, precision: 8);
    }

    [Fact]
    public void ApplyDeployedCalibration_Applies_Temperature_Conditional_Then_Isotonic_In_Order()
    {
        var snap = new ModelSnapshot
        {
            TemperatureScale = 2.0,
            PlattA = 0.25,
            PlattB = -1.0,
            PlattABuy = 1.5,
            PlattBBuy = -0.1,
            PlattASell = 0.5,
            PlattBSell = 0.2,
            IsotonicBreakpoints = [0.0, 0.0, 0.5, 0.4, 1.0, 0.9]
        };

        double result = InferenceHelpers.ApplyDeployedCalibration(0.8, snap);
        double rawLogit = Math.Log(0.8 / 0.2);
        double branchProb = 1.0 / (1.0 + Math.Exp(-(1.5 * rawLogit - 0.1)));
        double expected = 0.4 + ((branchProb - 0.5) / 0.5) * 0.5;

        Assert.Equal(expected, result, 8);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  AggregateProbs — edge cases
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AggregateProbs_Ignores_MetaWeights_When_Count_Mismatches()
    {
        double[] probs  = [0.3, 0.7];
        double[] metaW  = [1.0]; // length 1, count is 2 → mismatch

        double result = InferenceHelpers.AggregateProbs(probs, 2, metaW, 0.0, null, null, null);

        // Should fall through to average since metaW.Length != count
        Assert.Equal(0.5, result, precision: 10);
    }

    [Fact]
    public void AggregateProbs_MetaBias_Shifts_Output()
    {
        double[] probs = [0.5, 0.5];
        double[] metaW = [0.0, 0.0]; // zero weights

        double withoutBias = InferenceHelpers.AggregateProbs(probs, 2, metaW, 0.0, null, null, null);
        double withBias    = InferenceHelpers.AggregateProbs(probs, 2, metaW, 2.0, null, null, null);

        // sigmoid(0) = 0.5, sigmoid(2) ≈ 0.88
        Assert.Equal(0.5, withoutBias, precision: 5);
        Assert.True(withBias > 0.85);
    }

    [Fact]
    public void AggregateProbs_Treats_NonFinite_Probabilities_As_Neutral()
    {
        double[] probs = [double.NaN, 1.0];

        double result = InferenceHelpers.AggregateProbs(probs, 2, null, 0.0, null, null, null);

        Assert.Equal(0.75, result, precision: 6);
    }

    [Fact]
    public void AggregateProbs_Ignores_NonFinite_Ges_Weights()
    {
        double[] probs = [0.2, 0.8];
        double[] ges = [double.NaN, 1.0];

        double result = InferenceHelpers.AggregateProbs(probs, 2, null, 0.0, ges, null, null);

        Assert.Equal(0.8, result, precision: 6);
    }

    [Fact]
    public void AggregateProbs_Ignores_NonFinite_Meta_Weights()
    {
        double[] probs = [0.2, 0.8];
        double[] metaW = [double.NaN, double.NaN];

        double result = InferenceHelpers.AggregateProbs(probs, 2, metaW, double.NaN, null, null, null);

        Assert.Equal(0.5, result, precision: 6);
    }

    [Fact]
    public void EnsembleProb_Computes_Std_From_Learner_Mean_Not_Aggregated_Output()
    {
        double[][] weights = [[0.0], [0.0]];
        double[] biases =
        [
            MLFeatureHelper.Logit(0.2),
            MLFeatureHelper.Logit(0.8)
        ];
        double[] metaW = [5.0, 0.0];

        var (_, stdProb) = EnsembleInferenceEngine.EnsembleProb(
            [0f],
            weights,
            biases,
            featureCount: 1,
            subsets: null,
            metaWeights: metaW,
            metaBias: 0.0);

        Assert.Equal(Math.Sqrt(0.18), stdProb, precision: 6);
    }

    [Fact]
    public void EnsembleProb_Excludes_Inactive_Pruned_Learners_From_Std()
    {
        var (_, stdProb) = EnsembleInferenceEngine.EnsembleProb(
            [0f],
            weights: [[0.0], [0.0]],
            biases:
            [
                MLFeatureHelper.Logit(0.2),
                0.0
            ],
            featureCount: 1);

        Assert.Equal(0.0, stdProb, precision: 6);
    }

    [Fact]
    public void EnsembleProb_Uses_Polynomial_Inputs_For_Mlp_Learners()
    {
        var (prob, _) = EnsembleInferenceEngine.EnsembleProb(
            [2f, 3f, 0f, 0f, 0f],
            weights: [[1.0]],
            biases: [0.0],
            featureCount: 5,
            subsets: [[5]],
            mlpHiddenW: [[1.0]],
            mlpHiddenB: [[0.0]],
            mlpHiddenDim: 1);

        Assert.True(prob > 0.99);
    }

    [Fact]
    public void ComputeShapContributionsJson_Uses_Projected_Mlp_Weights()
    {
        string? json = ScoringEnrichmentCalculator.ComputeShapContributionsJson(
            [1f, 2f, 3f, 4f],
            [[2.0, 3.0]],
            [[1, 3]],
            ["f0", "f1", "f2", "f3"],
            4,
            [],
            [[5.0, 7.0, 11.0, 13.0]],
            2);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("f3", doc.RootElement[0].GetProperty("Feature").GetString());
        Assert.Equal(212.0, doc.RootElement[0].GetProperty("Value").GetDouble(), precision: 6);
    }

    [Fact]
    public void ComputeCounterfactualJson_Uses_Projected_Mlp_Weights()
    {
        string? json = ScoringEnrichmentCalculator.ComputeCounterfactualJson(
            [1f, 2f, 3f, 4f],
            [[2.0, 3.0]],
            [[1, 3]],
            ["f0", "f1", "f2", "f3"],
            4,
            calibP: 0.6,
            threshold: 0.5,
            mlpHiddenWeights: [[5.0, 7.0, 11.0, 13.0]],
            mlpHiddenDim: 2);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.True(doc.RootElement.TryGetProperty("f3", out _));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  StandardiseFeatures — edge cases
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StandardiseFeatures_Pads_Output_When_FeatureCount_Exceeds_Raw()
    {
        float[] raw   = [5f];
        float[] means = [5f, 10f, 15f];
        float[] stds  = [1f, 2f, 3f];

        float[] result = MLSignalScorer.StandardiseFeatures(raw, means, stds, 3);

        Assert.Equal(3, result.Length);
        Assert.Equal(0f, result[0]); // (5-5)/1 = 0
        Assert.Equal(0f, result[1]); // raw[1] doesn't exist → stays 0
        Assert.Equal(0f, result[2]); // raw[2] doesn't exist → stays 0
    }

    [Fact]
    public void PredictSnapshotMagnitude_Uses_Bagged_Polynomial_Terms_When_Present()
    {
        var snap = new ModelSnapshot
        {
            MagWeights = [1.0, 2.0, 10.0],
            MagBias = 0.5,
        };

        double magnitude = MLSignalScorer.PredictSnapshotMagnitude([2f, 3f], 2, snap);

        Assert.Equal(68.5, magnitude, precision: 6);
    }

    private static ModelSnapshot CreateMinimalTcnSnapshot(bool[]? channelMask = null)
    {
        var weights = new TcnModelTrainer.TcnSnapshotWeights
        {
            ConvW = [new double[MLFeatureHelper.SequenceChannelCount * 3]],
            ConvB = [new[] { 1.0 }],
            HeadW = [0.0, 1.0],
            HeadB = [0.0, 0.0],
            MagHeadW = [2.0],
            MagHeadB = 0.5,
            ResW = [null],
            ChannelIn = MLFeatureHelper.SequenceChannelCount,
            TimeSteps = MLFeatureHelper.LookbackWindow,
            Filters = 1,
            UseLayerNorm = false,
            Activation = (int)TcnActivation.Relu,
            UseAttentionPooling = false,
        };

        var seqMeans = new float[MLFeatureHelper.SequenceChannelCount];
        var seqStds = new float[MLFeatureHelper.SequenceChannelCount];
        Array.Fill(seqStds, 1f);

        return new ModelSnapshot
        {
            Type = "TCN",
            Version = "5.0",
            ConvWeightsJson = JsonSerializer.Serialize(weights),
            SeqMeans = seqMeans,
            SeqStds = seqStds,
            TcnActiveChannelMask = channelMask ?? Enumerable.Repeat(true, MLFeatureHelper.SequenceChannelCount).ToArray(),
            TcnChannelNames = MLFeatureHelper.SequenceChannelNames,
            TcnChannelImportanceScores = Enumerable.Repeat(1.0, MLFeatureHelper.SequenceChannelCount).ToArray(),
        };
    }

    private static List<Candle> CreateCandleWindow()
    {
        var candles = new List<Candle>(MLFeatureHelper.LookbackWindow);
        DateTime start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < MLFeatureHelper.LookbackWindow; i++)
        {
            decimal open = 100m + i;
            decimal close = open + 0.5m + (i % 3) * 0.1m;
            candles.Add(new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Open = open,
                High = close + 0.25m,
                Low = open - 0.25m,
                Close = close,
                Volume = 1000m + (i * 10m),
                Timestamp = start.AddHours(i),
                IsClosed = true,
            });
        }

        return candles;
    }
}
