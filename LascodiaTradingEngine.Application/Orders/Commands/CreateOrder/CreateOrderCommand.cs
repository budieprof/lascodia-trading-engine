using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates a new trading order with the specified parameters. The order is persisted
/// in Pending status and an <see cref="OrderCreatedIntegrationEvent"/> is published.
/// </summary>
public class CreateOrderCommand : IRequest<ResponseData<long>>
{
    /// <summary>Optional originating trade signal identifier.</summary>
    public long?   TradeSignalId  { get; set; }
    /// <summary>Strategy that generated this order.</summary>
    public long    StrategyId     { get; set; }
    /// <summary>Trading account under which the order is placed.</summary>
    public long    TradingAccountId { get; set; }
    /// <summary>Currency pair symbol (e.g. "EURUSD").</summary>
    public required string Symbol { get; set; }
    /// <summary>Trade direction: "Buy" or "Sell".</summary>
    public required string OrderType { get; set; }    // "Buy" | "Sell"
    /// <summary>Execution method: "Market", "Limit", "Stop", or "StopLimit".</summary>
    public string  ExecutionType  { get; set; } = "Market";  // "Market" | "Limit" | "Stop" | "StopLimit"
    /// <summary>Order lot size / quantity.</summary>
    public decimal Quantity       { get; set; }
    /// <summary>Requested price. Zero for Market orders.</summary>
    public decimal Price          { get; set; }  // 0 for Market orders
    /// <summary>Optional stop-loss price level.</summary>
    public decimal? StopLoss      { get; set; }
    /// <summary>Optional take-profit price level.</summary>
    public decimal? TakeProfit    { get; set; }
    /// <summary>Whether this is a paper-trading (simulated) order.</summary>
    public bool    IsPaper        { get; set; }
    /// <summary>Free-text notes attached to the order.</summary>
    public string? Notes          { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates <see cref="CreateOrderCommand"/> inputs including symbol, order type, execution type, quantity, and price constraints.</summary>
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

/// <summary>Persists a new order in Pending status and publishes an <see cref="OrderCreatedIntegrationEvent"/> via the event bus.</summary>
public class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventBus;

    public CreateOrderCommandHandler(IWriteApplicationDbContext context, IIntegrationEventService eventBus)
    {
        _context  = context;
        _eventBus = eventBus;
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

        await _eventBus.SaveAndPublish(_context, new OrderCreatedIntegrationEvent
        {
            OrderId          = entity.Id,
            TradeSignalId    = entity.TradeSignalId,
            StrategyId       = entity.StrategyId,
            TradingAccountId = entity.TradingAccountId,
            Symbol           = entity.Symbol,
            OrderType        = entity.OrderType,
            ExecutionType    = entity.ExecutionType,
            Quantity         = entity.Quantity,
            Price            = entity.Price,
            IsPaper          = entity.IsPaper,
            CreatedAt        = entity.CreatedAt,
        });

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
