using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.EngineConfiguration.Commands.UpsertEngineConfig;
using LascodiaTradingEngine.Application.EngineConfiguration.Queries.DTOs;
using LascodiaTradingEngine.Application.EngineConfiguration.Queries.GetEngineConfig;
using LascodiaTradingEngine.Application.EngineConfiguration.Queries.GetAllEngineConfigs;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/config")]
[ApiController]
public class EngineConfigurationController : AuthControllerBase<EngineConfigurationController>
{
    public EngineConfigurationController(
        ILogger<EngineConfigurationController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Upsert an engine configuration entry</summary>
    [HttpPut]
    public async Task<ResponseData<long>> Upsert(UpsertEngineConfigCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get engine config by key</summary>
    [HttpGet("{key}")]
    public async Task<ResponseData<EngineConfigDto>> GetByKey(string key)
        => await Mediator.Send(new GetEngineConfigQuery { Key = key });

    /// <summary>Get all engine configuration entries</summary>
    [HttpGet("all")]
    public async Task<ResponseData<List<EngineConfigDto>>> GetAll()
        => await Mediator.Send(new GetAllEngineConfigsQuery());
}
