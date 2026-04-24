using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.LogoutTradingAccount;

namespace LascodiaTradingEngine.IntegrationTest.Fixtures;

public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "IntegrationTest";
    public const string AuthorizationHeaderValue = "Test integration-user";

    /// <summary>
    /// Custom header tests can set to inject role claims onto the synthetic principal,
    /// enabling RBAC policy assertions without minting real JWTs. Comma-separated; e.g.
    /// <c>X-Test-Roles: Operator,Admin</c>. When absent, the principal is authenticated
    /// but carries no role — exercising the "deny" branch of any policy-protected endpoint.
    /// </summary>
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>
    /// Optional header carrying a synthetic <c>jti</c> claim that's embedded on the
    /// principal. Lets logout-revocation tests replay the same "token" across multiple
    /// requests — the handler mirrors the production JWT middleware's revocation check
    /// against <see cref="IMemoryCache"/>, so a hit on <c>/auth/logout</c> makes
    /// subsequent requests with this header fail with 401.
    /// </summary>
    public const string JtiHeader = "X-Test-Jti";

    private readonly IMemoryCache _cache;

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IMemoryCache cache)
        : base(options, logger, encoder)
    {
        _cache = cache;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorizationHeader)
            || authorizationHeader.Count == 0
            || authorizationHeader[0] != AuthorizationHeaderValue)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? jti = null;
        if (Request.Headers.TryGetValue(JtiHeader, out var jtiHeader)
            && jtiHeader.Count > 0
            && !string.IsNullOrWhiteSpace(jtiHeader[0]))
        {
            jti = jtiHeader[0];

            // Mirror the JWT middleware's OnTokenValidated revocation check so the
            // logout flow can be asserted end-to-end without minting real RFC 7519
            // tokens. Cache-only check is sufficient — LogoutTradingAccountCommandHandler
            // warms the cache unconditionally, so we don't need the DB fallback here.
            if (_cache.TryGetValue(LogoutTradingAccountCommandHandler.RevokedCacheKeyPrefix + jti, out _))
                return Task.FromResult(AuthenticateResult.Fail("Token has been revoked."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "integration-user"),
            new(ClaimTypes.Name,            "Integration User"),
            new("tradingAccountId",         "1"),
        };

        if (jti is not null)
            claims.Add(new Claim(JwtRegisteredClaimNames.Jti, jti));

        if (Request.Headers.TryGetValue(RolesHeader, out var rolesHeader)
            && rolesHeader.Count > 0)
        {
            foreach (var role in rolesHeader[0]!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity  = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket    = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
