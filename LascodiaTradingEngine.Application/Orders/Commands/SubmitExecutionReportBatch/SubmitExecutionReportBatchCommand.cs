using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.SubmitExecutionReportBatch;

// ── DTO ──────────────────────────────────────────────────────────────────────

public class ExecutionReportItem
{
    public long     OrderId         { get; set; }
    public string?  BrokerOrderId   { get; set; }
    public decimal? FilledPrice     { get; set; }
    public decimal? FilledQuantity  { get; set; }
    public required string Status   { get; set; }  // "Filled" | "Rejected" | "Cancelled"
    public string?  RejectionReason { get; set; }
    public DateTime? FilledAt       { get; set; }
}

// ── Command ──────────────────────────────────────────────────────────────────

public class SubmitExecutionReportBatchCommand : IRequest<ResponseData<int>>
{
    public List<ExecutionReportItem> Reports { get; set; } = new();
}

// ── Validator ────────────────────────────────────────────────────────────────

public class SubmitExecutionReportBatchCommandValidator : AbstractValidator<SubmitExecutionReportBatchCommand>
{
    public SubmitExecutionReportBatchCommandValidator()
    {
        RuleFor(x => x.Reports)
            .NotNull().WithMessage("Reports list cannot be null")
            .Must(r => r.Count <= 100).WithMessage("Execution report batch cannot exceed 100 items");

        RuleForEach(x => x.Reports).ChildRules(report =>
        {
            report.RuleFor(r => r.OrderId)
                .GreaterThan(0).WithMessage("Order Id must be greater than zero");

            report.RuleFor(r => r.Status)
                .NotEmpty().WithMessage("Status cannot be empty")
                .Must(s => s is "Filled" or "Rejected" or "Cancelled" or "PartialFill" or "Failed")
                .WithMessage("Status must be Filled, Rejected, Cancelled, PartialFill, or Failed");
        });
    }
}

// ── Handler ──────────────────────────────────────────────────────────────────

public class SubmitExecutionReportBatchCommandHandler : IRequestHandler<SubmitExecutionReportBatchCommand, ResponseData<int>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public SubmitExecutionReportBatchCommandHandler(IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
    }

    public async Task<ResponseData<int>> Handle(SubmitExecutionReportBatchCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();
        int processed = 0;

        foreach (var report in request.Reports)
        {
            var entity = await dbContext
                .Set<Domain.Entities.Order>()
                .FirstOrDefaultAsync(
                    x => x.Id == report.OrderId && !x.IsDeleted,
                    cancellationToken);

            if (entity == null)
                continue; // Skip unknown orders in batch

            if (!Enum.TryParse<OrderStatus>(report.Status, ignoreCase: true, out var newStatus))
                continue;

            var previousStatus = entity.Status;
            entity.Status          = newStatus;
            entity.BrokerOrderId   = report.BrokerOrderId ?? entity.BrokerOrderId;
            entity.FilledPrice     = report.FilledPrice ?? entity.FilledPrice;
            entity.FilledQuantity  = report.FilledQuantity ?? entity.FilledQuantity;
            entity.RejectionReason = report.RejectionReason ?? entity.RejectionReason;
            entity.FilledAt        = report.FilledAt ?? entity.FilledAt;

            if (newStatus == OrderStatus.Filled && previousStatus != OrderStatus.Filled)
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

            processed++;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<int>.Init(processed, true, "Successful", "00");
    }
}
