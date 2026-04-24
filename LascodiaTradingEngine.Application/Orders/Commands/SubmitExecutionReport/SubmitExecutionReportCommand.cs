using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReport;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Submitted by the EA after it executes an order on MT5.
/// Updates the order with broker-side fill details (ticket, price, quantity).
/// </summary>
public class SubmitExecutionReportCommand : IRequest<ResponseData<string>>
{
    public long     Id             { get; set; }
    public string?  BrokerOrderId  { get; set; }
    public decimal? FilledPrice    { get; set; }
    public decimal? FilledQuantity { get; set; }
    public required string Status  { get; set; }  // "Filled" | "Rejected" | "Cancelled"
    public string?  RejectionReason { get; set; }
    public DateTime? FilledAt      { get; set; }

    // ── EA telemetry fields ─────────────────────────────────────────────────
    public long?    SignalId           { get; set; }
    public long?    Mt5DealTicket      { get; set; }
    public long?    MagicNumber        { get; set; }
    public decimal? RequestedPrice     { get; set; }
    public decimal? SlippagePips       { get; set; }
    public int?     SlippagePoints     { get; set; }
    public decimal? Commission         { get; set; }
    public int?     ExecutionLatencyMs { get; set; }
    public int?     QueueDwellMs       { get; set; }
    public string?  FillPolicy         { get; set; }
    public string?  AccountMode        { get; set; }
    public int?     BrokerRetcode      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the order Id is positive and Status is one of Filled, Rejected, or Cancelled.</summary>
public class SubmitExecutionReportCommandValidator : AbstractValidator<SubmitExecutionReportCommand>
{
    public SubmitExecutionReportCommandValidator()
    {
        RuleFor(x => x.Id)
            .GreaterThan(0).WithMessage("Order Id must be greater than zero");

        RuleFor(x => x.Status)
            .NotEmpty().WithMessage("Status cannot be empty")
            .Must(s => s is "Filled" or "Rejected" or "Cancelled" or "PartialFill" or "Failed")
            .WithMessage("Status must be Filled, Rejected, Cancelled, PartialFill, or Failed");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Applies the EA execution report to the order entity. On first transition to Filled,
/// publishes an <see cref="OrderFilledIntegrationEvent"/> to trigger downstream position management.
/// </summary>
public class SubmitExecutionReportCommandHandler : IRequestHandler<SubmitExecutionReportCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public SubmitExecutionReportCommandHandler(IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<string>> Handle(SubmitExecutionReportCommand request, CancellationToken cancellationToken)
    {
        var ctx = _context.GetDbContext();
        var newStatus = Enum.Parse<OrderStatus>(request.Status, ignoreCase: true);

        // ── Atomic idempotency for Filled transitions ──────────────────────────
        // Two concurrent EA reports for the same order previously both observed
        // previousStatus != Filled and both published OrderFilledIntegrationEvent,
        // double-triggering downstream position management. Use a DB-level CAS
        // (WHERE Status != Filled) so only the winning UPDATE affects a row; the
        // loser sees 0 rows and skips the publish. Tracked-entity updates are
        // still used for non-Filled transitions since they don't emit events.
        if (newStatus == OrderStatus.Filled)
        {
            int rows = await ctx.Set<Domain.Entities.Order>()
                .Where(o => o.Id == request.Id && o.Status != OrderStatus.Filled && !o.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status,          OrderStatus.Filled)
                    .SetProperty(o => o.BrokerOrderId,   o => request.BrokerOrderId   ?? o.BrokerOrderId)
                    .SetProperty(o => o.FilledPrice,     o => request.FilledPrice     ?? o.FilledPrice)
                    .SetProperty(o => o.FilledQuantity,  o => request.FilledQuantity  ?? o.FilledQuantity)
                    .SetProperty(o => o.RejectionReason, o => request.RejectionReason ?? o.RejectionReason)
                    .SetProperty(o => o.FilledAt,        o => request.FilledAt        ?? o.FilledAt),
                    cancellationToken);

            if (rows == 0)
            {
                // Either the order doesn't exist or it is already Filled. Distinguish
                // so legitimate "already filled" retries return success (idempotent).
                bool exists = await ctx.Set<Domain.Entities.Order>().AsNoTracking()
                    .AnyAsync(o => o.Id == request.Id && !o.IsDeleted, cancellationToken);
                return exists
                    ? ResponseData<string>.Init(null, true, "Successful (already filled — idempotent)", "00")
                    : ResponseData<string>.Init(null, false, "Order not found", "-14");
            }

            // Won the CAS. Re-load canonical row to build the event payload.
            var entity = await ctx.Set<Domain.Entities.Order>().AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == request.Id && !o.IsDeleted, cancellationToken);
            if (entity is null)
                return ResponseData<string>.Init(null, false, "Order not found", "-14");

            var filledPrice = entity.FilledPrice ?? 0;
            var filledQty   = entity.FilledQuantity ?? 0;
            var fillRate    = entity.Quantity > 0 ? filledQty / entity.Quantity : 1m;

            await _eventBus.SaveAndPublish(_context, new OrderFilledIntegrationEvent
            {
                OrderId            = entity.Id,
                StrategyId         = entity.StrategyId,
                Symbol             = entity.Symbol,
                Session            = entity.Session,
                RequestedPrice     = request.RequestedPrice ?? entity.Price,
                FilledPrice        = filledPrice,
                WasPartialFill     = fillRate < 1m,
                FillRate           = fillRate,
                FilledAt           = entity.FilledAt ?? DateTime.UtcNow,
                SubmitToFillMs     = request.ExecutionLatencyMs ?? 0,
                SlippagePips       = request.SlippagePips,
                Commission         = request.Commission,
                QueueDwellMs       = request.QueueDwellMs,
                BrokerRetcode      = request.BrokerRetcode,
                BrokerOrderId      = entity.BrokerOrderId,
            });

            return ResponseData<string>.Init(null, true, "Successful", "00");
        }

        // ── Non-Filled transitions (Rejected/Cancelled/PartialFill/Failed) ─────
        // These do not emit integration events, so tracked-entity updates are safe.
        var nonFilledEntity = await ctx.Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(o => o.Id == request.Id && !o.IsDeleted, cancellationToken);
        if (nonFilledEntity is null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        nonFilledEntity.Status          = newStatus;
        nonFilledEntity.BrokerOrderId   = request.BrokerOrderId   ?? nonFilledEntity.BrokerOrderId;
        nonFilledEntity.FilledPrice     = request.FilledPrice     ?? nonFilledEntity.FilledPrice;
        nonFilledEntity.FilledQuantity  = request.FilledQuantity  ?? nonFilledEntity.FilledQuantity;
        nonFilledEntity.RejectionReason = request.RejectionReason ?? nonFilledEntity.RejectionReason;
        nonFilledEntity.FilledAt        = request.FilledAt        ?? nonFilledEntity.FilledAt;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
