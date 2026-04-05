using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>OptimizationWorker</c> when an optimization run completes (regardless
/// of whether it was auto-approved or sent to manual review). Downstream consumers can
/// use this to trigger dashboards, notifications, or further pipeline steps.
/// </summary>
public record OptimizationCompletedIntegrationEvent : IntegrationEvent
{
    public long      SequenceNumber { get; init; } = EventSequence.Next();
    public long      OptimizationRunId { get; init; }
    public long      StrategyId        { get; init; }
    public string    Symbol            { get; init; } = string.Empty;
    public Timeframe Timeframe         { get; init; }
    public int       Iterations        { get; init; }
    public decimal   BaselineScore     { get; init; }
    public decimal   BestOosScore      { get; init; }
    public DateTime  CompletedAt       { get; init; }
}
