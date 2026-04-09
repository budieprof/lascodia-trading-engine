using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetEngineStatus;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetStrategyGenerationWorkerHealth;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Exposes engine health status including worker states, EA connectivity, and data availability.
/// Route: api/v1/lascodia-trading-engine/health
/// </summary>
[Route("api/v1/lascodia-trading-engine/health")]
[ApiController]
public class SystemHealthController : AuthControllerBase<SystemHealthController>
{
    public SystemHealthController(
        ILogger<SystemHealthController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get the current engine status</summary>
    [HttpGet("status")]
    public async Task<ResponseData<EngineStatusDto>> GetStatus()
        => await Mediator.Send(new GetEngineStatusQuery());

    /// <summary>Get typed strategy-generation worker health, replay state, and phase diagnostics.</summary>
    [HttpGet("strategy-generation")]
    public async Task<ResponseData<StrategyGenerationWorkerHealthDto>> GetStrategyGenerationWorkerHealth()
        => await Mediator.Send(new GetStrategyGenerationWorkerHealthQuery());
}
