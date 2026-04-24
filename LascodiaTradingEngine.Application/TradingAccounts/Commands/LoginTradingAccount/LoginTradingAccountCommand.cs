using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Bridge.Options;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.Application.TradingAccounts.Commands.LoginTradingAccount;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Authenticates a trading account. Web logins require a password; EA logins authenticate
/// via API key (plaintext or server-encrypted blob). Returns a JWT scoped to the account.
/// </summary>
public class LoginTradingAccountCommand : IRequest<ResponseData<AuthTokenResult>>
{
    /// <summary>The broker-assigned account identifier.</summary>
    public required string AccountId    { get; set; }
    /// <summary>The broker server address for account lookup.</summary>
    public required string BrokerServer { get; set; }
    /// <summary>Password for web login (required when LoginSource is "web").</summary>
    public string?         Password     { get; set; }
    /// <summary>Plaintext API key for EA login.</summary>
    public string?         ApiKey       { get; set; }
    /// <summary>
    /// Server-encrypted API key blob. The EA can send this instead of the plain-text
    /// ApiKey. The engine decrypts it server-side and validates against the stored key.
    /// This allows the EA to store the opaque blob on disk instead of a plaintext secret.
    /// </summary>
    public string?         EncryptedApiKeyBlob { get; set; }
    public string          LoginSource  { get; set; } = "web";
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class LoginTradingAccountCommandValidator : AbstractValidator<LoginTradingAccountCommand>
{
    public LoginTradingAccountCommandValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage("AccountId cannot be empty");

        RuleFor(x => x.BrokerServer)
            .NotEmpty().WithMessage("BrokerServer cannot be empty");

        RuleFor(x => x.LoginSource)
            .NotEmpty().WithMessage("LoginSource cannot be empty")
            .Must(x => x is "web" or "ea").WithMessage("LoginSource must be 'web' or 'ea'");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Validates credentials (password for web, API key or encrypted blob for EA), checks
/// account active status, generates a JWT token, and optionally includes bridge endpoint info.
/// </summary>
public class LoginTradingAccountCommandHandler : IRequestHandler<LoginTradingAccountCommand, ResponseData<AuthTokenResult>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly BridgeOptions _bridgeOptions;

    public LoginTradingAccountCommandHandler(
        IReadApplicationDbContext context,
        IConfiguration configuration,
        IOptions<BridgeOptions> bridgeOptions)
    {
        _context       = context;
        _configuration = configuration;
        _bridgeOptions = bridgeOptions.Value;
    }

    public async Task<ResponseData<AuthTokenResult>> Handle(LoginTradingAccountCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradingAccount>()
            .FirstOrDefaultAsync(x => x.AccountId == request.AccountId
                                   && x.BrokerServer == request.BrokerServer
                                   && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<AuthTokenResult>.Init(null, false, "Trading account not found", "-14");

        // Deactivated accounts cannot login
        if (!entity.IsActive)
            return ResponseData<AuthTokenResult>.Init(null, false, "Account is deactivated", "-11");

        var encryptionKey = _configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured. Set the Encryption:Key configuration value.");

        // Web login requires password
        if (request.LoginSource == "web")
        {
            if (string.IsNullOrEmpty(request.Password))
                return ResponseData<AuthTokenResult>.Init(null, false, "Password is required for web login", "-11");

            var storedPassword = FieldEncryption.Decrypt(entity.EncryptedPassword, encryptionKey);
            if (storedPassword != request.Password)
                return ResponseData<AuthTokenResult>.Init(null, false, "Invalid credentials", "-11");
        }
        else if (request.LoginSource == "ea")
        {
            if (string.IsNullOrEmpty(entity.EncryptedApiKey))
                return ResponseData<AuthTokenResult>.Init(null, false, "Account has no API key — re-register to obtain one", "-11");

            var storedApiKey = FieldEncryption.Decrypt(entity.EncryptedApiKey, encryptionKey);

            // Accept either: (a) encrypted blob from disk, or (b) plain-text API key
            string? presentedKey = null;

            if (!string.IsNullOrEmpty(request.EncryptedApiKeyBlob))
            {
                // EA sent the server-encrypted blob — decrypt it to recover the plain key
                try
                {
                    presentedKey = FieldEncryption.Decrypt(request.EncryptedApiKeyBlob, encryptionKey);
                }
                catch
                {
                    return ResponseData<AuthTokenResult>.Init(null, false, "Invalid encrypted API key blob — re-register or rotate key", "-11");
                }
            }
            else if (!string.IsNullOrEmpty(request.ApiKey))
            {
                presentedKey = request.ApiKey;
            }
            else
            {
                return ResponseData<AuthTokenResult>.Init(null, false, "ApiKey or EncryptedApiKeyBlob is required for EA login", "-11");
            }

            if (storedApiKey != presentedKey)
                return ResponseData<AuthTokenResult>.Init(null, false, "Invalid API key", "-11");
        }

        // EA tokens always carry a single, fixed role so the EA cannot be locked out by
        // a misconfigured operator-role grant. Web tokens get the union of the account's
        // OperatorRole rows, defaulting to Viewer when nothing is granted.
        IEnumerable<string> roles;
        if (request.LoginSource == "ea")
        {
            roles = new[] { OperatorRoleNames.EA };
        }
        else
        {
            var grants = await _context.GetDbContext()
                .Set<Domain.Entities.OperatorRole>()
                .AsNoTracking()
                .Where(x => x.TradingAccountId == entity.Id)
                .Select(x => x.Role)
                .ToListAsync(cancellationToken);

            roles = grants.Count > 0 ? grants : new List<string> { OperatorRoleNames.Viewer };
        }

        var tokenResult = TradingAccountTokenGenerator.GenerateToken(entity, _configuration, roles);

        // Advertise bridge endpoint when the bridge is enabled
        if (_bridgeOptions.Enabled)
        {
            tokenResult.BridgeHost = _bridgeOptions.EffectiveAdvertisedHost;
            tokenResult.BridgePort = _bridgeOptions.Port;
        }

        return ResponseData<AuthTokenResult>.Init(tokenResult, true, "Successful", "00");
    }
}
