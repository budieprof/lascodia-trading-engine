using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.TrailingStop.Commands.UpdateTrailingStop;
using LascodiaTradingEngine.Application.TrailingStop.Commands.ScalePosition;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/trailing-stop")]
[ApiController]
public class TrailingStopController : AuthControllerBase<TrailingStopController>
{
    public TrailingStopController(
        ILogger<TrailingStopController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Update trailing stop settings on a position</summary>
    [HttpPut("{positionId}")]
    public async Task<ResponseData<string>> UpdateTrailingStop(long positionId, UpdateTrailingStopCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.PositionId = positionId;
        return await Mediator.Send(command);
    }

    /// <summary>Scale a position in or out</summary>
    [HttpPost("scale")]
    public async Task<ResponseData<long>> ScalePosition(ScalePositionCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }
}
