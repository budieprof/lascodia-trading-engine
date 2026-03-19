using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Positions.Queries.DTOs;
using LascodiaTradingEngine.Application.Positions.Queries.GetPosition;
using LascodiaTradingEngine.Application.Positions.Queries.GetPagedPositions;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/position")]
[ApiController]
public class PositionController : AuthControllerBase<PositionController>
{
    public PositionController(
        ILogger<PositionController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get position by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<PositionDto>> GetById(long id)
        => await Mediator.Send(new GetPositionQuery { Id = id });

    /// <summary>Get paged list of positions</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<PositionDto>>> GetPaged(GetPagedPositionsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<PositionDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
