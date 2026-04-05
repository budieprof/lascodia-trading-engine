using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.RiskProfiles.Commands.DeleteRiskProfile;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Soft-deletes a risk profile. The default risk profile cannot be deleted.</summary>
public class DeleteRiskProfileCommand : IRequest<ResponseData<string>>
{
    /// <summary>The unique identifier of the risk profile to delete.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class DeleteRiskProfileCommandValidator : AbstractValidator<DeleteRiskProfileCommand>
{
    public DeleteRiskProfileCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Marks the risk profile as soft-deleted. Rejects deletion of the default profile.</summary>
public class DeleteRiskProfileCommandHandler : IRequestHandler<DeleteRiskProfileCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeleteRiskProfileCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(DeleteRiskProfileCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.RiskProfile>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Risk profile not found", "-14");

        if (entity.IsDefault)
            return ResponseData<string>.Init(null, false, "Cannot delete the default risk profile", "-11");

        entity.IsDeleted = true;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
