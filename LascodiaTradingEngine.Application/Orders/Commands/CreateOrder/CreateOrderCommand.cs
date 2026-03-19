using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;

// ── Command ───────────────────────────────────────────────────────────────────

public class CreateOrderCommand : IRequest<ResponseData<long>>
{
    public long?   TradeSignalId  { get; set; }
    public long    StrategyId     { get; set; }
    public long    TradingAccountId { get; set; }
    public required string Symbol { get; set; }
    public required string OrderType { get; set; }    // "Buy" | "Sell"
    public string  ExecutionType  { get; set; } = "Market";  // "Market" | "Limit" | "Stop" | "StopLimit"
    public decimal Quantity       { get; set; }
    public decimal Price          { get; set; }  // 0 for Market orders
    public decimal? StopLoss      { get; set; }
    public decimal? TakeProfit    { get; set; }
    public bool    IsPaper        { get; set; }
    public string? Notes          { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(10).WithMessage("Symbol cannot exceed 10 characters");

        RuleFor(x => x.OrderType)
            .NotEmpty().WithMessage("OrderType cannot be empty")
            .Must(t => t == "Buy" || t == "Sell").WithMessage("OrderType must be 'Buy' or 'Sell'");

        RuleFor(x => x.ExecutionType)
            .Must(t => t is "Market" or "Limit" or "Stop" or "StopLimit")
            .WithMessage("ExecutionType must be Market, Limit, Stop, or StopLimit");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be greater than zero");

        // Price = 0 is valid for Market orders
        RuleFor(x => x.Price)
            .GreaterThanOrEqualTo(0).WithMessage("Price cannot be negative");

        // Limit/Stop orders require a price
        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be set for non-Market orders")
            .When(x => x.ExecutionType != "Market");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateOrderCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Order
        {
            TradeSignalId    = request.TradeSignalId,
            StrategyId       = request.StrategyId,
            TradingAccountId = request.TradingAccountId,
            Symbol           = request.Symbol.ToUpperInvariant(),
            OrderType        = Enum.Parse<OrderType>(request.OrderType, ignoreCase: true),
            ExecutionType    = Enum.Parse<ExecutionType>(request.ExecutionType, ignoreCase: true),
            Quantity         = request.Quantity,
            Price            = request.Price,
            StopLoss         = request.StopLoss,
            TakeProfit       = request.TakeProfit,
            IsPaper          = request.IsPaper,
            Notes            = request.Notes,
            Status           = OrderStatus.Pending,
            CreatedAt        = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
