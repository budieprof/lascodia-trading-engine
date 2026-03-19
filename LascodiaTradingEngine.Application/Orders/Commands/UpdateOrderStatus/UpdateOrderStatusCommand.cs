using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.UpdateOrderStatus;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateOrderStatusCommand : IRequest<ResponseData<string>>
{
    public long    Id             { get; set; }
    public string  Status         { get; set; } = string.Empty;
    public string? BrokerOrderId  { get; set; }
    public decimal? FilledPrice   { get; set; }
    public decimal? FilledQuantity { get; set; }
    public string? RejectionReason { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

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
