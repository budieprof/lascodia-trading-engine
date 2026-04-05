using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
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

/// <summary>
/// Marks a Pending order as Submitted so the EA can discover and execute it on MT5.
/// The EA polls for approved trade signals, executes them, and reports back via
/// <c>SubmitExecutionReportCommand</c>.
/// </summary>
public class SubmitOrderCommand : IRequest<ResponseData<SubmitOrderResult>>
{
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the order Id is a positive value.</summary>
public class SubmitOrderCommandValidator : AbstractValidator<SubmitOrderCommand>
{
    public SubmitOrderCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Atomically transitions a Pending order to Submitted status using a WHERE guard to
/// prevent race conditions. Enforces account ownership via <see cref="IEAOwnershipGuard"/>.
/// </summary>
public class SubmitOrderCommandHandler : IRequestHandler<SubmitOrderCommand, ResponseData<SubmitOrderResult>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public SubmitOrderCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<SubmitOrderResult>> Handle(SubmitOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (order is null)
            return ResponseData<SubmitOrderResult>.Init(null, false, "Order not found", "-14");

        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is not null && order.TradingAccountId != callerAccountId)
            return ResponseData<SubmitOrderResult>.Init(null, false, "Unauthorized: order belongs to another account", "-11");

        if (order.Status != OrderStatus.Pending)
            return ResponseData<SubmitOrderResult>.Init(null, false, "Order is not in Pending status", "-11");

        // Atomic status transition using a WHERE guard to prevent check-then-act race conditions.
        // If another thread changed the status between our read and this update, zero rows are affected.
        int affected = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .Where(x => x.Id == request.Id && x.Status == OrderStatus.Pending && !x.IsDeleted)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Submitted), cancellationToken);

        if (affected == 0)
            return ResponseData<SubmitOrderResult>.Init(null, false, "Order status changed concurrently — not Pending anymore", "-11");

        // Refresh the in-memory entity to reflect the DB state
        order.Status = OrderStatus.Submitted;

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

        return ResponseData<SubmitOrderResult>.Init(submitResult, true, "Successful", "00");
    }
}
