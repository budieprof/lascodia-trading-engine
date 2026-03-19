using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Reacts to <see cref="MLModelActivatedIntegrationEvent"/> by writing a structured
/// audit entry that captures model promotion details (accuracy, training run, replaced model).
///
/// <para>
/// <see cref="Services.MLSignalScorer"/> already queries the active model from the database
/// on every call, so there is no in-process cache to invalidate. This handler's sole
/// responsibility is ensuring that every model promotion is recorded in
/// <see cref="Domain.Entities.DecisionLog"/> for observability and compliance.
/// </para>
/// </summary>
public sealed class MLModelActivatedEventHandler : IIntegrationEventHandler<MLModelActivatedIntegrationEvent>
{
    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly ILogger<MLModelActivatedEventHandler>  _logger;

    public MLModelActivatedEventHandler(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLModelActivatedEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task Handle(MLModelActivatedIntegrationEvent @event)
    {
        string replacedClause = @event.OldModelId.HasValue
            ? $"; replaced model {@event.OldModelId}"
            : string.Empty;

        _logger.LogInformation(
            "MLModelActivatedEventHandler: model {NewModelId} activated for {Symbol}/{Timeframe} " +
            "(accuracy={Accuracy:P2}, trainingRun={RunId}{Replaced})",
            @event.NewModelId, @event.Symbol, @event.Timeframe,
            @event.DirectionAccuracy, @event.TrainingRunId, replacedClause);

        using var scope = _scopeFactory.CreateScope();
        var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "MLModel",
            EntityId     = @event.NewModelId,
            DecisionType = "ModelActivated",
            Outcome      = "Active",
            Reason       = $"{@event.Symbol}/{@event.Timeframe} model promoted from training run " +
                           $"{@event.TrainingRunId} (DirectionAccuracy={@event.DirectionAccuracy:P2})" +
                           replacedClause,
            Source       = "MLModelActivatedEventHandler"
        });
    }
}
