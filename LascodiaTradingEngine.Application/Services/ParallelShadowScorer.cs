using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Scores champion and challenger models concurrently during shadow evaluation,
/// reducing wall-clock scoring time from 2x latency to max(champion, challenger).
///
/// Used by <see cref="Workers.MLShadowArbiterWorker"/> and shadow evaluation flows
/// where both the production model and a candidate must be scored on the same signal.
/// Each scoring task runs independently — if one fails, the other still returns.
/// </summary>
[RegisterService]
public sealed class ParallelShadowScorer
{
    private readonly IMLSignalScorer _scorer;
    private readonly ILogger<ParallelShadowScorer> _logger;

    public ParallelShadowScorer(
        IMLSignalScorer scorer,
        ILogger<ParallelShadowScorer> logger)
    {
        _scorer = scorer;
        _logger = logger;
    }

    /// <summary>
    /// Scores a trade signal against both champion and challenger models concurrently.
    /// </summary>
    /// <param name="signal">The trade signal to score.</param>
    /// <param name="candles">Candle history for feature computation.</param>
    /// <param name="championModelId">
    /// The active champion model ID. The scorer resolves the active model for the
    /// signal's symbol/timeframe, so this is used for logging and validation only.
    /// </param>
    /// <param name="challengerModelId">
    /// The challenger model ID being shadow-evaluated. Passed for logging — the
    /// actual model resolution is handled by the scorer infrastructure.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of (champion result, challenger result). Either side may be null
    /// if that model's scoring fails — the other side still returns its result.
    /// </returns>
    public async Task<(MLScoreResult? Champion, MLScoreResult? Challenger)> ScoreParallelAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        long championModelId,
        long challengerModelId,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "ParallelShadowScorer: scoring {Symbol} — champion {ChampionId} vs challenger {ChallengerId}",
            signal.Symbol, championModelId, challengerModelId);

        // Launch both scoring tasks concurrently
        var championTask = ScoreSafeAsync(signal, candles, championModelId, "champion", ct);
        var challengerTask = ScoreSafeAsync(signal, candles, challengerModelId, "challenger", ct);

        await Task.WhenAll(championTask, challengerTask);

        var championResult = await championTask;
        var challengerResult = await challengerTask;

        if (championResult is null && challengerResult is null)
        {
            _logger.LogWarning(
                "ParallelShadowScorer: both champion {ChampionId} and challenger {ChallengerId} " +
                "failed for {Symbol}",
                championModelId, challengerModelId, signal.Symbol);
        }

        return (championResult, challengerResult);
    }

    /// <summary>
    /// Wraps a single scoring call with exception handling so one failure does not
    /// cancel the other concurrent task.
    /// </summary>
    private async Task<MLScoreResult?> ScoreSafeAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        long modelId,
        string role,
        CancellationToken ct)
    {
        try
        {
            return await _scorer.ScoreAsync(signal, candles, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ParallelShadowScorer: {Role} model {ModelId} scoring failed for {Symbol}",
                role, modelId, signal.Symbol);
            return null;
        }
    }
}
