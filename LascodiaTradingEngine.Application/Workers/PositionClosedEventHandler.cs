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
/// Integration event handler that reacts to <see cref="PositionClosedIntegrationEvent"/>
/// by writing a structured <see cref="DecisionLog"/> audit entry that captures realised
/// P&amp;L, close reason, pip movement, and profitability outcome.
///
/// <para>
/// <b>When it fires:</b> After <c>PositionWorker</c> closes a position (Stop Loss hit,
/// Take Profit hit, or manual closure) and publishes
/// <see cref="PositionClosedIntegrationEvent"/> onto the event bus.
/// </para>
///
/// <para>
/// <b>What it does:</b>
/// <list type="number">
///   <item>Logs the close event at Information level immediately on arrival so that even if
///         subsequent steps fail, the closure is visible in structured logs.</item>
///   <item>Checks whether a <c>DecisionLog</c> entry for this position closure already exists
///         (idempotency guard) and exits early if it does, preventing duplicate audit rows
///         on event-bus re-delivery.</item>
///   <item>Dispatches a <see cref="LogDecisionCommand"/> via MediatR to persist a
///         <c>DecisionLog</c> row containing the full close context — direction, symbol,
///         entry/close prices, realised P&amp;L, pips moved, and close reason.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pipeline position:</b>
/// <c>SL/TP breach → PositionWorker → PositionClosedIntegrationEvent →
/// PositionClosedEventHandler (audit log) +
/// MLPredictionOutcomeWorker (back-fills ML prediction outcomes) +
/// AccountSyncWorker (updates equity)</c>
/// </para>
///
/// <para>
/// <b>Design note — no retry loop:</b> Unlike <see cref="OrderFilledEventHandler"/>,
/// this handler does not implement its own retry loop. The audit log write is a
/// low-risk, non-critical operation — if the <see cref="LogDecisionCommand"/> fails, the
/// exception propagates to the event bus consumer, which will re-deliver the event
/// according to its own retry and dead-letter policy. The idempotency check ensures
/// re-delivery is safe.
/// </para>
///
/// <para>
/// <b>What this handler does NOT do:</b> It does not update <c>MLModelPredictionLog</c>
/// outcome fields, recalculate account equity, or trigger strategy feedback. Those
/// responsibilities belong to <c>MLPredictionOutcomeWorker</c> and
/// <c>AccountSyncWorker</c>, which also subscribe to the same event.
/// </para>
///
/// <para>
/// <b>DI note:</b> Registered as <c>Transient</c> via <c>AutoRegisterEventHandler</c>
/// and subscribed at startup. A new DI scope is created per invocation via
/// <see cref="IServiceScopeFactory"/> to safely consume scoped services.
/// </para>
/// </summary>
public sealed class PositionClosedEventHandler : IIntegrationEventHandler<PositionClosedIntegrationEvent>
{
    // IServiceScopeFactory is used (instead of direct injection of scoped services) because
    // this handler is instantiated in a context where the ambient DI scope may be singleton-
    // level (the event-bus consumer thread). Creating an explicit scope per Handle call
    // prevents EF Core DbContext instances from being shared across concurrent deliveries.
    private readonly IServiceScopeFactory               _scopeFactory;
    private readonly ILogger<PositionClosedEventHandler> _logger;

    /// <summary>
    /// Initialises the handler with the scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a fresh DI scope per event invocation, ensuring scoped services
    /// such as <see cref="IReadApplicationDbContext"/> and <see cref="IMediator"/> are
    /// properly isolated and disposed after each call.
    /// </param>
    /// <param name="logger">Structured logger for diagnostics and duplicate-skip notices.</param>
    public PositionClosedEventHandler(
        IServiceScopeFactory              scopeFactory,
        ILogger<PositionClosedEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point called by the event bus when a <see cref="PositionClosedIntegrationEvent"/>
    /// is received. Immediately logs the closure at Information level, then performs an
    /// idempotency check before persisting the audit entry.
    /// </summary>
    /// <param name="event">
    /// The integration event published by <c>PositionWorker</c>. Consumed fields:
    /// <list type="bullet">
    ///   <item><see cref="PositionClosedIntegrationEvent.PositionId"/> — primary key used
    ///         for the idempotency check and as the <c>DecisionLog.EntityId</c>.</item>
    ///   <item><see cref="PositionClosedIntegrationEvent.CloseReason"/> — one of
    ///         <c>"StopLoss"</c>, <c>"TakeProfit"</c>, or <c>"Manual"</c>.</item>
    ///   <item><see cref="PositionClosedIntegrationEvent.RealisedPnL"/> — account-currency
    ///         P&amp;L stored verbatim in the audit reason string.</item>
    ///   <item><see cref="PositionClosedIntegrationEvent.ActualMagnitudePips"/> — pip
    ///         distance from entry to close, positive when favourable.</item>
    ///   <item><see cref="PositionClosedIntegrationEvent.WasProfitable"/> — determines the
    ///         <c>Outcome</c> field value: <c>"Profitable"</c> or <c>"Loss"</c>.</item>
    /// </list>
    /// </param>
    public async Task Handle(PositionClosedIntegrationEvent @event)
    {
        // Log the close immediately so that the event is visible in structured logs even
        // before the database write. This is especially useful when debugging P&L
        // discrepancies or unexpected position closures in production.
        _logger.LogInformation(
            "PositionClosedEventHandler: position {PositionId} closed " +
            "({Symbol} {Direction}) — Reason={Reason}, PnL={PnL:F2}, Pips={Pips:F1}, Profitable={Profitable}",
            @event.PositionId, @event.Symbol, @event.Direction,
            @event.CloseReason, @event.RealisedPnL, @event.ActualMagnitudePips, @event.WasProfitable);

        // Create a fresh DI scope to safely resolve scoped services.
        using var scope = _scopeFactory.CreateScope();
        var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // Idempotency: skip if this close event was already logged.
        // The triple-key check (EntityType + EntityId + DecisionType) is the canonical
        // idempotency pattern used across all event handlers in this codebase. It prevents
        // duplicate audit rows when the event bus re-delivers a message after a transient
        // failure or consumer restart.
        bool alreadyLogged = await readContext.GetDbContext()
            .Set<DecisionLog>()
            .AnyAsync(d => d.EntityType == "Position" &&
                           d.EntityId == @event.PositionId &&
                           d.DecisionType == "PositionClosed");
        if (alreadyLogged)
        {
            _logger.LogDebug(
                "PositionClosedEventHandler: decision log already exists for position {Id} — skipping duplicate",
                @event.PositionId);
            return;
        }

        // Persist the audit entry via MediatR so that the FluentValidation pipeline
        // behaviour runs first, and the write goes through the write DbContext as per the
        // CQRS separation rule. The Reason string is intentionally verbose — it is the
        // primary human-readable record of what happened to this position.
        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Position",
            EntityId     = @event.PositionId,
            DecisionType = "PositionClosed",
            // "Profitable" or "Loss" — drives performance attribution reports and strategy
            // feedback scoring in StrategyFeedbackWorker.
            Outcome      = @event.WasProfitable ? "Profitable" : "Loss",
            Reason       = $"{@event.CloseReason}: {@event.Direction} {@event.Symbol} " +
                           $"entry={@event.EntryPrice:F5} close={@event.ClosePrice:F5} " +
                           $"PnL={@event.RealisedPnL:F2} Pips={@event.ActualMagnitudePips:F1}",
            Source       = "PositionClosedEventHandler"
        });
    }
}
