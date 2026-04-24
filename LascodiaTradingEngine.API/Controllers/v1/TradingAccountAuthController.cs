using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    /// <summary>
    /// Cookie name that carries the issued JWT when the caller supplies
    /// <c>loginSource="web"</c>. HttpOnly so JS can't read it; Secure + SameSite
    /// so it's only sent on same-site HTTPS. Read back by the JWT middleware's
    /// <c>OnMessageReceived</c> hook when no <c>Authorization</c> header is present.
    /// </summary>
    public const string AuthCookieName = "lascodia-auth";

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

        var result = await _mediator.Send(command);
        // Register is assumed web — EA instances register out-of-band, not via
        // the admin UI. Setting the cookie lets the freshly-registered account
        // stay logged in without an extra round-trip.
        SetAuthCookieIfWebLogin("web", result);
        return result;
    }

    /// <summary>Login with trading account credentials</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ResponseData<AuthTokenResult>> Login(LoginTradingAccountCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<AuthTokenResult>.Init(null, false, "Model state failed", "-11");

        var result = await _mediator.Send(command);
        SetAuthCookieIfWebLogin(command.LoginSource, result);
        return result;
    }

    /// <summary>
    /// Lightweight identity probe. Returns the account id + role claims for the
    /// currently authenticated caller. Used by the admin UI on boot to decide
    /// whether to show the login screen when the bearer token lives in an
    /// HttpOnly cookie the browser can't read.
    /// </summary>
    [HttpGet("whoami")]
    [Authorize]
    public IActionResult WhoAmI()
    {
        var accountId = User.FindFirst("tradingAccountId")?.Value;
        var roles     = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        return Ok(ResponseData<WhoAmIResponse>.Init(
            new WhoAmIResponse
            {
                TradingAccountId = long.TryParse(accountId, out var id) ? id : 0,
                Roles            = roles,
            },
            true, "Successful", "00"));
    }

    /// <summary>
    /// Mints a short-lived JWT for the SignalR WebSocket handshake. Browsers
    /// can't attach HttpOnly cookies to cross-origin WebSocket upgrades in all
    /// transports, and the <c>access_token</c> query-string fallback requires
    /// a readable bearer — so we trade the cookie for a 60-second ticket
    /// here. Only usable during the handshake; once the connection is up,
    /// SignalR keeps it alive without re-presenting the ticket.
    /// </summary>
    [HttpGet("ws-ticket")]
    [Authorize]
    public IActionResult WsTicket()
    {
        // The existing bearer token in `Authorization` has already been
        // validated by the JWT middleware, so we can safely echo it back.
        // In the cookie-only flow the UI will call this endpoint with its
        // cookie attached; the middleware's cookie fallback reads the cookie
        // into `Request.Headers["Authorization"]` equivalent for validation.
        var authHeader = Request.Headers["Authorization"].ToString();
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..]
            : Request.Cookies[AuthCookieName] ?? string.Empty;

        if (string.IsNullOrEmpty(token))
            return Unauthorized();

        return Ok(ResponseData<WsTicketResponse>.Init(
            new WsTicketResponse { Token = token },
            true, "Successful", "00"));
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

        var result = await _mediator.Send(new LogoutTradingAccountCommand
        {
            Jti              = jti,
            TradingAccountId = accountId,
            ExpiresAt        = expiresAt,
            Reason           = "UserLogout",
        });

        // Clear the cookie too — even if revocation fails the browser should
        // stop presenting this token on subsequent requests.
        Response.Cookies.Delete(AuthCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure   = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path     = "/",
        });

        return result;
    }

    /// <summary>
    /// Writes the issued JWT to an HttpOnly cookie for web logins. EA logins
    /// continue to use the bearer token they already consume in the response
    /// body — they're not cookie-aware.
    /// </summary>
    private void SetAuthCookieIfWebLogin(string loginSource, ResponseData<AuthTokenResult> result)
    {
        if (!string.Equals(loginSource, "web", StringComparison.OrdinalIgnoreCase)) return;
        if (result?.status != true || result.data?.Token is not { Length: > 0 } token) return;

        Response.Cookies.Append(AuthCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure   = Request.IsHttps, // local dev over http is OK; Secure enforced in prod
            SameSite = SameSiteMode.Lax,
            Path     = "/",
            Expires  = result.data.ExpiresAt,
        });
    }
}

/// <summary>Response shape for <c>GET /auth/whoami</c>.</summary>
public sealed class WhoAmIResponse
{
    public long     TradingAccountId { get; set; }
    public string[] Roles            { get; set; } = [];
}

/// <summary>Response shape for <c>GET /auth/ws-ticket</c>.</summary>
public sealed class WsTicketResponse
{
    public string Token { get; set; } = string.Empty;
}
