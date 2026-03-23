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
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class SubmitExecutionReportCommandValidator : AbstractValidator<SubmitExecutionReportCommand>
{
    public SubmitExecutionReportCommandValidator()
    {
        RuleFor(x => x.Id)
            .GreaterThan(0).WithMessage("Order Id must be greater than zero");

        RuleFor(x => x.Status)
            .NotEmpty().WithMessage("Status cannot be empty")
            .Must(s => s is "Filled" or "Rejected" or "Cancelled")
            .WithMessage("Status must be Filled, Rejected, or Cancelled");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(
                x => x.Id == request.Id && !x.IsDeleted,
                cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        var newStatus = Enum.Parse<OrderStatus>(request.Status, ignoreCase: true);
        entity.Status          = newStatus;
        entity.BrokerOrderId   = request.BrokerOrderId ?? entity.BrokerOrderId;
        entity.FilledPrice     = request.FilledPrice ?? entity.FilledPrice;
        entity.FilledQuantity  = request.FilledQuantity ?? entity.FilledQuantity;
        entity.RejectionReason = request.RejectionReason ?? entity.RejectionReason;
        entity.FilledAt        = request.FilledAt ?? entity.FilledAt;

        if (newStatus == OrderStatus.Filled)
        {
            var filledPrice = entity.FilledPrice ?? 0;
            var filledQty   = entity.FilledQuantity ?? 0;
            var fillRate    = entity.Quantity > 0 ? filledQty / entity.Quantity : 1m;

            await _eventBus.SaveAndPublish(_context, new OrderFilledIntegrationEvent
            {
                OrderId        = entity.Id,
                StrategyId     = entity.StrategyId,
                Symbol         = entity.Symbol,
                Session        = entity.Session,
                RequestedPrice = entity.Price,
                FilledPrice    = filledPrice,
                WasPartialFill = fillRate < 1m,
                FillRate       = fillRate,
                FilledAt       = entity.FilledAt ?? DateTime.UtcNow,
            });
        }
        else
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
