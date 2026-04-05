using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.CreateTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Creates a new trading account with an auto-generated AES-256-GCM encrypted password and API key.
/// Rejects duplicate AccountId+BrokerServer combinations.
/// </summary>
public class CreateTradingAccountCommand : IRequest<ResponseData<long>>
{
    public required string AccountId    { get; set; }
    public required string BrokerServer { get; set; }
    public required string BrokerName   { get; set; }
    public string?         AccountName  { get; set; }
    public string          Currency     { get; set; } = "USD";
    public AccountType     AccountType  { get; set; } = AccountType.Demo;
    public decimal         Leverage     { get; set; }
    public MarginMode      MarginMode   { get; set; } = MarginMode.Hedging;
    public bool            IsPaper      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class CreateTradingAccountCommandValidator : AbstractValidator<CreateTradingAccountCommand>
{
    public CreateTradingAccountCommandValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage("AccountId cannot be empty")
            .MaximumLength(100).WithMessage("AccountId cannot exceed 100 characters");

        RuleFor(x => x.BrokerServer)
            .NotEmpty().WithMessage("BrokerServer cannot be empty")
            .MaximumLength(200).WithMessage("BrokerServer cannot exceed 200 characters");

        RuleFor(x => x.BrokerName)
            .NotEmpty().WithMessage("BrokerName cannot be empty")
            .MaximumLength(100).WithMessage("BrokerName cannot exceed 100 characters");

        RuleFor(x => x.Currency)
            .MaximumLength(3).WithMessage("Currency cannot exceed 3 characters");

        RuleFor(x => x.Leverage)
            .GreaterThan(0).WithMessage("Leverage must be greater than 0")
            .LessThanOrEqualTo(500).WithMessage("Leverage cannot exceed 500:1 (regulatory ceiling)");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Checks for duplicate accounts, generates encrypted credentials, and persists the new trading account.
/// The account is created as inactive by default.
/// </summary>
public class CreateTradingAccountCommandHandler : IRequestHandler<CreateTradingAccountCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IConfiguration _configuration;

    public CreateTradingAccountCommandHandler(IWriteApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<ResponseData<long>> Handle(CreateTradingAccountCommand request, CancellationToken cancellationToken)
    {
        var exists = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .AnyAsync(x => x.AccountId == request.AccountId
                        && x.BrokerServer == request.BrokerServer
                        && !x.IsDeleted, cancellationToken);

        if (exists)
            return ResponseData<long>.Init(0, false, "Account already exists for this broker server", "-11");

        var encryptionKey = _configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured. Set the Encryption:Key configuration value.");
        var defaultPassword = FieldEncryption.GenerateRandomPassword();
        var encryptedPassword = FieldEncryption.Encrypt(defaultPassword, encryptionKey);
        var encryptedApiKey = FieldEncryption.Encrypt(FieldEncryption.GenerateRandomPassword(64), encryptionKey);

        var entity = new Domain.Entities.TradingAccount
        {
            AccountId         = request.AccountId,
            BrokerServer      = request.BrokerServer,
            BrokerName        = request.BrokerName,
            AccountName       = request.AccountName ?? $"Account {request.AccountId}",
            Currency          = request.Currency,
            AccountType       = request.AccountType,
            Leverage          = request.Leverage,
            MarginMode        = request.MarginMode,
            EncryptedPassword = encryptedPassword,
            EncryptedApiKey   = encryptedApiKey,
            IsPaper           = request.IsPaper,
            IsActive          = false
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Successful", "00");
    }
}
