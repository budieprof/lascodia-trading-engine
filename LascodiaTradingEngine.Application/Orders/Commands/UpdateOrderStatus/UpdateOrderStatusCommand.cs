using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.UpdateOrderStatus;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Transitions an order to a new status and optionally updates broker-side fill details.
/// Used by internal workers to reflect execution outcomes.
/// </summary>
public class UpdateOrderStatusCommand : IRequest<ResponseData<string>>
{
    /// <summary>Order identifier.</summary>
    public long    Id             { get; set; }
    /// <summary>Target <see cref="OrderStatus"/> as a string enum name.</summary>
    public string  Status         { get; set; } = string.Empty;
    /// <summary>Broker-assigned order ticket (e.g. MT5 ticket number).</summary>
    public string? BrokerOrderId  { get; set; }
    /// <summary>Actual fill price from the broker.</summary>
    public decimal? FilledPrice   { get; set; }
    /// <summary>Actual filled quantity from the broker.</summary>
    public decimal? FilledQuantity { get; set; }
    /// <summary>Reason the order was rejected, if applicable.</summary>
    public string? RejectionReason { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that Id is positive and Status is a valid <see cref="OrderStatus"/> enum name.</summary>
public class UpdateOrderStatusCommandValidator : AbstractValidator<UpdateOrderStatusCommand>
{
    public UpdateOrderStatusCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(s => Enum.TryParse<OrderStatus>(s, ignoreCase: true, out _))
            .WithMessage($"Status must be a valid OrderStatus value: {string.Join(", ", Enum.GetNames<OrderStatus>())}");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Applies the status transition and updates fill details (price, quantity, timestamp) when provided.</summary>
public class UpdateOrderStatusCommandHandler : IRequestHandler<UpdateOrderStatusCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateOrderStatusCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateOrderStatusCommand request, CancellationToken cancellationToken)
    {
        var order = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (order is null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        order.Status          = Enum.Parse<OrderStatus>(request.Status, ignoreCase: true);
        order.BrokerOrderId   = request.BrokerOrderId ?? order.BrokerOrderId;
        order.RejectionReason = request.RejectionReason ?? order.RejectionReason;

        if (request.FilledPrice.HasValue)
        {
            order.FilledPrice    = request.FilledPrice;
            order.FilledQuantity = request.FilledQuantity;
            order.FilledAt       = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
