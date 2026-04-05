using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>OptimizationWorker</c> when an optimization run is auto-approved and
/// the optimised parameters are applied to the strategy. Enables event-driven triggering
/// of validation backtests and downstream pipeline steps.
/// </summary>
public record OptimizationApprovedIntegrationEvent : IntegrationEvent
{
    public long      SequenceNumber { get; init; } = EventSequence.Next();
    public long      OptimizationRunId { get; init; }
    public long      StrategyId        { get; init; }
    public string    Symbol            { get; init; } = string.Empty;
    public Timeframe Timeframe         { get; init; }
    public decimal   Improvement       { get; init; }
    public decimal   OosScore          { get; init; }
    public DateTime  ApprovedAt        { get; init; }
}
