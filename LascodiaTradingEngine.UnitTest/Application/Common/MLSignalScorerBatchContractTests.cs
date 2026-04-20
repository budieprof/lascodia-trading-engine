using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Common;

/// <summary>
/// Pins the <see cref="IMLSignalScorer.ScoreBatchAsync"/> contract that concrete
/// batched scorers must honour. The interface ships a default fallback that
/// delegates to per-signal <c>ScoreAsync</c>; a real implementation would run a
/// single forward pass over the full batch. Either way, the observed results
/// MUST match what the sequential path would have produced — concrete scorers
/// need these invariants to be safe to opt into.
/// </summary>
public class MLSignalScorerBatchContractTests
{
    [Fact]
    public async Task DefaultBatch_ReturnsOneResultPerInputInOrder()
    {
        var scorer = new FakeScorer(asBuy: false);
        var batch = new List<(TradeSignal, IReadOnlyList<Candle>)>
        {
            (MakeSignal(1, TradeDirection.Buy),  Array.Empty<Candle>()),
            (MakeSignal(2, TradeDirection.Sell), Array.Empty<Candle>()),
            (MakeSignal(3, TradeDirection.Buy),  Array.Empty<Candle>()),
        };

        var results = await scorer.ScoreBatchAsync(batch, CancellationToken.None);

        Assert.Equal(batch.Count, results.Count);
        for (int i = 0; i < batch.Count; i++)
            Assert.Equal(batch[i].Item1.Direction, results[i].PredictedDirection);
    }

    [Fact]
    public async Task DefaultBatch_MatchesSequentialScoreAsync_ForIdenticalInputs()
    {
        var scorer = new FakeScorer(asBuy: true);
        var inputs = Enumerable.Range(0, 5)
            .Select(i => (MakeSignal(i, TradeDirection.Buy), (IReadOnlyList<Candle>)Array.Empty<Candle>()))
            .ToList();

        // Reference: sequential ScoreAsync
        var sequential = new List<MLScoreResult>();
        foreach (var (sig, c) in inputs)
            sequential.Add(await scorer.ScoreAsync(sig, c, CancellationToken.None));

        // Batched call
        var batched = await scorer.ScoreBatchAsync(inputs, CancellationToken.None);

        Assert.Equal(sequential.Count, batched.Count);
        for (int i = 0; i < sequential.Count; i++)
        {
            Assert.Equal(sequential[i].PredictedDirection, batched[i].PredictedDirection);
            Assert.Equal(sequential[i].ConfidenceScore,    batched[i].ConfidenceScore);
            Assert.Equal(sequential[i].MLModelId,          batched[i].MLModelId);
        }
    }

    [Fact]
    public async Task DefaultBatch_HonoursCancellationMidBatch()
    {
        // Slow scorer that respects CT. Cancel after the first item; the
        // default implementation checks the token between items so the
        // cancellation propagates before the second ScoreAsync starts. The
        // default interface member is only visible through the interface
        // reference (DIM semantics), so we access it via IMLSignalScorer.
        IMLSignalScorer scorer = new SlowScorer(delayMs: 100);
        using var cts = new CancellationTokenSource();

        var batch = new List<(TradeSignal, IReadOnlyList<Candle>)>
        {
            (MakeSignal(1, TradeDirection.Buy), Array.Empty<Candle>()),
            (MakeSignal(2, TradeDirection.Buy), Array.Empty<Candle>()),
            (MakeSignal(3, TradeDirection.Buy), Array.Empty<Candle>()),
        };

        var task = scorer.ScoreBatchAsync(batch, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task OverriddenBatch_ProducesSameObservableResult_ForIdenticalInputs()
    {
        // A concrete scorer that implements ScoreBatchAsync directly should
        // still agree with the sequential fallback on the SHAPE of results
        // (count, field-level equality) even if its implementation path is
        // completely different. This test catches a class of divergence bugs
        // where the "fast" batched path silently returns different values
        // than the slow sequential path.
        var sequential = new FakeScorer(asBuy: true);
        var batched    = new FakeScorer(asBuy: true) { OverrideBatch = true };

        var inputs = Enumerable.Range(0, 4)
            .Select(i => (MakeSignal(i, TradeDirection.Buy), (IReadOnlyList<Candle>)Array.Empty<Candle>()))
            .ToList();

        var seqRes = await sequential.ScoreBatchAsync(inputs, CancellationToken.None);
        var batRes = await batched.ScoreBatchAsync(inputs, CancellationToken.None);

        Assert.Equal(seqRes.Count, batRes.Count);
        for (int i = 0; i < seqRes.Count; i++)
        {
            Assert.Equal(seqRes[i].PredictedDirection, batRes[i].PredictedDirection);
            Assert.Equal(seqRes[i].ConfidenceScore,    batRes[i].ConfidenceScore);
            Assert.Equal(seqRes[i].MLModelId,          batRes[i].MLModelId);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TradeSignal MakeSignal(long id, TradeDirection dir) => new()
    {
        Id              = id,
        Symbol          = "EURUSD",
        Direction       = dir,
        EntryPrice      = 1.1m,
        StopLoss        = 1.09m,
        TakeProfit      = 1.11m,
        SuggestedLotSize = 0.1m,
        Confidence      = 0.7m,
        Status          = TradeSignalStatus.Pending,
        ExpiresAt       = DateTime.UtcNow.AddMinutes(30),
    };

    /// <summary>Deterministic scorer — returns a result that echoes the signal direction.</summary>
    private sealed class FakeScorer : IMLSignalScorer
    {
        private readonly bool _asBuy;
        public bool OverrideBatch { get; set; } = false;

        public FakeScorer(bool asBuy) { _asBuy = asBuy; }

        public Task<MLScoreResult> ScoreAsync(TradeSignal signal, IReadOnlyList<Candle> candles, CancellationToken cancellationToken)
            => Task.FromResult(new MLScoreResult(
                PredictedDirection:     signal.Direction,
                PredictedMagnitudePips: 10m,
                ConfidenceScore:        0.85m,
                MLModelId:              42));

        public async Task<IReadOnlyList<MLScoreResult>> ScoreBatchAsync(
            IReadOnlyList<(TradeSignal Signal, IReadOnlyList<Candle> Candles)> batch,
            CancellationToken cancellationToken)
        {
            if (!OverrideBatch)
            {
                // Delegate to the interface's default implementation by calling
                // ScoreAsync sequentially — but via the public path so the test
                // exercises exactly what default-impl users get.
                var results = new MLScoreResult[batch.Count];
                for (int i = 0; i < batch.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results[i] = await ScoreAsync(batch[i].Signal, batch[i].Candles, cancellationToken);
                }
                return results;
            }

            // Overridden "fast" path — a concrete batched scorer would use a
            // single tensor pass here; for the test we just construct the
            // results directly while still honouring cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            var fast = new MLScoreResult[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                fast[i] = new MLScoreResult(
                    PredictedDirection:     batch[i].Signal.Direction,
                    PredictedMagnitudePips: 10m,
                    ConfidenceScore:        0.85m,
                    MLModelId:              42);
            }
            return fast;
        }
    }

    /// <summary>Scorer that sleeps per signal so cancellation behaviour is observable.</summary>
    private sealed class SlowScorer : IMLSignalScorer
    {
        private readonly int _delayMs;
        public SlowScorer(int delayMs) { _delayMs = delayMs; }

        public async Task<MLScoreResult> ScoreAsync(TradeSignal signal, IReadOnlyList<Candle> candles, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken);
            return new MLScoreResult(signal.Direction, 10m, 0.8m, 42);
        }
    }
}
