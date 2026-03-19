using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.DrawdownRecovery.Commands.RecordDrawdownSnapshot;
using LascodiaTradingEngine.Application.DrawdownRecovery.Queries.DTOs;
using LascodiaTradingEngine.Application.DrawdownRecovery.Queries.GetLatestDrawdownSnapshot;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/drawdown-recovery")]
[ApiController]
public class DrawdownRecoveryController : AuthControllerBase<DrawdownRecoveryController>
{
    public DrawdownRecoveryController(
        ILogger<DrawdownRecoveryController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Record a drawdown snapshot and get the current recovery mode</summary>
    [HttpPost]
    public async Task<ResponseData<string>> Record(RecordDrawdownSnapshotCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get the latest drawdown snapshot</summary>
    [HttpGet("latest")]
    public async Task<ResponseData<DrawdownSnapshotDto>> GetLatest()
        => await Mediator.Send(new GetLatestDrawdownSnapshotQuery());
}
