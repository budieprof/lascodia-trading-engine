using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class ModelSnapshotFeatureImportanceExtractorTest
{
    [Fact]
    public void Extract_Prefers_Tcn_Channel_Scores()
    {
        var snapshot = new ModelSnapshot
        {
            Type = "TCN",
            Features = ["Raw0"],
            FeatureImportanceScores = [0.1],
            TcnChannelNames = ["Close", "Atr"],
            TcnChannelImportanceScores = [0.8, 0.2],
        };

        var result = ModelSnapshotFeatureImportanceExtractor.Extract(snapshot);

        Assert.Equal(ModelSnapshotFeatureImportanceExtractor.SourceTcnChannelScores, result.Source);
        Assert.Equal(2, result.Importance.Count);
        Assert.Equal(0.8, result.Importance["Close"]);
        Assert.Equal(0.2, result.Importance["Atr"]);
    }

    [Fact]
    public void Extract_Sanitizes_NonFinite_Importance_Scores()
    {
        var snapshot = new ModelSnapshot
        {
            Features = ["Rsi", "Atr", "Macd"],
            FeatureImportanceScores = [0.5, double.NaN, double.PositiveInfinity],
        };

        var result = ModelSnapshotFeatureImportanceExtractor.Extract(snapshot);

        Assert.Equal(ModelSnapshotFeatureImportanceExtractor.SourceFeatureImportanceScores, result.Source);
        Assert.Single(result.Importance);
        Assert.Equal(2, result.InvalidValueCount);
        Assert.Equal(0.5, result.Importance["Rsi"]);
    }

    [Fact]
    public void Extract_Falls_Back_To_Averaged_Absolute_Weights()
    {
        var snapshot = new ModelSnapshot
        {
            Features = ["Rsi", "Atr"],
            Weights =
            [
                [1.0, -3.0],
                [-5.0, 7.0],
            ],
        };

        var result = ModelSnapshotFeatureImportanceExtractor.Extract(snapshot);

        Assert.Equal(ModelSnapshotFeatureImportanceExtractor.SourceWeightFallback, result.Source);
        Assert.Equal(3.0, result.Importance["Rsi"]);
        Assert.Equal(5.0, result.Importance["Atr"]);
    }
}
