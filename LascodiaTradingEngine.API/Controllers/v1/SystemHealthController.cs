using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetEngineStatus;

namespace LascodiaTradingEngine.API.Controllers.v1;

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
}
