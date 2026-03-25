using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.SyncAccountBalance;

// ── Command ───────────────────────────────────────────────────────────────────

public class SyncAccountBalanceCommand : IRequest<ResponseData<string>>
{
    public long       Id              { get; set; }
    public decimal    Balance         { get; set; }
    public decimal    Equity          { get; set; }
    public decimal    MarginUsed      { get; set; }
    public decimal    MarginAvailable { get; set; }
    public decimal?   Leverage        { get; set; }
    public MarginMode? MarginMode     { get; set; }
    public decimal?   MarginLevel     { get; set; }
    public decimal?   Profit          { get; set; }
    public decimal?   Credit          { get; set; }
    public string?    MarginSoMode    { get; set; }
    public decimal?   MarginSoCall    { get; set; }
    public decimal?   MarginSoStopOut { get; set; }
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

        RuleFor(x => x.Leverage)
            .GreaterThan(0).WithMessage("Leverage must be greater than zero")
            .When(x => x.Leverage.HasValue);

        RuleFor(x => x.MarginLevel)
            .GreaterThanOrEqualTo(0).WithMessage("MarginLevel must be greater than or equal to zero")
            .When(x => x.MarginLevel.HasValue);

        RuleFor(x => x.MarginSoMode)
            .Must(v => v is "Percent" or "Money").WithMessage("MarginSoMode must be 'Percent' or 'Money'")
            .When(x => x.MarginSoMode != null);
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

        if (request.Leverage.HasValue)
            entity.Leverage = request.Leverage.Value;

        if (request.MarginMode.HasValue)
            entity.MarginMode = request.MarginMode.Value;

        if (request.MarginLevel.HasValue)
            entity.MarginLevel = request.MarginLevel.Value;

        if (request.Profit.HasValue)
            entity.Profit = request.Profit.Value;

        if (request.Credit.HasValue)
            entity.Credit = request.Credit.Value;

        if (request.MarginSoMode != null)
            entity.MarginSoMode = request.MarginSoMode;

        if (request.MarginSoCall.HasValue)
            entity.MarginSoCall = request.MarginSoCall.Value;

        if (request.MarginSoStopOut.HasValue)
            entity.MarginSoStopOut = request.MarginSoStopOut.Value;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Synced", true, "Successful", "00");
    }
}
