using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.CurrencyPairs.Commands.DeleteCurrencyPair;

// ── Command ───────────────────────────────────────────────────────────────────

public class DeleteCurrencyPairCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class DeleteCurrencyPairCommandValidator : AbstractValidator<DeleteCurrencyPairCommand>
{
    public DeleteCurrencyPairCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class DeleteCurrencyPairCommandHandler : IRequestHandler<DeleteCurrencyPairCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeleteCurrencyPairCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(DeleteCurrencyPairCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.CurrencyPair>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity is null)
            return ResponseData<string>.Init(null, false, "Currency pair not found", "-14");

        entity.IsDeleted = true;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
