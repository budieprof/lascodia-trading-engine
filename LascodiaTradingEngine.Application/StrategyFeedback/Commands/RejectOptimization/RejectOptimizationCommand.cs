using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Commands.RejectOptimization;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Rejects an optimization run, discarding the proposed parameter changes.</summary>
public class RejectOptimizationCommand : IRequest<ResponseData<string>>
{
    /// <summary>The unique identifier of the optimization run to reject.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RejectOptimizationCommandValidator : AbstractValidator<RejectOptimizationCommand>
{
    public RejectOptimizationCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Marks the optimization run as Rejected without modifying the strategy parameters.</summary>
public class RejectOptimizationCommandHandler : IRequestHandler<RejectOptimizationCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RejectOptimizationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(RejectOptimizationCommand request, CancellationToken cancellationToken)
    {
        var run = await _context.GetDbContext()
            .Set<Domain.Entities.OptimizationRun>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (run is null)
            return ResponseData<string>.Init(null, false, "Optimization run not found", "-14");

        run.Status = OptimizationRunStatus.Rejected;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Optimization run rejected", true, "Successful", "00");
    }
}
