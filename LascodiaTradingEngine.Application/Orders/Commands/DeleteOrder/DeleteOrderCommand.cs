using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Orders.Commands.DeleteOrder;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Soft-deletes an order by setting its <c>IsDeleted</c> flag.</summary>
public class DeleteOrderCommand : IRequest<ResponseData<string>>
{
    /// <summary>Order identifier (populated from route).</summary>
    [JsonIgnore] public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the order Id is a positive value.</summary>
public class DeleteOrderCommandValidator : AbstractValidator<DeleteOrderCommand>
{
    public DeleteOrderCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Marks the order as soft-deleted. Returns not-found if the order does not exist.</summary>
public class DeleteOrderCommandHandler : IRequestHandler<DeleteOrderCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IIntegrationEventService _eventService;

    public DeleteOrderCommandHandler(
        IWriteApplicationDbContext context,
        IIntegrationEventService eventService)
    {
        _context = context;
        _eventService = eventService;
    }

    public async Task<ResponseData<string>> Handle(DeleteOrderCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Order>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Order not found", "-14");

        entity.IsDeleted = true;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
