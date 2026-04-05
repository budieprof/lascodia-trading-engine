using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Marks a trade signal as expired, preventing further execution attempts.</summary>
public class ExpireTradeSignalCommand : IRequest<ResponseData<string>>
{
    /// <summary>Trade signal identifier to expire.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>Validates that the trade signal Id is a positive value.</summary>
public class ExpireTradeSignalCommandValidator : AbstractValidator<ExpireTradeSignalCommand>
{
    public ExpireTradeSignalCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Transitions the signal to Expired status regardless of its current state.</summary>
public class ExpireTradeSignalCommandHandler : IRequestHandler<ExpireTradeSignalCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ExpireTradeSignalCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ExpireTradeSignalCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradeSignal>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Trade signal not found", "-14");

        entity.Status = TradeSignalStatus.Expired;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Expired", true, "Successful", "00");
    }
}
