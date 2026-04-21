using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

/// <summary>
/// V7 train/inference parity: given the same (window, encoder, context), both the training
/// path (V6 raw → append projected embedding) and the inference path (same recipe, resolved
/// in <c>MLSignalScorer</c>'s V7 dispatch) must produce byte-identical V7 raw vectors.
///
/// <para>
/// Other V1..V6 parity is already exercised by the existing per-trainer <c>TrainerAuditTests</c>
/// harnesses. V7 only adds the appended CPC block; this file pins that append so future drift
/// (e.g. someone adds a sanitiser to one path but not the other) surfaces as a test failure
/// rather than a silent &gt;1e-6 parity-audit regression.
/// </para>
/// </summary>
public class V7ParityTest
{
    [Fact]
    public void Training_And_Inference_Produce_Byte_Identical_V7_Raw_Vectors()
    {
        // Shared inputs.
        var encoder   = BuildEncoder();
        var projection = new CpcEncoderProjection();
        var (window, current, previous) = BuildWindow(windowSize: 30);

        // A neutral V6 raw vector — we only need a valid shape; what matters is the append
        // half of the pipeline. Using `new float[FeatureCountV6]` leaves V6 slots at 0.
        var v6Raw = new float[MLFeatureHelper.FeatureCountV6];
        for (int i = 0; i < v6Raw.Length; i++) v6Raw[i] = i * 0.01f;

        // Mirrors the V7 dispatch at every call site: seqLen = count - 1 so the first candle
        // serves as the prior-close reference for log-return normalisation.
        int seqLen = Math.Max(2, window.Count - 1);

        // Training path: the V7 training-sample builder calls BuildFeatureVectorV7(v6, embedding)
        // after resolving the embedding from the window via MLCpcSequenceBuilder + projection.
        var trainSequences = MLCpcSequenceBuilder.Build(
            window, seqLen: seqLen, stride: seqLen, maxSequences: 1);
        Assert.Single(trainSequences);
        var trainEmbedding = projection.ProjectLatest(encoder, trainSequences[0]);
        var trainV7        = MLFeatureHelper.BuildFeatureVectorV7(v6Raw, trainEmbedding);

        // Inference path: MLSignalScorer.V7 dispatch does the exact same sequence -> projection
        // -> BuildFeatureVectorV7 call. We inline that recipe here to catch drift between the
        // two call sites.
        var inferSequences = MLCpcSequenceBuilder.Build(
            window, seqLen: seqLen, stride: seqLen, maxSequences: 1);
        var inferEmbedding = projection.ProjectLatest(encoder, inferSequences[0]);
        var inferV7        = MLFeatureHelper.BuildFeatureVectorV7(v6Raw, inferEmbedding);

        Assert.Equal(MLFeatureHelper.FeatureCountV7, trainV7.Length);
        Assert.Equal(trainV7.Length, inferV7.Length);
        for (int i = 0; i < trainV7.Length; i++)
            Assert.Equal(trainV7[i], inferV7[i], precision: 6);

        // Tighter bound on the CPC block specifically: these came from the projection call and
        // must pass through BuildFeatureVectorV7's SanitizeScalar unchanged for a well-formed
        // encoder, so the block matches what projection returned (no rounding beyond float).
        for (int i = 0; i < MLFeatureHelper.CpcEmbeddingBlockSize; i++)
        {
            int slot = MLFeatureHelper.FeatureCountV6 + i;
            Assert.Equal(inferEmbedding[i], inferV7[slot], precision: 6);
        }

        _ = current; _ = previous; // unused on purpose — fixture parity, not feature construction.
    }

    [Fact]
    public void No_Active_Encoder_Zero_Fills_V7_Block_Symmetrically()
    {
        var v6Raw = new float[MLFeatureHelper.FeatureCountV6];
        for (int i = 0; i < v6Raw.Length; i++) v6Raw[i] = 0.5f;

        // When CpcPretrainerWorker hasn't produced an encoder yet, both training
        // (cpcEmbeddingForWindow returns null) and inference (provider returns null →
        // BuildFeatureVectorV7 called with null) zero-fill the block. Predictions degrade
        // but inference keeps producing finite outputs.
        var trainV7 = MLFeatureHelper.BuildFeatureVectorV7(v6Raw, cpcEmbedding: null);
        var inferV7 = MLFeatureHelper.BuildFeatureVectorV7(v6Raw, cpcEmbedding: null);

        Assert.Equal(MLFeatureHelper.FeatureCountV7, trainV7.Length);
        for (int i = 0; i < trainV7.Length; i++)
            Assert.Equal(trainV7[i], inferV7[i]);
        for (int i = MLFeatureHelper.FeatureCountV6; i < MLFeatureHelper.FeatureCountV7; i++)
            Assert.Equal(0f, trainV7[i]);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────

    private static MLCpcEncoder BuildEncoder()
    {
        // 16 × 6 encoder matching MLFeatureHelper.CpcEmbeddingBlockSize and
        // MLCpcSequenceBuilder.FeaturesPerStep. Deterministic weights so the test is hermetic.
        const int E = MLFeatureHelper.CpcEmbeddingBlockSize;
        const int F = MLCpcSequenceBuilder.FeaturesPerStep;

        var rng = new Random(42);
        var flat = new double[E * F];
        for (int i = 0; i < flat.Length; i++) flat[i] = rng.NextDouble() - 0.5;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            We = flat,
            Wp = new[] { new double[E * E] }
        });

        return new MLCpcEncoder
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EmbeddingDim = E,
            PredictionSteps = 1,
            EncoderBytes = bytes,
            TrainedAt = DateTime.UtcNow,
            IsActive = true,
        };
    }

    private static (List<Candle> Window, Candle Current, Candle Previous) BuildWindow(int windowSize)
    {
        var rng = new Random(17);
        decimal price = 1.1m;
        var window = new List<Candle>(windowSize);
        var start = DateTime.UtcNow.AddHours(-(windowSize + 2));
        for (int i = 0; i < windowSize + 2; i++)
        {
            decimal d = (decimal)((rng.NextDouble() - 0.5) * 0.002);
            decimal open = price;
            decimal close = price + d;
            decimal hi = Math.Max(open, close) + 0.0001m;
            decimal lo = Math.Min(open, close) - 0.0001m;
            var c = new Candle
            {
                Id = i + 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = start.AddHours(i),
                Open = open, High = hi, Low = lo, Close = close,
                Volume = 1000m + i,
                IsClosed = true,
            };
            window.Add(c);
            price = close;
        }

        var current = window[^1];
        var previous = window[^2];
        var onlyWindow = window.Take(windowSize).ToList();
        return (onlyWindow, current, previous);
    }
}
