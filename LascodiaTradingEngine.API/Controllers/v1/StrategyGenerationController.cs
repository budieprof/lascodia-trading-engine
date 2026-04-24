using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.StrategyGeneration.Commands.TriggerStrategyGenerationCycle;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Operator-facing controls for the strategy-generation pipeline. Provides manual
/// triggers that bypass the normal schedule so operators can run a cycle immediately
/// after a deploy without waiting for the next 02:12 UTC polling window.
/// Route: api/v1/lascodia-trading-engine/strategy-generation
/// </summary>
[Route("api/v1/lascodia-trading-engine/strategy-generation")]
[ApiController]
public class StrategyGenerationController : AuthControllerBase<StrategyGenerationController>
{
    public StrategyGenerationController(
        ILogger<StrategyGenerationController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>
    /// Triggers a strategy-generation cycle immediately, bypassing the time-of-day
    /// schedule. Still obeys the distributed generation lock and cycle-run bookkeeping,
    /// so calling this twice in quick succession will serialise the requests. Long-
    /// running — a full cycle can take tens of seconds.
    /// Gated on the Operator policy per E9 so only authenticated operators/admins can
    /// force a cycle outside the schedule — the previous AllowAnonymous was a dev
    /// convenience that no longer holds now that RBAC is wired.
    /// </summary>
    [HttpPost("cycles/trigger")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<string>> TriggerCycle()
        => await Mediator.Send(new TriggerStrategyGenerationCycleCommand());
}
