using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.RotateApiKey;

// ── Command ───────────────────────────────────────────────────────────────────

public class RotateApiKeyCommand : IRequest<ResponseData<RotateApiKeyResult>>
{
    public long Id { get; set; }
}

public class RotateApiKeyResult
{
    public string ApiKey { get; set; } = string.Empty;
    public string EncryptedApiKeyBlob { get; set; } = string.Empty;
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class RotateApiKeyCommandValidator : AbstractValidator<RotateApiKeyCommand>
{
    public RotateApiKeyCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("Id must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RotateApiKeyCommandHandler : IRequestHandler<RotateApiKeyCommand, ResponseData<RotateApiKeyResult>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public RotateApiKeyCommandHandler(
        IWriteApplicationDbContext context,
        IConfiguration configuration,
        IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _configuration  = configuration;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<RotateApiKeyResult>> Handle(RotateApiKeyCommand request, CancellationToken cancellationToken)
    {
        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is not null && request.Id != callerAccountId)
            return ResponseData<RotateApiKeyResult>.Init(null, false, "Unauthorized: cannot rotate another account's API key", "-11");

        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<RotateApiKeyResult>.Init(null, false, "Trading account not found", "-14");

        var encryptionKey = _configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured. Set the Encryption:Key configuration value.");
        var plainApiKey = FieldEncryption.GenerateRandomPassword(64);
        entity.EncryptedApiKey = FieldEncryption.Encrypt(plainApiKey, encryptionKey);

        await _context.SaveChangesAsync(cancellationToken);

        // Return both the plain key (for backwards compat) and the encrypted blob
        // that the EA can store on disk instead of the plaintext key.
        var encryptedBlob = FieldEncryption.Encrypt(plainApiKey, encryptionKey);

        return ResponseData<RotateApiKeyResult>.Init(
            new RotateApiKeyResult { ApiKey = plainApiKey, EncryptedApiKeyBlob = encryptedBlob },
            true, "API key rotated successfully", "00");
    }
}
