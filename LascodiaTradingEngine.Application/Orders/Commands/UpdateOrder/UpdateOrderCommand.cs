using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Orders.Commands.UpdateOrder;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Partially updates an existing order. Only non-null fields are applied.
/// </summary>
public class UpdateOrderCommand : IRequest<ResponseData<string>>
{
    /// <summary>Order identifier (populated from route).</summary>
    [JsonIgnore] public long Id { get; set; }

    /// <summary>Updated currency pair symbol.</summary>
    public string? Symbol { get; set; }
    /// <summary>Updated trade direction ("Buy" or "Sell").</summary>
    public string? OrderType { get; set; }
    /// <summary>Updated lot size / quantity.</summary>
    public decimal? Quantity { get; set; }
    /// <summary>Updated requested price.</summary>
    public decimal? Price { get; set; }
    /// <summary>Updated order status as a string enum name.</summary>
    public string? Status { get; set; }
    /// <summary>Updated free-text notes.</summary>
    public string? Notes { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates <see cref="UpdateOrderCommand"/> ensuring the Id is present and optional fields have valid values.</summary>
public class UpdateOrderCommandValidator : AbstractValidator<UpdateOrderCommand>
{
    public UpdateOrderCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Order Id is required");

        When(x => x.OrderType != null, () =>
            RuleFor(x => x.OrderType)
                .Must(t => t == "Buy" || t == "Sell").WithMessage("OrderType must be 'Buy' or 'Sell'"));

        When(x => x.Quantity.HasValue, () =>
            RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("Quantity must be greater than zero"));

        When(x => x.Price.HasValue, () =>
            RuleFor(x => x.Price).GreaterThan(0).WithMessage("Price must be greater than zero"));
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Applies partial updates to an existing order entity. Returns not-found if the order does not exist or is soft-deleted.</summary>
public class UpdateOrderCommandHandler : IRequestHandler<UpdateOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventService;

    public UpdateOrderCommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService eventService)
    {
        _context = context;
        _eventService = eventService;
    }

    public async Task<ResponseData<string>> Handle(UpdateOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        if (!string.IsNullOrWhiteSpace(request.Symbol))    entity.Symbol    = request.Symbol;
        if (!string.IsNullOrWhiteSpace(request.OrderType)) entity.OrderType = Enum.Parse<OrderType>(request.OrderType, ignoreCase: true);
        if (request.Quantity.HasValue)                     entity.Quantity  = request.Quantity.Value;
        if (request.Price.HasValue)                        entity.Price     = request.Price.Value;
        if (!string.IsNullOrWhiteSpace(request.Status))    entity.Status    = Enum.Parse<OrderStatus>(request.Status, ignoreCase: true);
        if (request.Notes != null)                         entity.Notes     = request.Notes;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
