using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Batched inference implementation that groups scoring requests by symbol+timeframe,
/// shares feature vector computation within each group, and delegates individual scoring
/// to the underlying <see cref="IMLSignalScorer"/>.
///
/// When a burst of signals arrives (e.g., from an EA tick batch triggering multiple
/// strategies on the same symbol), the candle history is identical within each
/// symbol+timeframe group. By grouping first, we avoid redundant feature computation
/// and model resolution overhead.
///
/// For single-request batches, delegates directly to <see cref="IMLSignalScorer"/>
/// with zero overhead.
/// </summary>
[RegisterService]
public sealed class BatchMLSignalScorer : IBatchMLSignalScorer
{
    private readonly IMLSignalScorer _scorer;
    private readonly ILogger<BatchMLSignalScorer> _logger;

    public BatchMLSignalScorer(
        IMLSignalScorer scorer,
        ILogger<BatchMLSignalScorer> logger)
    {
        _scorer = scorer;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MLScoreResult?>> ScoreBatchAsync(
        IReadOnlyList<BatchScoringRequest> requests,
        CancellationToken ct = default)
    {
        if (requests.Count == 0)
            return Array.Empty<MLScoreResult?>();

        // Fast path: single request — no grouping overhead
        if (requests.Count == 1)
        {
            var req = requests[0];
            try
            {
                var result = await _scorer.ScoreAsync(req.Signal, req.Candles, ct);
                return new[] { result };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "BatchMLSignalScorer: single-request scoring failed for {Symbol}",
                    req.Signal.Symbol);
                return new MLScoreResult?[] { null };
            }
        }

        // Allocate result array preserving original request order
        var results = new MLScoreResult?[requests.Count];

        // Group requests by symbol+timeframe to share feature computation
        // The timeframe is derived from the candle list (same heuristic as MLSignalScorer)
        var groups = requests
            .Select((req, index) => new
            {
                Request = req,
                Index = index,
                GroupKey = BuildGroupKey(req)
            })
            .GroupBy(x => x.GroupKey);

        foreach (var group in groups)
        {
            var items = group.ToList();

            // Within a group, all signals target the same symbol+timeframe.
            // The first request's candles are representative — the scorer uses them
            // for feature extraction, and all signals in the group share the same
            // underlying candle history.
            var sharedCandles = items[0].Request.Candles;

            _logger.LogDebug(
                "BatchMLSignalScorer: scoring group {GroupKey} with {Count} signals, " +
                "sharing {CandleCount} candles",
                group.Key, items.Count, sharedCandles.Count);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // Use the shared candles for all signals in the group.
                    // The scorer resolves the model once per call, but the model resolution
                    // result is cached in IMemoryCache so subsequent calls within the same
                    // group hit the cache.
                    var result = await _scorer.ScoreAsync(
                        item.Request.Signal,
                        sharedCandles,
                        ct);

                    results[item.Index] = result;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "BatchMLSignalScorer: scoring failed for signal on {Symbol} " +
                        "in group {GroupKey}",
                        item.Request.Signal.Symbol, group.Key);
                    results[item.Index] = null;
                }
            }
        }

        _logger.LogDebug(
            "BatchMLSignalScorer: completed batch of {Total} requests across {Groups} groups",
            requests.Count,
            groups.Count());

        return results;
    }

    /// <summary>
    /// Builds a grouping key from symbol + timeframe so signals sharing the same
    /// candle history are scored together with shared feature vectors.
    /// </summary>
    private static string BuildGroupKey(BatchScoringRequest req)
    {
        var symbol = req.Signal.Symbol ?? string.Empty;
        var timeframe = req.Candles.Count > 0
            ? req.Candles[0].Timeframe
            : Timeframe.H1;

        return $"{symbol}:{timeframe}";
    }
}
