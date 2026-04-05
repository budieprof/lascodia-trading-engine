using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.DeleteTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Soft-deletes a trading account. Active accounts cannot be deleted.</summary>
public class DeleteTradingAccountCommand : IRequest<ResponseData<string>>
{
    /// <summary>The unique identifier of the trading account to delete.</summary>
    public long Id { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class DeleteTradingAccountCommandValidator : AbstractValidator<DeleteTradingAccountCommand>
{
    public DeleteTradingAccountCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Marks the trading account as soft-deleted. Rejects deletion of the currently active account.</summary>
public class DeleteTradingAccountCommandHandler : IRequestHandler<DeleteTradingAccountCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeleteTradingAccountCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(DeleteTradingAccountCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Trading account not found", "-14");

        if (entity.IsActive)
            return ResponseData<string>.Init(null, false, "Cannot delete the active trading account", "-11");

        entity.IsDeleted = true;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
