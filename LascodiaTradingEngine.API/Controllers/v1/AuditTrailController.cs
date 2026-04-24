using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.AuditTrail.Queries.DTOs;
using LascodiaTradingEngine.Application.AuditTrail.Queries.GetPagedDecisionLogs;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Provides immutable decision audit logging and paginated retrieval of audit entries.
/// Route: api/v1/lascodia-trading-engine/audit-trail
/// </summary>
[Route("api/v1/lascodia-trading-engine/audit-trail")]
[ApiController]
public class AuditTrailController : AuthControllerBase<AuditTrailController>
{
    public AuditTrailController(
        ILogger<AuditTrailController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Append an immutable decision log entry</summary>
    [HttpPost]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<long>> Log(LogDecisionCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get paged list of decision logs</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<DecisionLogDto>>> GetPaged(GetPagedDecisionLogsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<DecisionLogDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
