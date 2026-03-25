using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.DeadLetters.Commands.ResolveDeadLetter;

// ── Command ───────────────────────────────────────────────────────────────────

public class ResolveDeadLetterCommand : IRequest<ResponseData<bool>>
{
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ResolveDeadLetterCommandValidator : AbstractValidator<ResolveDeadLetterCommand>
{
    public ResolveDeadLetterCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ResolveDeadLetterCommandHandler
    : IRequestHandler<ResolveDeadLetterCommand, ResponseData<bool>>
{
    private readonly IWriteApplicationDbContext _context;

    public ResolveDeadLetterCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<bool>> Handle(
        ResolveDeadLetterCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<DeadLetterEvent>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<bool>.Init(false, false, "Dead letter event not found", "-14");

        if (entity.IsResolved)
            return ResponseData<bool>.Init(true, true, "Already resolved", "00");

        entity.IsResolved = true;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<bool>.Init(true, true, "Successful", "00");
    }
}
