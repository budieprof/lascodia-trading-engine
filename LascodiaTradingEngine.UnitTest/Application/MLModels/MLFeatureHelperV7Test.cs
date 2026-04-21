using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

public class MLFeatureHelperV7Test
{
    [Fact]
    public void FeatureCountV7_Equals_V6_Plus_CpcEmbeddingBlockSize()
    {
        Assert.Equal(
            MLFeatureHelper.FeatureCountV6 + MLFeatureHelper.CpcEmbeddingBlockSize,
            MLFeatureHelper.FeatureCountV7);
    }

    [Fact]
    public void CpcEmbeddingBlockSize_Is_Within_Cap()
    {
        Assert.True(MLFeatureHelper.FeatureCountV7 <= MLFeatureHelper.MaxAllowedFeatureCount);
    }

    [Fact]
    public void BuildFeatureVectorV7_Preserves_V6_Prefix()
    {
        var v6 = new float[MLFeatureHelper.FeatureCountV6];
        for (int i = 0; i < v6.Length; i++) v6[i] = i * 0.01f;

        var cpc = new float[MLFeatureHelper.CpcEmbeddingBlockSize];
        for (int i = 0; i < cpc.Length; i++) cpc[i] = 0.5f + i;

        var v7 = MLFeatureHelper.BuildFeatureVectorV7(v6, cpc);

        Assert.Equal(MLFeatureHelper.FeatureCountV7, v7.Length);
        for (int i = 0; i < MLFeatureHelper.FeatureCountV6; i++)
            Assert.Equal(v6[i], v7[i]);
    }

    [Fact]
    public void BuildFeatureVectorV7_Appends_Sanitised_Embedding_Block()
    {
        var v6 = new float[MLFeatureHelper.FeatureCountV6];
        var cpc = new float[MLFeatureHelper.CpcEmbeddingBlockSize];
        cpc[0] = float.NaN;
        cpc[1] = float.PositiveInfinity;
        cpc[2] = 50f;   // out of sane range; sanitiser clamps to [-5, 5]
        cpc[3] = -50f;
        cpc[4] = 0.3f;

        var v7 = MLFeatureHelper.BuildFeatureVectorV7(v6, cpc);

        Assert.Equal(0f, v7[MLFeatureHelper.FeatureCountV6 + 0]);
        Assert.Equal(0f, v7[MLFeatureHelper.FeatureCountV6 + 1]);
        Assert.InRange(v7[MLFeatureHelper.FeatureCountV6 + 2], -5f, 5f);
        Assert.InRange(v7[MLFeatureHelper.FeatureCountV6 + 3], -5f, 5f);
        Assert.Equal(0.3f, v7[MLFeatureHelper.FeatureCountV6 + 4]);
    }

    [Fact]
    public void BuildFeatureVectorV7_Zero_Fills_Embedding_When_Null()
    {
        var v6 = new float[MLFeatureHelper.FeatureCountV6];
        for (int i = 0; i < v6.Length; i++) v6[i] = 1f;

        var v7 = MLFeatureHelper.BuildFeatureVectorV7(v6, cpcEmbedding: null);

        Assert.Equal(MLFeatureHelper.FeatureCountV7, v7.Length);
        for (int i = MLFeatureHelper.FeatureCountV6; i < MLFeatureHelper.FeatureCountV7; i++)
            Assert.Equal(0f, v7[i]);
    }

    [Fact]
    public void BuildFeatureVectorV7_Zero_Fills_Tail_When_Embedding_Is_Too_Short()
    {
        var v6 = new float[MLFeatureHelper.FeatureCountV6];
        var cpc = new float[4]; // shorter than CpcEmbeddingBlockSize
        cpc[0] = 0.1f;
        cpc[1] = 0.2f;
        cpc[2] = 0.3f;
        cpc[3] = 0.4f;

        var v7 = MLFeatureHelper.BuildFeatureVectorV7(v6, cpc);

        for (int i = 0; i < 4; i++)
            Assert.Equal(cpc[i], v7[MLFeatureHelper.FeatureCountV6 + i]);
        for (int i = 4; i < MLFeatureHelper.CpcEmbeddingBlockSize; i++)
            Assert.Equal(0f, v7[MLFeatureHelper.FeatureCountV6 + i]);
    }

    [Fact]
    public void BuildFeatureVectorV7_Throws_When_V6_Raw_Has_Wrong_Length()
    {
        var badV6 = new float[MLFeatureHelper.FeatureCountV6 - 1];
        Assert.Throws<InvalidOperationException>(() =>
            MLFeatureHelper.BuildFeatureVectorV7(badV6, cpcEmbedding: null));
    }
}
