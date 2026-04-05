using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.EconomicEvents.Commands.UpdateEconomicEventActual;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Updates the actual released value for a scheduled economic event after its announcement.
/// </summary>
public class UpdateEconomicEventActualCommand : IRequest<ResponseData<string>>
{
    /// <summary>The unique identifier of the economic event.</summary>
    public long   Id     { get; set; }
    /// <summary>The actual released value (e.g., "3.5%").</summary>
    public required string Actual { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateEconomicEventActualCommandValidator : AbstractValidator<UpdateEconomicEventActualCommand>
{
    public UpdateEconomicEventActualCommandValidator()
    {
        RuleFor(x => x.Id)
            .GreaterThan(0).WithMessage("Id must be greater than zero");

        RuleFor(x => x.Actual)
            .NotEmpty().WithMessage("Actual value is required");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Writes the actual value to the economic event entity after the data is released.</summary>
public class UpdateEconomicEventActualCommandHandler : IRequestHandler<UpdateEconomicEventActualCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateEconomicEventActualCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateEconomicEventActualCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.EconomicEvent>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Economic event not found", "-14");

        entity.Actual = request.Actual;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Actual value updated successfully", true, "Successful", "00");
    }
}
