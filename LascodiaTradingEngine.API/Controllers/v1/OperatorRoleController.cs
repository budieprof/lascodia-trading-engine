using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.OperatorRoles.Commands.AssignOperatorRole;
using LascodiaTradingEngine.Application.OperatorRoles.Commands.RevokeOperatorRole;
using LascodiaTradingEngine.Application.OperatorRoles.Queries.ListOperatorRoles;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Admin-only role management. Lists, grants and revokes <see cref="LascodiaTradingEngine.Domain.Entities.OperatorRole"/>
/// rows. Changes affect future logins; existing JWTs continue to carry their issued roles
/// until expiry — combine with <c>POST /auth/logout</c> for immediate effect.
/// Route: api/v1/lascodia-trading-engine/admin/operator-roles
/// </summary>
[Route("api/v1/lascodia-trading-engine/admin/operator-roles")]
[ApiController]
[Authorize(Policy = Policies.Admin)]
public class OperatorRoleController : AuthControllerBase<OperatorRoleController>
{
    public OperatorRoleController(
        ILogger<OperatorRoleController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>List role grants. Pass <c>tradingAccountId</c> to scope to a single account.</summary>
    [HttpGet]
    public async Task<ResponseData<List<OperatorRoleDto>>> List([FromQuery] long? tradingAccountId)
        => await Mediator.Send(new ListOperatorRolesQuery { TradingAccountId = tradingAccountId });

    /// <summary>Grant a role to an account. Idempotent.</summary>
    [HttpPost("grant")]
    public async Task<ResponseData<string>> Grant(AssignOperatorRoleCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Revoke a role from an account. Idempotent.</summary>
    [HttpPost("revoke")]
    public async Task<ResponseData<string>> Revoke(RevokeOperatorRoleCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }
}
