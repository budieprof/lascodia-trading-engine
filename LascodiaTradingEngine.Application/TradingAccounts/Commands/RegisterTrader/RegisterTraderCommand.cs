using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.RegisterTrader;

// ── Command ───────────────────────────────────────────────────────────────────

public class RegisterTraderCommand : IRequest<ResponseData<AuthTokenResult>>
{
    public required string AccountId    { get; set; }
    public required string BrokerServer { get; set; }
    public required string BrokerName   { get; set; }
    public string?         AccountName  { get; set; }
    public string?         Password     { get; set; }
    public string          Currency     { get; set; } = "USD";
    public AccountType     AccountType  { get; set; } = AccountType.Demo;
    public decimal         Leverage     { get; set; }
    public MarginMode      MarginMode   { get; set; } = MarginMode.Hedging;
    public bool            IsPaper      { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RegisterTraderCommandValidator : AbstractValidator<RegisterTraderCommand>
{
    public RegisterTraderCommandValidator()
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

        RuleFor(x => x.Password)
            .MinimumLength(8).WithMessage("Password must be at least 8 characters")
            .MaximumLength(128).WithMessage("Password cannot exceed 128 characters")
            .Matches(@"[A-Z]").WithMessage("Password must contain at least one uppercase letter")
            .Matches(@"[a-z]").WithMessage("Password must contain at least one lowercase letter")
            .Matches(@"\d").WithMessage("Password must contain at least one digit")
            .Matches(@"[^a-zA-Z\d]").WithMessage("Password must contain at least one special character")
            .When(x => !string.IsNullOrEmpty(x.Password));

        RuleFor(x => x.Currency)
            .MaximumLength(3).WithMessage("Currency cannot exceed 3 characters");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RegisterTraderCommandHandler : IRequestHandler<RegisterTraderCommand, ResponseData<AuthTokenResult>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IConfiguration _configuration;

    public RegisterTraderCommandHandler(IWriteApplicationDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    public async Task<ResponseData<AuthTokenResult>> Handle(RegisterTraderCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();
        var encryptionKey = _configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured. Set the Encryption:Key configuration value.");

        // If account already exists, return existing API key + fresh JWT (supports multi-machine EA)
        var existing = await db.Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.AccountId == request.AccountId
                                   && x.BrokerServer == request.BrokerServer
                                   && !x.IsDeleted, cancellationToken);

        if (existing is not null)
        {
            string existingApiKey;
            if (!string.IsNullOrEmpty(existing.EncryptedApiKey))
            {
                existingApiKey = FieldEncryption.Decrypt(existing.EncryptedApiKey, encryptionKey);
            }
            else
            {
                // Legacy account without API key — generate one now
                existingApiKey = FieldEncryption.GenerateRandomPassword(64);
                existing.EncryptedApiKey = FieldEncryption.Encrypt(existingApiKey, encryptionKey);
                await _context.SaveChangesAsync(cancellationToken);
            }

            var existingToken = TradingAccountTokenGenerator.GenerateToken(existing, _configuration);
            existingToken.ApiKey = existingApiKey;
            return ResponseData<AuthTokenResult>.Init(existingToken, true, "Successful", "00");
        }

        var password = request.Password ?? FieldEncryption.GenerateRandomPassword();
        var encryptedPassword = FieldEncryption.Encrypt(password, encryptionKey);

        // Generate a 64-char API key for EA authentication
        var plainApiKey = FieldEncryption.GenerateRandomPassword(64);
        var encryptedApiKey = FieldEncryption.Encrypt(plainApiKey, encryptionKey);

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
            IsPaper           = request.IsPaper,
            EncryptedPassword = encryptedPassword,
            EncryptedApiKey   = encryptedApiKey,
            IsActive          = true,
        };

        await db.Set<Domain.Entities.TradingAccount>().AddAsync(entity, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var tokenResult = TradingAccountTokenGenerator.GenerateToken(entity, _configuration);
        tokenResult.ApiKey = plainApiKey;
        tokenResult.EncryptedApiKeyBlob = encryptedApiKey; // EA can store this opaque blob instead of plaintext

        return ResponseData<AuthTokenResult>.Init(tokenResult, true, "Successful", "00");
    }
}
