using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

/// <summary>Unit tests for the ML feature engineering and training engine components.</summary>
public class MLTrainingEngineTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<Candle> MakeCandles(int count, decimal startClose = 1.1000m)
    {
        var candles = new List<Candle>(count);
        var rng     = new Random(42);
        var ts      = DateTime.UtcNow.AddHours(-count);
        decimal close = startClose;

        for (int i = 0; i < count; i++)
        {
            decimal delta = (decimal)(rng.NextDouble() * 0.002 - 0.001);
            decimal open  = close;
            close += delta;
            decimal high  = Math.Max(open, close) + (decimal)(rng.NextDouble() * 0.0005);
            decimal low   = Math.Min(open, close) - (decimal)(rng.NextDouble() * 0.0005);

            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = open,
                High      = high,
                Low       = low,
                Close     = close,
                Volume    = (decimal)(rng.NextDouble() * 1000 + 100),
                Timestamp = ts.AddHours(i),
                IsClosed  = true,
            });
        }

        return candles;
    }

    private static TrainingHyperparams DefaultHp() => new(
        K:                         3,
        LearningRate:              0.05,
        L2Lambda:                  0.001,
        MaxEpochs:                 50,
        EarlyStoppingPatience:     10,
        MinAccuracyToPromote:      0.50,
        MinExpectedValue:          -99,
        MaxBrierScore:             1.0,
        MinSharpeRatio:            -99,
        MinSamples:                30,
        ShadowRequiredTrades:      10,
        ShadowExpiryDays:          30,
        WalkForwardFolds:          2,
        EmbargoBarCount:           5,
        TrainingTimeoutMinutes:    5,
        TemporalDecayLambda:       2.0,
        DriftWindowDays:           14,
        DriftMinPredictions:       10,
        DriftAccuracyThreshold:    0.45,
        MaxWalkForwardStdDev:      1.0,    // disabled in tests
        LabelSmoothing:            0.0,    // disabled in tests
        MinFeatureImportance:      0.0,    // disabled in tests
        EnableRegimeSpecificModels: false,
        FeatureSampleRatio:        0.0,    // disabled in tests
        MaxEce:                    0.0,    // disabled in tests
        UseTripleBarrier:          false,
        TripleBarrierProfitAtrMult: 1.5,
        TripleBarrierStopAtrMult:   1.0,
        TripleBarrierHorizonBars:   24,
        NoiseSigma:                0.0,   // disabled in tests
        FpCostWeight:              0.5,   // symmetric (disabled) in tests
        NclLambda:                 0.0,   // disabled in tests
        FracDiffD:                 0.0,
        MaxFoldDrawdown:           1.0,   // gate disabled in tests
        MinFoldCurveSharpe:        -99.0, // gate disabled in tests
        PolyLearnerFraction:       0.0,   // disabled in tests
        PurgeHorizonBars:          0,     // disabled in tests
        NoiseCorrectionThreshold:  0.0,   // disabled in tests
        MaxLearnerCorrelation:     1.0,   // disabled in tests
        SwaStartEpoch:             0,     // SWA disabled in tests
        SwaFrequency:              1,
        MixupAlpha:                0.0,   // Mixup disabled in tests
        EnableGreedyEnsembleSelection: false,
        MaxGradNorm:               0.0,   // gradient clipping disabled in tests
        AtrLabelSensitivity:       0.0,   // soft labels disabled in tests
        ShadowMinZScore:           0.0,   // z-score gate disabled in tests
        L1Lambda:                  0.0,   // disabled in tests
        MagnitudeQuantileTau:      0.0,   // disabled in tests
        MagLossWeight:             0.0,   // disabled in tests
        DensityRatioWindowDays:    0,     // disabled in tests
        DurbinWatsonThreshold:            0.0,   // disabled in tests
        AdaptiveLrDecayFactor:            0.0,   // disabled in tests
        OobPruningEnabled:                false,
        MutualInfoRedundancyThreshold:    0.0,   // disabled in tests
        MinSharpeTrendSlope:              -99.0, // disabled in tests
        FitTemperatureScale:              false, // disabled in tests
        MinBrierSkillScore:               -1.0,  // disabled in tests
        RecalibrationDecayLambda:         0.0,   // disabled in tests
        MaxEnsembleDiversity:             1.0,   // disabled in tests
        UseSymmetricCE:                   false, // disabled in tests
        SymmetricCeAlpha:                 0.0,   // disabled in tests
        DiversityLambda:                  0.0,   // disabled in tests
        UseAdaptiveLabelSmoothing:        false, // disabled in tests
        AgeDecayLambda:                   0.0,   // disabled in tests
        UseCovariateShiftWeights:         false, // disabled in tests
        MaxBadFoldFraction:               1.0,   // gate disabled in tests
        MinQualityRetentionRatio:         0.0,   // gate disabled in tests
        MultiTaskMagnitudeWeight:         0.3,   // default; not used in tests
        CurriculumEasyFraction:           0.3,   // disabled in tests
        SelfDistillTemp:                  3.0,   // disabled in tests
        FgsmEpsilon:                      0.01); // disabled in tests

    // ── MLFeatureHelper.BuildTrainingSamples tests ────────────────────────────

    [Fact]
    public void BuildTrainingSamples_ReturnsSamplesWithCorrectFeatureLength()
    {
        var candles = MakeCandles(200);
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);

        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.Equal(MLFeatureHelper.FeatureCount, s.Features.Length));
    }

    [Fact]
    public void BuildTrainingSamples_DirectionIsZeroOrOne()
    {
        var candles = MakeCandles(200);
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);

        // 1 = price moved up (Buy), 0 = price moved down (Sell)
        Assert.All(samples, s => Assert.True(s.Direction == 1 || s.Direction == 0));
    }

    [Fact]
    public void BuildTrainingSamples_MagnitudeIsWithinClampBounds()
    {
        var candles = MakeCandles(200);
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);

        // Magnitude is ATR-normalised change, clamped to [-5, 5]
        Assert.All(samples, s => Assert.InRange(s.Magnitude, -5f, 5f));
    }

    [Fact]
    public void BuildTrainingSamples_CountRespectedLookbackConstraint()
    {
        var candles = MakeCandles(150);
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);

        Assert.True(samples.Count > 0);
        Assert.True(samples.Count <= candles.Count - MLFeatureHelper.LookbackWindow - 1);
    }

    [Fact]
    public void BuildTrainingSamples_WithCotLookup_PopulatesLastTwoFeatures()
    {
        var candles = MakeCandles(100);
        CotFeatureEntry lookup(DateTime _) => new(1.5f, -0.5f);

        var withCot    = MLFeatureHelper.BuildTrainingSamples(candles, lookup);
        var withoutCot = MLFeatureHelper.BuildTrainingSamples(candles, null);

        // Feature index 26 (CotBaseNetNorm) should differ between the two
        Assert.NotEqual(withCot[0].Features[26], withoutCot[0].Features[26]);
    }

    // ── Indicator tests ───────────────────────────────────────────────────────

    [Fact]
    public void CalculateRSI_ReturnsValueBetweenZeroAndHundred()
    {
        var closes = Enumerable.Range(0, 20)
            .Select(i => 1.10m + (decimal)i * 0.001m + (i % 3 == 0 ? -0.003m : 0))
            .ToList();

        double rsi = MLFeatureHelper.CalculateRSI(closes, 14);

        Assert.InRange(rsi, 0.0, 100.0);
    }

    [Fact]
    public void CalculateATR_ReturnsPositiveValue()
    {
        var candles = MakeCandles(20);
        double atr = MLFeatureHelper.CalculateATR(candles, 14);

        Assert.True(atr > 0);
    }

    [Fact]
    public void CalculateStochK_ReturnsValueBetweenZeroAndHundred()
    {
        var candles = MakeCandles(20);
        double stoch = MLFeatureHelper.CalculateStochK(candles);

        Assert.InRange(stoch, 0.0, 100.0);
    }

    [Fact]
    public void CalculateADX_ReturnsNonNegativeValue()
    {
        var candles = MakeCandles(30);
        double adx = MLFeatureHelper.CalculateADX(candles);

        Assert.True(adx >= 0);
    }

    [Fact]
    public void CalculateROC_AllPeriods_ReturnsBoundedValues()
    {
        var closes = Enumerable.Range(0, 30)
            .Select(i => 1.10m + (decimal)i * 0.001m)
            .ToList();

        foreach (int period in new[] { 3, 7, 14 })
        {
            float roc = MLFeatureHelper.CalculateROC(closes, period);
            Assert.InRange(roc, -3.1f, 3.1f);
        }
    }

    // ── Standardisation tests ─────────────────────────────────────────────────

    [Fact]
    public void ComputeStandardization_MeanIsApproximatelyZeroAfterStandardize()
    {
        var features = Enumerable.Range(1, 100)
            .Select(i => new float[] { i, i * 2f, i * 0.5f })
            .ToList();

        var (means, stds) = MLFeatureHelper.ComputeStandardization(features);
        var standardised  = features.Select(f => MLFeatureHelper.Standardize(f, means, stds)).ToList();

        for (int j = 0; j < 3; j++)
        {
            double mean = standardised.Average(f => f[j]);
            Assert.InRange(mean, -0.01, 0.01);
        }
    }

    [Fact]
    public void ComputeStandardization_StdIsApproximatelyOneAfterStandardize()
    {
        var rng      = new Random(0);
        var features = Enumerable.Range(0, 200)
            .Select(_ => new float[] { (float)rng.NextDouble() * 10 })
            .ToList();

        var (means, stds) = MLFeatureHelper.ComputeStandardization(features);
        var standardised  = features.Select(f => MLFeatureHelper.Standardize(f, means, stds)).ToList();

        double variance = standardised.Select(f => (double)f[0])
                                      .Select(v => v * v)
                                      .Average();
        Assert.InRange(variance, 0.9, 1.1);
    }

    // ── BaggedLogisticTrainer tests ───────────────────────────────────────────

    [Fact]
    public async Task BaggedLogisticTrainer_TrainAsync_ReturnsBytesAndMetrics()
    {
        var candles = MakeCandles(300);
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);
        Assert.True(samples.Count >= 30, "Need at least 30 samples for this test");

        var trainer = new BaggedLogisticTrainer(NullLogger<BaggedLogisticTrainer>.Instance);
        var result  = await trainer.TrainAsync(samples, DefaultHp(), ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy,   0.0, 1.0);
        Assert.InRange(result.FinalMetrics.BrierScore, 0.0, 1.0);
        Assert.InRange(result.FinalMetrics.F1,         0.0, 1.0);
    }

    [Fact]
    public async Task BaggedLogisticTrainer_ModelBytes_DeserialiseToValidSnapshot()
    {
        var candles = MakeCandles(300);
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);

        var trainer = new BaggedLogisticTrainer(NullLogger<BaggedLogisticTrainer>.Instance);
        var result  = await trainer.TrainAsync(samples, DefaultHp(), ct: CancellationToken.None);

        var snap = System.Text.Json.JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);

        Assert.NotNull(snap);
        Assert.Equal(3,                          snap!.BaseLearnersK);
        Assert.Equal(MLFeatureHelper.FeatureCount, snap.Features.Length);
        Assert.Equal(MLFeatureHelper.FeatureCount, snap.Means.Length);
        Assert.Equal(MLFeatureHelper.FeatureCount, snap.Stds.Length);
    }

    [Fact]
    public void ComputeTemporalWeights_SumToOne()
    {
        var weights = BaggedLogisticTrainer.ComputeTemporalWeights(100, 2.0);

        Assert.Equal(100, weights.Length);
        Assert.InRange(weights.Sum(), 0.9999, 1.0001);
    }

    [Fact]
    public void ComputeTemporalWeights_LastWeightGreaterThanFirst()
    {
        var weights = BaggedLogisticTrainer.ComputeTemporalWeights(50, 2.0);

        Assert.True(weights[^1] > weights[0],
            "Recent samples should have higher weight than older samples");
    }

    // ── Math helper tests ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0,  0.5)]
    [InlineData(1.0,  0.7310585786)]
    [InlineData(-1.0, 0.2689414214)]
    public void Sigmoid_MatchesKnownValues(double z, double expected)
    {
        double result = MLFeatureHelper.Sigmoid(z);
        Assert.InRange(result, expected - 0.001, expected + 0.001);
    }

    [Fact]
    public void Logit_IsInverseOfSigmoid()
    {
        double p     = 0.7;
        double logit = MLFeatureHelper.Logit(p);
        double backP = MLFeatureHelper.Sigmoid(logit);

        Assert.InRange(backP, p - 0.001, p + 0.001);
    }
}
