using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.StrategyEnsemble.Commands.UpdateStrategyAllocation;

// ── Command ───────────────────────────────────────────────────────────────────

public class UpdateStrategyAllocationCommand : IRequest<ResponseData<string>>
{
    public long    StrategyId        { get; set; }
    public decimal Weight            { get; set; }
    public decimal RollingSharpRatio { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateStrategyAllocationCommandValidator : AbstractValidator<UpdateStrategyAllocationCommand>
{
    public UpdateStrategyAllocationCommandValidator()
    {
        RuleFor(x => x.StrategyId)
            .GreaterThan(0).WithMessage("StrategyId must be greater than zero");

        RuleFor(x => x.Weight)
            .InclusiveBetween(0, 1).WithMessage("Weight must be between 0 and 1");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class UpdateStrategyAllocationCommandHandler
    : IRequestHandler<UpdateStrategyAllocationCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateStrategyAllocationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(
        UpdateStrategyAllocationCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var allocation = await db.Set<Domain.Entities.StrategyAllocation>()
            .FirstOrDefaultAsync(x => x.StrategyId == request.StrategyId && !x.IsDeleted, cancellationToken);

        if (allocation == null)
        {
            allocation = new Domain.Entities.StrategyAllocation
            {
                StrategyId = request.StrategyId
            };
            await db.Set<Domain.Entities.StrategyAllocation>().AddAsync(allocation, cancellationToken);
        }

        allocation.Weight            = request.Weight;
        allocation.RollingSharpRatio = request.RollingSharpRatio;
        allocation.LastRebalancedAt  = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Allocation updated", true, "Successful", "00");
    }
}
