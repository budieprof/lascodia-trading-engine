using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.SyncAccountBalance;

// ── Command ───────────────────────────────────────────────────────────────────

public class SyncAccountBalanceCommand : IRequest<ResponseData<string>>
{
    public long    Id              { get; set; }
    public decimal Balance         { get; set; }
    public decimal Equity          { get; set; }
    public decimal MarginUsed      { get; set; }
    public decimal MarginAvailable { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class SyncAccountBalanceCommandValidator : AbstractValidator<SyncAccountBalanceCommand>
{
    public SyncAccountBalanceCommandValidator()
    {
        RuleFor(x => x.Id)
            .GreaterThan(0).WithMessage("Id must be greater than zero");

        RuleFor(x => x.Balance)
            .GreaterThanOrEqualTo(0).WithMessage("Balance must be greater than or equal to zero");

        RuleFor(x => x.Equity)
            .GreaterThanOrEqualTo(0).WithMessage("Equity must be greater than or equal to zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class SyncAccountBalanceCommandHandler : IRequestHandler<SyncAccountBalanceCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public SyncAccountBalanceCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(SyncAccountBalanceCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Trading account not found", "-14");

        entity.Balance         = request.Balance;
        entity.Equity          = request.Equity;
        entity.MarginUsed      = request.MarginUsed;
        entity.MarginAvailable = request.MarginAvailable;
        entity.LastSyncedAt    = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Synced", true, "Successful", "00");
    }
}
