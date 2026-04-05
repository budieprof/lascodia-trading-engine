using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.ModifyOrder;

/// <summary>
/// Modifies the stop-loss and/or take-profit levels of an existing order. If the order
/// has been submitted to the broker, an EA command is queued to update SL/TP on MT5.
/// </summary>
public class ModifyOrderCommand : IRequest<ResponseData<string>>
{
    /// <summary>Order identifier (populated from route).</summary>
    [JsonIgnore]
    public long Id { get; set; }
    /// <summary>New stop-loss price level (null to leave unchanged).</summary>
    public decimal? StopLoss   { get; set; }
    /// <summary>New take-profit price level (null to leave unchanged).</summary>
    public decimal? TakeProfit { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the order Id is positive and optional SL/TP values are positive when provided.</summary>
public class ModifyOrderCommandValidator : AbstractValidator<ModifyOrderCommand>
{
    public ModifyOrderCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.StopLoss).GreaterThan(0).When(x => x.StopLoss.HasValue);
        RuleFor(x => x.TakeProfit).GreaterThan(0).When(x => x.TakeProfit.HasValue);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Updates SL/TP on the order entity and queues a <c>ModifySLTP</c> EA command
/// if the order has been submitted to the broker. Enforces account ownership.
/// </summary>
public class ModifyOrderCommandHandler : IRequestHandler<ModifyOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ModifyOrderCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ModifyOrderCommand request, CancellationToken cancellationToken)
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

        // If the order has been submitted to the broker, queue an EACommand
        // so the EA can modify SL/TP on MT5.
        if (!string.IsNullOrEmpty(order.BrokerOrderId))
        {
            var eaInstance = await dbContext
                .Set<Domain.Entities.EAInstance>()
                .ActiveForSymbol(order.Symbol)
                .FirstOrDefaultAsync(cancellationToken);

            if (eaInstance is null)
                return ResponseData<string>.Init(null, false, "No active EA instance found for symbol " + order.Symbol, "-11");

            await dbContext.Set<Domain.Entities.EACommand>().AddAsync(new Domain.Entities.EACommand
            {
                TargetInstanceId = eaInstance.InstanceId,
                CommandType      = EACommandType.ModifySLTP,
                TargetTicket     = long.TryParse(order.BrokerOrderId, out var ticket) ? ticket : null,
                Symbol           = order.Symbol,
                Parameters       = JsonSerializer.Serialize(new
                {
                    stopLoss   = request.StopLoss,
                    takeProfit = request.TakeProfit,
                }),
            }, cancellationToken);
        }

        order.StopLoss   = request.StopLoss   ?? order.StopLoss;
        order.TakeProfit = request.TakeProfit  ?? order.TakeProfit;

        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
