using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
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
    ///
    /// <para>
    /// <b>Dev/ops endpoint</b> — marked <see cref="AllowAnonymousAttribute"/> because it's
    /// meant to be called by operators or integration tests against a development engine
    /// to validate a freshly-deployed generation change without waiting for the 02:12 UTC
    /// polling window. Before exposing on a production deployment, wrap it in a proper
    /// admin-auth policy or put it behind an IP allow-list at the ingress layer.
    /// </para>
    /// </summary>
    [HttpPost("cycles/trigger")]
    [AllowAnonymous]
    public async Task<ResponseData<string>> TriggerCycle()
        => await Mediator.Send(new TriggerStrategyGenerationCycleCommand());
}
