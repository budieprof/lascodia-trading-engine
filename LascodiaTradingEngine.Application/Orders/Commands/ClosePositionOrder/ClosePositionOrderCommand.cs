using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.ClosePositionOrder;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Creates a counter-order to close (or partially close) an open position.</summary>
public class ClosePositionOrderCommand : IRequest<ResponseData<long>>
{
    public string  Symbol       { get; set; } = string.Empty;  // From position
    public string  Direction    { get; set; } = string.Empty;  // Counter-direction: if position is Long → "Sell"
    public decimal Quantity     { get; set; }
    public bool    IsPaper      { get; set; }
    public string? Notes        { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ClosePositionOrderCommandValidator : AbstractValidator<ClosePositionOrderCommand>
{
    public ClosePositionOrderCommandValidator()
    {
        RuleFor(x => x.Symbol).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Direction)
            .NotEmpty()
            .Must(d => d == "Buy" || d == "Sell")
            .WithMessage("Direction must be 'Buy' or 'Sell'");
        RuleFor(x => x.Quantity).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ClosePositionOrderCommandHandler : IRequestHandler<ClosePositionOrderCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public ClosePositionOrderCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(ClosePositionOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Order
        {
            Symbol        = request.Symbol.ToUpperInvariant(),
            OrderType     = Enum.Parse<OrderType>(request.Direction, ignoreCase: true),
            ExecutionType = ExecutionType.Market,
            Quantity      = request.Quantity,
            Price         = 0,
            Status        = OrderStatus.Pending,
            IsPaper       = request.IsPaper,
            Notes         = request.Notes ?? "Close position order",
            CreatedAt     = DateTime.UtcNow
        };

        await _context.GetDbContext().Set<Domain.Entities.Order>().AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
