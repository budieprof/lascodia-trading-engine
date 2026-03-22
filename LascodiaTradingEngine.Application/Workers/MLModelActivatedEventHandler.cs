using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Integration event handler that reacts to <see cref="MLModelActivatedIntegrationEvent"/>
/// by writing a structured <see cref="DecisionLog"/> audit entry that captures model
/// promotion details: which model was activated, its direction accuracy, the training run
/// that produced it, and which previous model (if any) was superseded.
///
/// <para>
/// <b>When it fires:</b> After <c>MLTrainingWorker</c> or <c>MLShadowArbiterWorker</c>
/// promotes a newly trained <c>MLModel</c> to <c>Active</c> status and publishes
/// <see cref="MLModelActivatedIntegrationEvent"/> onto the event bus.
/// </para>
///
/// <para>
/// <b>What it does:</b>
/// <list type="number">
///   <item>Constructs a human-readable "replaced model" clause when a previous champion
///         model is being superseded, for inclusion in the audit reason string.</item>
///   <item>Logs the activation at Information level immediately on arrival.</item>
///   <item>Performs an idempotency check against <see cref="DecisionLog"/> so that
///         re-delivered events do not produce duplicate audit rows.</item>
///   <item>Dispatches a <see cref="LogDecisionCommand"/> via MediatR to persist the
///         promotion record — symbol, timeframe, training run ID, direction accuracy,
///         and any replaced model ID.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pipeline position:</b>
/// <c>MLTrainingWorker trains model → MLShadowArbiterWorker evaluates challenger →
/// ActivateModelCommand promotes model → MLModelActivatedIntegrationEvent published →
/// MLModelActivatedEventHandler (audit log) +
/// MLShadowArbiterWorker (starts shadow evaluation of challenger vs. new champion)</c>
/// </para>
///
/// <para>
/// <b>Why no cache invalidation:</b> <see cref="Services.MLSignalScorer"/> queries the
/// active model from the database on every scoring call — it holds no in-process cache.
/// Therefore this handler does not need to invalidate or refresh any model reference;
/// the next scoring call will automatically pick up the newly active model from the
/// read context. This keeps the handler simple and avoids distributed cache consistency
/// problems.
/// </para>
///
/// <para>
/// <b>Compliance note:</b> Every model promotion must be traceable for audit and
/// regulatory purposes. The <see cref="DecisionLog"/> entry written here is the
/// authoritative record of when a model was put into production, what its accuracy was,
/// and what it replaced. This is queried by the back-office ML governance UI.
/// </para>
///
/// <para>
/// <b>Design note — no retry loop:</b> Like <see cref="PositionClosedEventHandler"/>,
/// this handler relies on the event bus's own re-delivery mechanism for resilience. The
/// idempotency check makes re-delivery safe. A self-managed retry loop is omitted here
/// because the audit write is non-critical and the event bus provides at-least-once
/// delivery guarantees.
/// </para>
///
/// <para>
/// <b>DI note:</b> Registered as <c>Transient</c> via <c>AutoRegisterEventHandler</c>
/// and subscribed to the event bus at application startup. A new DI scope is created per
/// invocation via <see cref="IServiceScopeFactory"/> to safely consume scoped services.
/// </para>
/// </summary>
public sealed class MLModelActivatedEventHandler : IIntegrationEventHandler<MLModelActivatedIntegrationEvent>
{
    // IServiceScopeFactory is used instead of injecting scoped services directly because
    // this handler is Transient but is invoked from the singleton event-bus consumer loop.
    // Creating an explicit scope per Handle call prevents EF Core DbContext sharing across
    // concurrent event deliveries.
    private readonly IServiceScopeFactory                   _scopeFactory;
    private readonly ILogger<MLModelActivatedEventHandler>  _logger;

    /// <summary>
    /// Initialises the handler with the scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a fresh DI scope per event invocation, ensuring scoped services
    /// such as <see cref="IReadApplicationDbContext"/> and <see cref="IMediator"/> are
    /// properly isolated and disposed after each call.
    /// </param>
    /// <param name="logger">Structured logger for diagnostics and duplicate-skip notices.</param>
    public MLModelActivatedEventHandler(
        IServiceScopeFactory                  scopeFactory,
        ILogger<MLModelActivatedEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point called by the event bus when an <see cref="MLModelActivatedIntegrationEvent"/>
    /// is received. Logs the activation, performs an idempotency check, and persists the
    /// promotion audit entry.
    /// </summary>
    /// <param name="event">
    /// The integration event published by <c>MLTrainingWorker</c> or
    /// <c>MLShadowArbiterWorker</c>. Consumed fields:
    /// <list type="bullet">
    ///   <item><see cref="MLModelActivatedIntegrationEvent.NewModelId"/> — primary key of
    ///         the newly active <c>MLModel</c>; used as <c>DecisionLog.EntityId</c> and for
    ///         the idempotency check.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.OldModelId"/> — the superseded
    ///         model's ID, if one existed. Null when this is the first model for the
    ///         symbol/timeframe combination.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.Symbol"/> and
    ///         <see cref="MLModelActivatedIntegrationEvent.Timeframe"/> — identify which
    ///         instrument and granularity the model targets.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.DirectionAccuracy"/> — the
    ///         fraction of direction predictions that were correct on training data (0.0–1.0),
    ///         stored in the audit reason for governance review.</item>
    ///   <item><see cref="MLModelActivatedIntegrationEvent.TrainingRunId"/> — links the
    ///         activated model back to the <c>MLTrainingRun</c> that produced it.</item>
    /// </list>
    /// </param>
    public async Task Handle(MLModelActivatedIntegrationEvent @event)
    {
        // Build the "replaced model" suffix once and reuse it in both the log message
        // and the DecisionLog reason string. An empty string is used rather than a null
        // check later to keep the string interpolations clean.
        string replacedClause = @event.OldModelId.HasValue
            ? $"; replaced model {@event.OldModelId}"
            : string.Empty;

        // Log immediately so the activation is visible in structured logs regardless
        // of whether the subsequent database write succeeds. Useful when debugging
        // model promotion issues in production.
        _logger.LogInformation(
            "MLModelActivatedEventHandler: model {NewModelId} activated for {Symbol}/{Timeframe} " +
            "(accuracy={Accuracy:P2}, trainingRun={RunId}{Replaced})",
            @event.NewModelId, @event.Symbol, @event.Timeframe,
            @event.DirectionAccuracy, @event.TrainingRunId, replacedClause);

        // Create a fresh DI scope to safely resolve scoped services.
        using var scope = _scopeFactory.CreateScope();
        var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // Idempotency: skip if this activation was already logged.
        // Uses the triple-key pattern (EntityType + EntityId + DecisionType) consistent
        // with PositionClosedEventHandler and other handlers in this codebase, ensuring
        // safe at-least-once re-delivery from RabbitMQ/Kafka without duplicate audit rows.
        bool alreadyLogged = await readContext.GetDbContext()
            .Set<DecisionLog>()
            .AnyAsync(d => d.EntityType == "MLModel" &&
                           d.EntityId == @event.NewModelId &&
                           d.DecisionType == "ModelActivated");
        if (alreadyLogged)
        {
            _logger.LogDebug(
                "MLModelActivatedEventHandler: decision log already exists for model {Id} — skipping duplicate",
                @event.NewModelId);
            return;
        }

        // Persist the governance audit entry via MediatR. The Reason string is intentionally
        // verbose — it is the primary human-readable compliance record of the model promotion.
        // Outcome="Active" mirrors the MLModelStatus enum value so that the audit trail can
        // be filtered or aggregated by outcome without parsing the reason string.
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
