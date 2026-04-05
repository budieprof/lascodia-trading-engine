using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.ActivateTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Activates a trading account and deactivates all other accounts, ensuring only one is active at a time.</summary>
public class ActivateTradingAccountCommand : IRequest<ResponseData<string>>
{
    /// <summary>The unique identifier of the trading account to activate.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ActivateTradingAccountCommandValidator : AbstractValidator<ActivateTradingAccountCommand>
{
    public ActivateTradingAccountCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Sets the target account as active and deactivates all sibling accounts in a single transaction.</summary>
public class ActivateTradingAccountCommandHandler : IRequestHandler<ActivateTradingAccountCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ActivateTradingAccountCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ActivateTradingAccountCommand request, CancellationToken cancellationToken)
    {
        var target = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (target == null)
            return ResponseData<string>.Init(null, false, "Trading account not found", "-14");

        // Deactivate all other active accounts
        var siblings = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .Where(x => x.Id != target.Id && x.IsActive && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var account in siblings)
            account.IsActive = false;

        target.IsActive = true;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Activated", true, "Successful", "00");
    }
}
