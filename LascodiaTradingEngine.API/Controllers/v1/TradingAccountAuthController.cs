using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.RegisterTrader;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.LoginTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.LogoutTradingAccount;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Handles unauthenticated trading account registration and login endpoints.
/// EA instances use passwordless login; web dashboard requires password. Rate-limited to resist brute-force.
/// Route: api/v1/lascodia-trading-engine/auth
/// </summary>
[Route("api/v1/lascodia-trading-engine/auth")]
[ApiController]
public class TradingAccountAuthController : ControllerBase
{
    private readonly ISender _mediator;

    public TradingAccountAuthController(ISender mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Register a new trading account and auto-login</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ResponseData<AuthTokenResult>> Register(RegisterTraderCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<AuthTokenResult>.Init(null, false, "Model state failed", "-11");

        return await _mediator.Send(command);
    }

    /// <summary>Login with trading account credentials</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ResponseData<AuthTokenResult>> Login(LoginTradingAccountCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<AuthTokenResult>.Init(null, false, "Model state failed", "-11");

        return await _mediator.Send(command);
    }

    /// <summary>
    /// Revokes the bearer token used to make this call. After logout the same JWT cannot
    /// be reused, even before its natural expiry. Idempotent.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<ResponseData<string>> Logout()
    {
        var jti = User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti))
            return ResponseData<string>.Init(null, false, "Token has no jti claim.", "-11");

        if (!long.TryParse(User.FindFirst("tradingAccountId")?.Value, out var accountId))
            return ResponseData<string>.Init(null, false, "Token has no tradingAccountId claim.", "-11");

        // `exp` is a Unix timestamp (seconds since epoch) per RFC 7519.
        var expClaim = User.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
        var expiresAt = long.TryParse(expClaim, out var expSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime
            : DateTime.UtcNow.AddDays(1);

        return await _mediator.Send(new LogoutTradingAccountCommand
        {
            Jti              = jti,
            TradingAccountId = accountId,
            ExpiresAt        = expiresAt,
            Reason           = "UserLogout",
        });
    }
}
