using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Commands.PromotePendingModelStrategy;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Second subscriber on <see cref="MLModelActivatedIntegrationEvent"/>, alongside the
/// existing <c>MLModelActivatedEventHandler</c> that writes an audit log. This handler's
/// job is narrower and more specialised: find any <see cref="Strategy"/> rows parked in
/// <see cref="StrategyLifecycleStage.PendingModel"/> for the newly-activated model's
/// combo, and dispatch <see cref="PromotePendingModelStrategyCommand"/> for each one.
///
/// This closes the autonomous chicken-and-egg loop:
/// <list type="number">
///   <item><c>StrategyGenerationWorker</c> emits a CompositeML candidate for a combo
///         with no MLModel → <c>DeferredCompositeMLRegistrar</c> parks it + queues
///         a training run.</item>
///   <item><c>MLTrainingWorker</c> trains the model, its 14-gate quality check passes,
///         the model is promoted to Active and this event fires.</item>
///   <item><b>This handler</b> finds the parked strategy and moves it out of
///         <c>PendingModel</c> so the normal lifecycle can take over.</item>
/// </list>
///
/// <para>
/// Idempotency: the <see cref="PromotePendingModelStrategyCommand"/> handler no-ops
/// when the strategy is already out of <c>PendingModel</c>, so redelivered events are
/// safe. The query is scoped tightly to the exact (Symbol, Timeframe, StrategyType)
/// combo so unrelated events cause zero work.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Transient, typeof(IIntegrationEventHandler<MLModelActivatedIntegrationEvent>))]
public class PromotePendingModelStrategiesOnActivationHandler
    : IIntegrationEventHandler<MLModelActivatedIntegrationEvent>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PromotePendingModelStrategiesOnActivationHandler> _logger;

    public PromotePendingModelStrategiesOnActivationHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<PromotePendingModelStrategiesOnActivationHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task Handle(MLModelActivatedIntegrationEvent @event)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Tight query — matches on Symbol + Timeframe + StrategyType + LifecycleStage.
        // In practice there is at most one parked row per combo (unique generation key
        // index), but the Where + ToList handles future relaxations cleanly.
        var parkedIds = await readCtx.GetDbContext()
            .Set<Strategy>()
            .AsNoTracking()
            .Where(s => !s.IsDeleted
                     && s.StrategyType == StrategyType.CompositeML
                     && s.Symbol == @event.Symbol
                     && s.Timeframe == @event.Timeframe
                     && s.LifecycleStage == StrategyLifecycleStage.PendingModel)
            .Select(s => s.Id)
            .ToListAsync();

        if (parkedIds.Count == 0)
        {
            _logger.LogDebug(
                "PromotePendingModelStrategiesOnActivation: no parked strategies for {Symbol}/{Tf} on model {ModelId}",
                @event.Symbol, @event.Timeframe, @event.NewModelId);
            return;
        }

        _logger.LogInformation(
            "PromotePendingModelStrategiesOnActivation: {Count} parked strategies for {Symbol}/{Tf} — promoting on model {ModelId}",
            parkedIds.Count, @event.Symbol, @event.Timeframe, @event.NewModelId);

        foreach (var strategyId in parkedIds)
        {
            try
            {
                await mediator.Send(new PromotePendingModelStrategyCommand
                {
                    StrategyId         = strategyId,
                    ActivatedMLModelId = @event.NewModelId,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PromotePendingModelStrategiesOnActivation: failed to promote strategy {StrategyId} on model {ModelId}",
                    strategyId, @event.NewModelId);
                // Continue — one strategy's failure must not block promoting the others.
            }
        }
    }
}
