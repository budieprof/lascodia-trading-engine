using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Monitors the accuracy of performance feedback predictions and adjusts the
/// recency decay half-life accordingly. When predictions become inaccurate
/// (market dynamics changed), the half-life shortens so recent data dominates.
/// When predictions are accurate, the half-life lengthens to leverage more history.
/// </summary>
public interface IFeedbackDecayMonitor
{
    double GetEffectiveHalfLifeDays();
    Task RecordPredictionsAsync(DbContext readDb, DbContext writeDb,
        Dictionary<(StrategyType, MarketRegimeEnum), double> predictions, CancellationToken ct);
    Task EvaluateAndAdjustAsync(DbContext readDb, DbContext writeDb, CancellationToken ct);
}
