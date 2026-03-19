using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.CreateTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

public class CreateTradingAccountCommand : IRequest<ResponseData<long>>
{
    public long            BrokerId    { get; set; }
    public required string AccountId   { get; set; }
    public required string AccountName { get; set; }
    public string          Currency    { get; set; } = "USD";
    public bool            IsPaper     { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateTradingAccountCommandValidator : AbstractValidator<CreateTradingAccountCommand>
{
    public CreateTradingAccountCommandValidator()
    {
        RuleFor(x => x.BrokerId)
            .GreaterThan(0).WithMessage("BrokerId must be greater than zero");

        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage("AccountId cannot be empty")
            .MaximumLength(100).WithMessage("AccountId cannot exceed 100 characters");

        RuleFor(x => x.AccountName)
            .NotEmpty().WithMessage("AccountName cannot be empty")
            .MaximumLength(200).WithMessage("AccountName cannot exceed 200 characters");

        RuleFor(x => x.Currency)
            .MaximumLength(3).WithMessage("Currency cannot exceed 3 characters");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class CreateTradingAccountCommandHandler : IRequestHandler<CreateTradingAccountCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public CreateTradingAccountCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(CreateTradingAccountCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.TradingAccount
        {
            BrokerId    = request.BrokerId,
            AccountId   = request.AccountId,
            AccountName = request.AccountName,
            Currency    = request.Currency,
            IsPaper     = request.IsPaper,
            IsActive    = false
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
