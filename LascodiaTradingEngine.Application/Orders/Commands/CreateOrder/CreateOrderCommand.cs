using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Orders.Commands.CreateOrder;

// ── Command ───────────────────────────────────────────────────────────────────

public class CreateOrderCommand : IRequest<ResponseData<long>>
{
    [JsonIgnore]
    public int BusinessId { get; set; }

    public required string Symbol { get; set; }
    public required string OrderType { get; set; }   // Buy / Sell
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string? Notes { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty().WithMessage("Symbol cannot be empty")
            .MaximumLength(20).WithMessage("Symbol cannot exceed 20 characters");

        RuleFor(x => x.OrderType)
            .NotEmpty().WithMessage("OrderType cannot be empty")
            .Must(t => t == "Buy" || t == "Sell").WithMessage("OrderType must be 'Buy' or 'Sell'");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("Quantity must be greater than zero");

        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventService;

    public CreateOrderCommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService eventService)
    {
        _context = context;
        _eventService = eventService;
    }

    public async Task<ResponseData<long>> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.Order
        {
            BusinessId = request.BusinessId,
            Symbol = request.Symbol,
            OrderType = request.OrderType,
            Quantity = request.Quantity,
            Price = request.Price,
            Notes = request.Notes,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
