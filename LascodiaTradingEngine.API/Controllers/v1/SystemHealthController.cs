using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetEngineStatus;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetStrategyGenerationWorkerHealth;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Exposes engine health status including worker states, EA connectivity, and data availability.
/// Route: api/v1/lascodia-trading-engine/health
/// </summary>
[Route("api/v1/lascodia-trading-engine/health")]
[ApiController]
public class SystemHealthController : AuthControllerBase<SystemHealthController>
{
    private readonly IWorkerHealthMonitor _workerHealthMonitor;

    public SystemHealthController(
        ILogger<SystemHealthController> logger,
        IConfiguration config,
        ICurrentUserService userService,
        IWorkerHealthMonitor workerHealthMonitor)
        : base(logger, config, userService)
    {
        _workerHealthMonitor = workerHealthMonitor;
    }

    /// <summary>Get the current engine status</summary>
    [HttpGet("status")]
    public async Task<ResponseData<EngineStatusDto>> GetStatus()
        => await Mediator.Send(new GetEngineStatusQuery());

    /// <summary>Get typed strategy-generation worker health, replay state, and phase diagnostics.</summary>
    [HttpGet("strategy-generation")]
    public async Task<ResponseData<StrategyGenerationWorkerHealthDto>> GetStrategyGenerationWorkerHealth()
        => await Mediator.Send(new GetStrategyGenerationWorkerHealthQuery());

    /// <summary>
    /// Returns real-time health snapshots for all registered background workers.
    /// Includes cycle durations, error rates, backlog depths, and staleness indicators.
    /// </summary>
    [HttpGet("workers")]
    public ActionResult<IReadOnlyList<WorkerHealthSnapshot>> GetWorkerHealth()
    {
        var snapshots = _workerHealthMonitor.GetCurrentSnapshots();
        return Ok(snapshots);
    }
}
