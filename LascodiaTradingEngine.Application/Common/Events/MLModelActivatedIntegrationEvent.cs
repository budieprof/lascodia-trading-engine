using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>MLTrainingWorker</c> after a new <c>MLModel</c> is promoted to
/// <c>Active</c> status. The primary consumer automatically starts an
/// <c>MLShadowEvaluation</c> pitting the new challenger against the previous champion
/// on live trade outcomes before any full promotion decision is made.
/// </summary>
public record MLModelActivatedIntegrationEvent : IntegrationEvent
{
    /// <summary>The newly activated MLModel's database Id (the challenger).</summary>
    public long      NewModelId     { get; init; }

    /// <summary>The model that was superseded, if any (the former champion).</summary>
    public long?     OldModelId     { get; init; }

    /// <summary>The currency pair this model targets.</summary>
    public string    Symbol         { get; init; } = string.Empty;

    /// <summary>The timeframe this model was trained on.</summary>
    public Timeframe Timeframe      { get; init; }

    /// <summary>The training run that produced this model.</summary>
    public long      TrainingRunId  { get; init; }

    /// <summary>Direction accuracy achieved on the training data (0.0–1.0).</summary>
    public decimal   DirectionAccuracy { get; init; }

    /// <summary>UTC timestamp when the model was activated.</summary>
    public DateTime  ActivatedAt    { get; init; }
}
