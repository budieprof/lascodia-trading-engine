using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.CancelOrder;

/// <summary>
/// Cancels a pending, submitted, or partially-filled order. If the order has been sent to the
/// broker, an <see cref="Domain.Entities.EACommand"/> is queued so the EA cancels it on MT5.
/// </summary>
public class CancelOrderCommand : IRequest<ResponseData<string>>
{
    /// <summary>Order identifier to cancel.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the order Id is a positive value.</summary>
public class CancelOrderCommandValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

/// <summary>
/// Sets the order status to Cancelled, enforces ownership via <see cref="IEAOwnershipGuard"/>,
/// and queues an EA command to cancel the order on MT5 if it has a broker ticket.
/// </summary>
public class CancelOrderCommandHandler : IRequestHandler<CancelOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public CancelOrderCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        var order = await dbContext
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (order is null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is not null && order.TradingAccountId != callerAccountId)
            return ResponseData<string>.Init(null, false, "Unauthorized: order belongs to another account", "-11");

        if (order.Status is not (OrderStatus.Pending or OrderStatus.Submitted or OrderStatus.PartialFill))
            return ResponseData<string>.Init(null, false, "Order cannot be cancelled in its current status", "-11");

        // If the order has been submitted to the broker (has a ticket), queue an EACommand
        // so the EA can cancel it on MT5.
        if (!string.IsNullOrEmpty(order.BrokerOrderId))
        {
            // Find the EA instance that owns this symbol
            var eaInstance = await dbContext
                .Set<Domain.Entities.EAInstance>()
                .ActiveForSymbol(order.Symbol)
                .FirstOrDefaultAsync(cancellationToken);

            if (eaInstance is null)
                return ResponseData<string>.Init(null, false, "No active EA instance found for symbol " + order.Symbol, "-11");

            await dbContext.Set<Domain.Entities.EACommand>().AddAsync(new Domain.Entities.EACommand
            {
                TargetInstanceId = eaInstance.InstanceId,
                CommandType      = EACommandType.CancelOrder,
                TargetTicket     = long.TryParse(order.BrokerOrderId, out var ticket) ? ticket : null,
                Symbol           = order.Symbol,
                Parameters       = JsonSerializer.Serialize(new { orderId = order.Id }),
            }, cancellationToken);
        }

        order.Status = OrderStatus.Cancelled;
        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
