using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.UpdateTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>Partially updates a trading account's display name, currency, or paper trading flag.</summary>
public class UpdateTradingAccountCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore] public long    Id          { get; set; }
    public string?  AccountName { get; set; }
    public string?  Currency    { get; set; }
    public bool?    IsPaper     { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class UpdateTradingAccountCommandValidator : AbstractValidator<UpdateTradingAccountCommand>
{
    public UpdateTradingAccountCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0);
        RuleFor(x => x.AccountName).MaximumLength(100).When(x => x.AccountName is not null);
        RuleFor(x => x.Currency).MaximumLength(10).When(x => x.Currency is not null);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Applies partial updates to the trading account; only non-null fields are written.</summary>
public class UpdateTradingAccountCommandHandler : IRequestHandler<UpdateTradingAccountCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateTradingAccountCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateTradingAccountCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Trading account not found", "-14");

        if (request.AccountName != null) entity.AccountName = request.AccountName;
        if (request.Currency    != null) entity.Currency    = request.Currency;
        if (request.IsPaper     != null) entity.IsPaper     = request.IsPaper.Value;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Successful", "00");
    }
}
