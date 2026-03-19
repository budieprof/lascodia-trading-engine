using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.MLModels.Services;

/// <summary>
/// Default no-op implementation of IMLSignalScorer.
/// Returns null scores — real ML scoring is performed by MLSignalScorer in Infrastructure
/// once a trained model file is available.
/// </summary>
public class NullMLSignalScorer : IMLSignalScorer
{
    public Task<MLScoreResult> ScoreAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new MLScoreResult(null, null, null, null));
    }
}
