using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Strategies.Commands.AssignRiskProfile;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Assigns (or clears) a risk profile on a strategy. Pass null for <c>RiskProfileId</c>
/// to remove the current assignment and fall back to the default risk profile.
/// </summary>
public class AssignRiskProfileCommand : IRequest<ResponseData<string>>
{
    /// <summary>Strategy to update.</summary>
    public long  StrategyId    { get; set; }
    /// <summary>Risk profile to assign, or null to clear the assignment.</summary>
    public long? RiskProfileId { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that StrategyId is positive and optional RiskProfileId is positive when provided.</summary>
public class AssignRiskProfileCommandValidator : AbstractValidator<AssignRiskProfileCommand>
{
    public AssignRiskProfileCommandValidator()
    {
        RuleFor(x => x.StrategyId).GreaterThan(0);
        RuleFor(x => x.RiskProfileId).GreaterThan(0).When(x => x.RiskProfileId.HasValue);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Updates the strategy's <c>RiskProfileId</c> foreign key. Returns not-found if the strategy does not exist.</summary>
public class AssignRiskProfileCommandHandler : IRequestHandler<AssignRiskProfileCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public AssignRiskProfileCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(AssignRiskProfileCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.StrategyId && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        entity.RiskProfileId = request.RiskProfileId;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("RiskProfile assigned", true, "Successful", "00");
    }
}
