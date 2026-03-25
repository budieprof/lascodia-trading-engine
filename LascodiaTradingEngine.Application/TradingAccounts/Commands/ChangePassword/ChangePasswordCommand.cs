using System.Text.Json.Serialization;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.ChangePassword;

// ── Command ───────────────────────────────────────────────────────────────────

public class ChangePasswordCommand : IRequest<ResponseData<string>>
{
    [JsonIgnore] public long Id { get; set; }
    public string? CurrentPassword { get; set; }
    public required string NewPassword { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("NewPassword cannot be empty")
            .MinimumLength(8).WithMessage("NewPassword must be at least 8 characters")
            .MaximumLength(128).WithMessage("NewPassword cannot exceed 128 characters")
            .Matches(@"[A-Z]").WithMessage("NewPassword must contain at least one uppercase letter")
            .Matches(@"[a-z]").WithMessage("NewPassword must contain at least one lowercase letter")
            .Matches(@"\d").WithMessage("NewPassword must contain at least one digit")
            .Matches(@"[^a-zA-Z\d]").WithMessage("NewPassword must contain at least one special character");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ChangePasswordCommandHandler(
        IWriteApplicationDbContext context,
        IConfiguration configuration,
        IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _configuration  = configuration;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is not null && request.Id != callerAccountId)
            return ResponseData<string>.Init(null, false, "Unauthorized: cannot change another account's password", "-11");

        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Trading account not found", "-14");

        var encryptionKey = _configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured. Set the Encryption:Key configuration value.");

        // Current password is mandatory — prevents password change via stolen session
        if (string.IsNullOrEmpty(request.CurrentPassword))
            return ResponseData<string>.Init(null, false, "Current password is required to change password", "-11");

        var storedPassword = FieldEncryption.Decrypt(entity.EncryptedPassword, encryptionKey);
        if (storedPassword != request.CurrentPassword)
            return ResponseData<string>.Init(null, false, "Current password is incorrect", "-11");

        entity.EncryptedPassword = FieldEncryption.Encrypt(request.NewPassword, encryptionKey);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Updated", true, "Password changed successfully", "00");
    }
}
