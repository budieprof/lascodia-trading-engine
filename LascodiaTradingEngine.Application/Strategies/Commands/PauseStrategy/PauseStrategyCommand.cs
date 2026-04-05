using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Commands.PauseStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Pauses an active strategy, preventing it from generating new trade signals.</summary>
public class PauseStrategyCommand : IRequest<ResponseData<string>>
{
    /// <summary>Strategy identifier to pause.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the strategy Id is a positive value.</summary>
public class PauseStrategyCommandValidator : AbstractValidator<PauseStrategyCommand>
{
    public PauseStrategyCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Sets the strategy status to Paused. Returns not-found if the strategy does not exist.</summary>
public class PauseStrategyCommandHandler : IRequestHandler<PauseStrategyCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public PauseStrategyCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(PauseStrategyCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        entity.Status = StrategyStatus.Paused;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Paused", true, "Successful", "00");
    }
}
