using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Commands.OpenPosition;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Integration event handler that reacts to <see cref="OrderFilledIntegrationEvent"/>
/// by opening a corresponding <see cref="Position"/> record in the database.
///
/// <para>
/// <b>When it fires:</b> Immediately after <c>OrderExecutionWorker</c> receives a fill
/// confirmation from the broker and publishes <see cref="OrderFilledIntegrationEvent"/>
/// onto the event bus (RabbitMQ or Kafka, depending on configuration).
/// </para>
///
/// <para>
/// <b>What it does:</b>
/// <list type="number">
///   <item>Loads the filled <see cref="Order"/> from the read context to obtain lot size,
///         direction, stop-loss, take-profit, and paper-trading flag.</item>
///   <item>Guards against duplicate processing: if a <see cref="Position"/> already exists
///         for this order (i.e., the event bus re-delivered the message), the handler exits
///         early without creating a second position.</item>
///   <item>Maps the order fields to an <see cref="OpenPositionCommand"/> and dispatches it
///         via MediatR, which persists a new <see cref="Position"/> with
///         <c>Status = Open</c>.</item>
///   <item>Writes a <see cref="DecisionLog"/> audit entry recording the order fill and the
///         resulting position ID for traceability.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Pipeline position:</b>
/// <c>Broker fill → OrderExecutionWorker → OrderFilledIntegrationEvent →
/// OrderFilledEventHandler → Position created →
/// PositionWorker / TrailingStopWorker / RiskMonitorWorker</c>
/// </para>
///
/// <para>
/// <b>Why this matters:</b> Without this handler, broker fills never produce positions.
/// <see cref="PositionWorker"/>, <see cref="TrailingStopWorker"/>, and all position-level
/// risk logic depend on open <see cref="Position"/> rows existing in the database. This
/// handler is the only component that creates them from live order fills.
/// </para>
///
/// <para>
/// <b>Retry behaviour:</b> The outer <see cref="Handle"/> loop retries up to 3 times with
/// exponential back-off (500 ms → 1 s → 2 s). After all retries are exhausted the event
/// is dropped and logged at Error level — manual intervention is required if this occurs,
/// because the filled order will have no corresponding position.
/// </para>
///
/// <para>
/// <b>DI note:</b> This handler is registered as <c>Transient</c> via
/// <c>AutoRegisterEventHandler</c> and subscribed to the event bus via
/// <c>eventBus.AutoConfigureEventHandler</c> at application startup. A new DI scope is
/// created for every invocation to obtain scoped services safely inside the singleton
/// event-bus dispatch loop.
/// </para>
/// </summary>
public sealed class OrderFilledEventHandler : IIntegrationEventHandler<OrderFilledIntegrationEvent>
{
    // IServiceScopeFactory is used instead of injecting scoped services directly because
    // this handler is resolved in a Transient lifetime from a singleton event-bus consumer.
    // Creating an explicit scope per Handle call ensures EF DbContext instances are not
    // shared across concurrent event deliveries.
    private readonly IServiceScopeFactory              _scopeFactory;
    private readonly ILogger<OrderFilledEventHandler>  _logger;

