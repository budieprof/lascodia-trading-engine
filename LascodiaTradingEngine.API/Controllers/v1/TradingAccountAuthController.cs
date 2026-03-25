using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.RegisterTrader;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.LoginTradingAccount;

namespace LascodiaTradingEngine.API.Controllers.v1;

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
}
