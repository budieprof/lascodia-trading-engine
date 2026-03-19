using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.ActivateTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

public class ActivateTradingAccountCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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

        // Deactivate all accounts for the same broker
        var siblings = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .Where(x => x.BrokerId == target.BrokerId && x.IsActive && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var account in siblings)
            account.IsActive = false;

        target.IsActive = true;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Activated", true, "Successful", "00");
    }
}
