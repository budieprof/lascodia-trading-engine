using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Events;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationEventFactory))]
/// <summary>
/// Builds integration events emitted by the strategy-generation persistence pipeline.
/// </summary>
internal sealed class StrategyGenerationEventFactory : IStrategyGenerationEventFactory
{
    public StrategyCandidateCreatedIntegrationEvent BuildCandidateCreatedEvent(ScreeningOutcome candidate, long strategyId)
        => new()
        {
            StrategyId = strategyId,
            Name = candidate.Strategy.Name,
            Symbol = candidate.Strategy.Symbol,
            Timeframe = candidate.Strategy.Timeframe,
            StrategyType = candidate.Strategy.StrategyType,
            Regime = candidate.Regime,
            ObservedRegime = candidate.ObservedRegime,
            GenerationSource = candidate.GenerationSource,
            ReserveTargetRegime = candidate.GenerationSource == "Reserve" ? candidate.Regime : null,
            CreatedAt = candidate.Strategy.CreatedAt,
        };

    public StrategyAutoPromotedIntegrationEvent BuildAutoPromotedEvent(ScreeningOutcome candidate, long strategyId)
    {
        // Fall back to a minimal metrics payload so auto-promotion events remain publishable even
        // when the caller did not materialize a full ScreeningMetrics instance.
        var metrics = candidate.Metrics ?? new ScreeningMetrics
        {
            Regime = candidate.Regime.ToString(),
            ObservedRegime = candidate.ObservedRegime.ToString(),
            GenerationSource = candidate.GenerationSource,
            ScreenedAtUtc = candidate.Strategy.CreatedAt,
        };
        return new StrategyAutoPromotedIntegrationEvent
        {
            StrategyId = strategyId,
            Name = candidate.Strategy.Name,
            Symbol = candidate.Strategy.Symbol,
            Timeframe = candidate.Strategy.Timeframe,
            StrategyType = candidate.Strategy.StrategyType,
            Regime = candidate.Regime,
            ObservedRegime = candidate.ObservedRegime,
            GenerationSource = candidate.GenerationSource,
            ReserveTargetRegime = candidate.GenerationSource == "Reserve" ? candidate.Regime : null,
            IsSharpeRatio = metrics.IsSharpeRatio,
            OosSharpeRatio = metrics.OosSharpeRatio,
            EquityCurveR2 = metrics.EquityCurveR2,
            MonteCarloPValue = metrics.MonteCarloPValue,
            ShufflePValue = metrics.ShufflePValue,
            WalkForwardWindowsPassed = metrics.WalkForwardWindowsPassed,
            LiveHaircutApplied = metrics.LiveHaircutApplied,
            WinRateHaircutApplied = metrics.WinRateHaircutApplied,
            ProfitFactorHaircutApplied = metrics.ProfitFactorHaircutApplied,
            SharpeHaircutApplied = metrics.SharpeHaircutApplied,
            DrawdownInflationApplied = metrics.DrawdownInflationApplied,
        };
    }
}
