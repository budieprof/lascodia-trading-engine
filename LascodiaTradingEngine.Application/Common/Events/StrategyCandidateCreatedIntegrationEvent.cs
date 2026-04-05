using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>StrategyGenerationWorker</c> when a new auto-generated strategy candidate
/// passes screening and is persisted as Draft/Paused. The primary consumer is <c>BacktestWorker</c>
/// which can pick up the queued <c>BacktestRun</c> immediately rather than waiting for its next
/// polling interval, enabling event-driven progression through the validation pipeline.
/// </summary>
public record StrategyCandidateCreatedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long      SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>The newly created strategy's database Id.</summary>
    public long      StrategyId    { get; init; }

    /// <summary>Human-readable strategy name (e.g. "Auto-MovingAverageCrossover-EURUSD-H1").</summary>
    public string    Name          { get; init; } = string.Empty;

    /// <summary>Currency pair the strategy targets.</summary>
    public string    Symbol        { get; init; } = string.Empty;

    /// <summary>Timeframe the strategy operates on.</summary>
    public Timeframe Timeframe     { get; init; }

    /// <summary>The strategy type (e.g. MovingAverageCrossover, RSIReversion).</summary>
    public StrategyType StrategyType { get; init; }

    /// <summary>The market regime the strategy was generated for.</summary>
    public LascodiaTradingEngine.Domain.Enums.MarketRegime Regime { get; init; }

    /// <summary>UTC timestamp when the candidate was created.</summary>
    public DateTime  CreatedAt     { get; init; }
}
