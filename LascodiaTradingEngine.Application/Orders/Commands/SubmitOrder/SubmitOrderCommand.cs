using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.SubmitOrder;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Submits a Pending order to the broker and updates its status.</summary>
public class SubmitOrderCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class SubmitOrderCommandHandler : IRequestHandler<SubmitOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IBrokerOrderExecutor _broker;

    public SubmitOrderCommandHandler(IWriteApplicationDbContext context, IBrokerOrderExecutor broker)
    {
        _context = context;
        _broker  = broker;
    }

    public async Task<ResponseData<string>> Handle(SubmitOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (order is null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        if (order.Status != OrderStatus.Pending)
            return ResponseData<string>.Init(null, false, "Order is not in Pending status", "-11");

        var result = await _broker.SubmitOrderAsync(order, cancellationToken);

        if (result.Success)
        {
            order.Status         = result.FilledPrice.HasValue ? OrderStatus.Filled : OrderStatus.Submitted;
            order.BrokerOrderId  = result.BrokerOrderId;
            order.FilledPrice    = result.FilledPrice;
            order.FilledQuantity = result.FilledQuantity;
            order.FilledAt       = result.FilledPrice.HasValue ? DateTime.UtcNow : null;
        }
        else
        {
            order.Status          = OrderStatus.Rejected;
            order.RejectionReason = result.ErrorMessage;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init(null, result.Success, result.Success ? "Successful" : result.ErrorMessage ?? "Rejected", result.Success ? "00" : "-11");
    }
}