    /// <summary>
    /// Initialises the handler with the scope factory and logger.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a fresh DI scope per event, so that scoped services such as
    /// <see cref="IReadApplicationDbContext"/> and <see cref="IMediator"/> are isolated
    /// to a single event invocation and disposed promptly afterwards.
    /// </param>
    /// <param name="logger">Structured logger for diagnostics, warnings, and errors.</param>
    public OrderFilledEventHandler(
        IServiceScopeFactory             scopeFactory,
        ILogger<OrderFilledEventHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Entry point called by the event bus when an <see cref="OrderFilledIntegrationEvent"/>
    /// is received. Wraps <see cref="HandleCoreAsync"/> in a retry loop with exponential
    /// back-off so that transient infrastructure failures (e.g., a brief database blip)
    /// do not permanently lose the event.
    /// </summary>
    /// <param name="event">
    /// The integration event published by <c>OrderExecutionWorker</c>. Key fields consumed
    /// here are <see cref="OrderFilledIntegrationEvent.OrderId"/> (to load the order record)
    /// and <see cref="OrderFilledIntegrationEvent.FilledPrice"/> (used as the position's
    /// average entry price instead of the originally requested price, because slippage means
    /// the actual fill price may differ).
    /// </param>
    public async Task Handle(OrderFilledIntegrationEvent @event)
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await HandleCoreAsync(@event);
                return; // Success — exit the retry loop immediately.
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex))
            {
                // Exponential back-off with jitter: 500ms, 1s, 2s ± random jitter
                int baseDelayMs = 500 * (int)Math.Pow(2, attempt - 1);
                int jitter = Random.Shared.Next(0, baseDelayMs / 2);
                int delayMs = baseDelayMs + jitter;
                _logger.LogWarning(ex,
                    "OrderFilledEventHandler: transient error on attempt {Attempt}/{Max} for order {OrderId} — retrying in {Delay}ms",
                    attempt, maxRetries, @event.OrderId, delayMs);
                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                // Permanent error or all retry attempts exhausted — dead-letter immediately.
                // Permanent errors (validation, argument, null-ref) are not retried because
                // they will fail identically on every attempt.
                _logger.LogError(ex,
                    "OrderFilledEventHandler: {ErrorType} error for order {OrderId} on attempt {Attempt}/{Max} — dead-lettering event.",
                    IsTransient(ex) ? "final retry" : "permanent",
                    @event.OrderId, attempt, maxRetries);

                await TryDeadLetterAsync(@event, ex, attempt);
                return;
            }
        }
    }

    /// <summary>
    /// Determines whether an exception is transient and worth retrying.
    /// Validation, argument, and null-reference errors are permanent — they will
    /// fail identically on every retry and should be dead-lettered immediately.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex is not (
        ArgumentException or
        InvalidOperationException or
        NullReferenceException or
        FormatException);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> wraps a PostgreSQL unique-
    /// constraint violation (SQLSTATE 23505) on the OpenOrderId unique index.
    /// Used to distinguish the idempotent-race case (another concurrent delivery
    /// already inserted a Position for this order) from genuinely unexpected DB
    /// failures. Defensive — we accept any unique-violation exception here
    /// because if multiple handlers race the only index that can fire is
    /// <c>IX_Position_OpenOrderId</c>, and we still want the same idempotent
    /// treatment if the index name ever changes.
    /// </summary>
    private static bool IsUniqueOpenOrderIdViolation(DbUpdateException ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            // Npgsql's PostgresException exposes SqlState; detect by the portable
            // SQLSTATE code for unique_violation rather than by type name so this
            // works with provider wrappers (ResilientTransaction, retry proxies).
            var sqlState = cur.GetType().GetProperty("SqlState")?.GetValue(cur) as string;
            if (sqlState == "23505") return true;

            // Fallback: message-based detection for test doubles that don't
            // surface SqlState. Keep narrow so we don't swallow unrelated errors.
            var msg = cur.Message ?? string.Empty;
            if (msg.Contains("23505", StringComparison.Ordinal)
                || msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("IX_Position_OpenOrderId", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Core logic executed on each retry attempt. Creates a DI scope, loads the filled
    /// order, performs an idempotency check, then opens a <see cref="Position"/> and writes
    /// an audit log entry via MediatR.
    /// </summary>
    /// <param name="event">The <see cref="OrderFilledIntegrationEvent"/> to process.</param>
    private async Task HandleCoreAsync(OrderFilledIntegrationEvent @event)
    {
        // Structured logging scope — CorrelationId appears in every log line within this scope
        using var correlationScope = @event.CorrelationId is not null
            ? _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = @event.CorrelationId })
            : null;

        // Create a new DI scope so that EF Core DbContext instances are short-lived and
        // isolated to this single event invocation. The scope is disposed at the end of
        // the using block, releasing all scoped services and their database connections.
        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var mediator    = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Load the filled order to retrieve lot size, direction, SL/TP, and paper flag.
        // AsNoTracking is used because this is a read-only lookup — we never mutate the
        // Order entity here; mutation is handled by OrderExecutionWorker before publishing
        // the event.
        var order = await readContext.GetDbContext()
            .Set<Order>()
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == @event.OrderId && !o.IsDeleted);

        if (order is null)
        {
            // This should not normally happen because the event is published by
            // OrderExecutionWorker after it writes the fill to the Orders table.
            // A missing order could indicate a race condition or data corruption.
            _logger.LogWarning(
                "OrderFilledEventHandler: order {OrderId} not found — skipping position open",
                @event.OrderId);
            return;
        }

        // State-aware validation: verify the order is actually in Filled (or PartialFill)
        // status before opening a position. If the event arrived out of order (e.g., a
        // cancellation was processed before this fill event) the order may already be in a
        // terminal state. Processing a stale fill would create a phantom position.
        if (order.Status != OrderStatus.Filled && order.Status != OrderStatus.PartialFill)
        {
            _logger.LogWarning(
                "OrderFilledEventHandler: order {OrderId} is in status {Status}, expected Filled or PartialFill — " +
                "event may be out of order or already superseded, skipping",
                @event.OrderId, order.Status);
            return;
        }

        // Idempotency guard: skip if a position already exists for this order.
        // Prevents duplicate positions when the event bus retries delivery.
        // The check uses OpenOrderId (a foreign key on Position) rather than a dedicated
        // idempotency key table to keep the schema lightweight.
        //
        // NOTE: this AnyAsync is a best-effort fast path. Two concurrent deliveries can
        // both observe "no position" and both dispatch OpenPositionCommand, at which
        // point the unique index IX_Position_OpenOrderId enforces the invariant at the
        // DB layer — one insert wins, the other hits a unique-constraint violation.
        // The handler must catch that race below and treat it as "another delivery
        // won" rather than a permanent failure.
        bool positionExists = await readContext.GetDbContext()
            .Set<Position>()
            .AsNoTracking()
            .AnyAsync(p => p.OpenOrderId == @event.OrderId && !p.IsDeleted);

        if (positionExists)
        {
            _logger.LogWarning(
                "OrderFilledEventHandler: position already exists for order {OrderId} — skipping duplicate",
                @event.OrderId);
            return;
        }

        // OpenPositionCommand internally uses SaveAndPublish which opens its own
        // ResilientTransaction. Do NOT wrap in an outer BeginTransaction — nested
        // transactions with NpgsqlRetryingExecutionStrategy throw InvalidOperationException.

        // Derive position direction from the order type. OrderType.Buy → Long position
        // (profit when price rises); all other order types (Sell, SellLimit, etc.) → Short
        // (profit when price falls).
        string  direction = order.OrderType == OrderType.Buy ? "Long" : "Short";

        // Prefer the broker-confirmed FilledQuantity over the originally requested Quantity.
        // For partial fills, FilledQuantity < Quantity; the position is sized to what was
        // actually executed, not what was intended.
        decimal lots      = order.FilledQuantity ?? order.Quantity;

        // Dispatch via MediatR so that the OpenPositionCommandValidator runs first
        // (FluentValidation pipeline behaviour) and the write goes through
        // OpenPositionCommandHandler using the write DbContext.
        Lascodia.Trading.Engine.SharedApplication.Common.Models.ResponseData<long> result;
        try
        {
            result = await mediator.Send(new OpenPositionCommand
            {
                Symbol            = order.Symbol,
                Direction         = direction,
                OpenLots          = lots,
                // Use the actual broker fill price (which may differ from the requested price
                // due to slippage or market gap) so that P&L calculations are accurate.
                AverageEntryPrice = @event.FilledPrice,
                StopLoss          = order.StopLoss,
                TakeProfit        = order.TakeProfit,
                IsPaper           = order.IsPaper,
                // Link the position back to its originating order for idempotency checks and
                // for the audit trail (Position.OpenOrderId is the FK used by the guard above).
                OpenOrderId       = order.Id,
                // Carry the broker ticket so reconciliation against MT5 can match by
                // BrokerPositionId. Without this, snapshot reconciliation creates duplicate
                // Position rows (one with the ticket, one without) and the engine's
                // per-symbol open-position cap trips on the duplicates.
                BrokerPositionId  = @event.BrokerOrderId ?? order.BrokerOrderId,
            });
        }
        catch (DbUpdateException ex) when (IsUniqueOpenOrderIdViolation(ex))
        {
            // Race: another concurrent delivery of the same OrderFilledIntegrationEvent
            // already inserted a Position for this OpenOrderId and the unique index
            // (IX_Position_OpenOrderId) rejected our duplicate insert. This is the DESIRED
            // outcome of the invariant — treat it as success and move on, so the retry
            // loop does not dead-letter a genuinely idempotent race.
            _logger.LogInformation(
                "OrderFilledEventHandler: OpenOrderId unique-constraint race won by another delivery for order {OrderId} — treating as already-handled",
                @event.OrderId);
            return;
        }

        if (result.responseCode != "00")
        {
            // MediatR returned a non-success response code. This is treated as a
            // non-retryable failure at this point — the retry loop above will attempt
            // HandleCoreAsync again, but if the command handler consistently rejects
            // the request the event will ultimately be dropped.
            _logger.LogError(
                "OrderFilledEventHandler: failed to open position for order {OrderId} — {Message}",
                @event.OrderId, result.message);
            return;
        }

        long positionId = result.data;

        _logger.LogInformation(
            "OrderFilledEventHandler: opened {Direction} position {PositionId} for order {OrderId} " +
            "({Symbol} @ {Price:F5}, lots={Lots:F2})",
            direction, positionId, @event.OrderId, order.Symbol, @event.FilledPrice, lots);

        await mediator.Send(new LogDecisionCommand
        {
            EntityType   = "Order",
            EntityId     = @event.OrderId,
            DecisionType = "PositionOpened",
            Outcome      = $"Position {positionId} opened",
            Reason       = $"{order.OrderType} {order.Symbol} filled at {(@event.FilledPrice):F5} — " +
                           $"{direction} position opened (lots={lots:F2})",
            Source       = "OrderFilledEventHandler"
        });
    }

    /// <summary>
    /// Persists a failed event to the dead-letter table for manual inspection and replay.
    /// Best-effort — if this also fails, the event is truly lost (logged at Critical).
    /// </summary>
    private async Task TryDeadLetterAsync(OrderFilledIntegrationEvent @event, Exception ex, int attempts)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var deadLetterSink = scope.ServiceProvider.GetRequiredService<IDeadLetterSink>();

            await deadLetterSink.WriteAsync(
                handlerName:      nameof(OrderFilledEventHandler),
                eventType:        nameof(OrderFilledIntegrationEvent),
                eventPayloadJson: JsonSerializer.Serialize(@event),
                errorMessage:     ex.Message,
                stackTrace:       ex.StackTrace,
                attempts:         attempts);

            _logger.LogWarning(
                "OrderFilledEventHandler: dead-lettered event for order {OrderId} — stored for manual replay",
                @event.OrderId);
        }
        catch (Exception dlEx)
        {
            _logger.LogCritical(dlEx,
                "OrderFilledEventHandler: FAILED to dead-letter event for order {OrderId} — event may be lost. " +
                "Original error: {OriginalError}",
                @event.OrderId, ex.Message);
        }
    }
}
