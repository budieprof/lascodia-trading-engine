using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Security;

/// <summary>
/// Generates JWT tokens scoped to a single <see cref="TradingAccount"/>.
/// </summary>
public static class TradingAccountTokenGenerator
{
    public static AuthTokenResult GenerateToken(TradingAccount account, IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection("JwtSettings");
        var secretKey  = jwtSection["SecretKey"]
            ?? throw new InvalidOperationException("JwtSettings:SecretKey is not configured.");

        var issuer   = jwtSection["Issuer"]   ?? "lascodia-trading-engine";
        var audience = jwtSection["Audience"] ?? "lascodia-trading-engine-api";
        var expirationMinutes = int.TryParse(jwtSection["ExpirationMinutes"], out var exp) ? exp : 480;

        var signingKey  = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("tradingAccountId", account.Id.ToString()),
            new("accountType",      account.AccountType.ToString()),
            new(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer:             issuer,
            audience:           audience,
            claims:             claims,
            expires:            DateTime.UtcNow.AddMinutes(expirationMinutes),
            signingCredentials: credentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return new AuthTokenResult
        {
            Token     = tokenString,
            ExpiresAt = token.ValidTo,
            TokenType = "Bearer",
            Account   = new AuthAccountSummary
            {
                Id           = account.Id,
                AccountId    = account.AccountId,
                AccountName  = account.AccountName,
                BrokerServer = account.BrokerServer,
                BrokerName   = account.BrokerName,
                AccountType  = account.AccountType,
                Currency     = account.Currency,
            }
        };
    }
}

public class AuthTokenResult
{
    public string Token        { get; set; } = string.Empty;
    public DateTime ExpiresAt  { get; set; }
    public string TokenType    { get; set; } = "Bearer";
    public AuthAccountSummary Account { get; set; } = null!;

    /// <summary>
    /// Plain-text EA API key. Only populated on registration or key rotation —
    /// never returned on regular login. The EA must store this and send it
    /// on every subsequent <c>POST /auth/login</c> with <c>loginSource=ea</c>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Server-encrypted API key blob (AES-256-GCM). The EA stores this opaque string
    /// on disk instead of the plain-text API key. On login, the EA sends this blob
    /// and the engine decrypts it server-side. This eliminates plaintext API keys
    /// on the EA's filesystem. Only populated on registration or key rotation.
    /// </summary>
    public string? EncryptedApiKeyBlob { get; set; }
}

public class AuthAccountSummary
{
    public long   Id           { get; set; }
    public string AccountId    { get; set; } = string.Empty;
    public string AccountName  { get; set; } = string.Empty;
    public string BrokerServer { get; set; } = string.Empty;
    public string BrokerName   { get; set; } = string.Empty;
    public AccountType AccountType  { get; set; }
    public string Currency     { get; set; } = string.Empty;
}
