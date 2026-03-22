using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.SubmitOrder;

// ── Result ────────────────────────────────────────────────────────────────────

/// <summary>
/// Returned by <see cref="SubmitOrderCommand"/> so callers can inspect the
/// post-submission state without re-querying the database.
/// </summary>
public record SubmitOrderResult
{
    public long            OrderId        { get; init; }
    public long            StrategyId     { get; init; }
    public string          Symbol         { get; init; } = string.Empty;
    public TradingSession  Session        { get; init; }
    public OrderStatus     Status         { get; init; }
    public decimal         RequestedPrice { get; init; }
    public decimal?        FilledPrice    { get; init; }
    public decimal?        FilledQuantity { get; init; }
    public decimal         Quantity       { get; init; }
    public DateTime?       FilledAt       { get; init; }
    public OrderType       OrderType      { get; init; }
}

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Submits a Pending order to the broker and updates its status.</summary>
public class SubmitOrderCommand : IRequest<ResponseData<SubmitOrderResult>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class SubmitOrderCommandHandler : IRequestHandler<SubmitOrderCommand, ResponseData<SubmitOrderResult>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IBrokerOrderExecutor _broker;

    public SubmitOrderCommandHandler(IWriteApplicationDbContext context, IBrokerOrderExecutor broker)
    {
        _context = context;
        _broker  = broker;
    }

    public async Task<ResponseData<SubmitOrderResult>> Handle(SubmitOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (order is null)
            return ResponseData<SubmitOrderResult>.Init(null, false, "Order not found", "-14");

        if (order.Status != OrderStatus.Pending)
            return ResponseData<SubmitOrderResult>.Init(null, false, "Order is not in Pending status", "-11");

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

        var submitResult = new SubmitOrderResult
        {
            OrderId        = order.Id,
            StrategyId     = order.StrategyId,
            Symbol         = order.Symbol,
            Session        = order.Session,
            Status         = order.Status,
            RequestedPrice = order.Price,
            FilledPrice    = order.FilledPrice,
            FilledQuantity = order.FilledQuantity,
            Quantity       = order.Quantity,
            FilledAt       = order.FilledAt,
            OrderType      = order.OrderType,
        };

        return ResponseData<SubmitOrderResult>.Init(
            submitResult,
            result.Success,
            result.Success ? "Successful" : result.ErrorMessage ?? "Rejected",
            result.Success ? "00" : "-11");
    }
}
