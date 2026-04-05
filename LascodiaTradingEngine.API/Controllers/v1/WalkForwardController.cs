using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.WalkForward.Commands.RunWalkForward;
using LascodiaTradingEngine.Application.WalkForward.Queries.DTOs;
using LascodiaTradingEngine.Application.WalkForward.Queries.GetWalkForwardRun;
using LascodiaTradingEngine.Application.WalkForward.Queries.GetPagedWalkForwardRuns;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages walk-forward optimisation runs for out-of-sample strategy parameter validation.
/// Route: api/v1/lascodia-trading-engine/walk-forward
/// </summary>
[Route("api/v1/lascodia-trading-engine/walk-forward")]
[ApiController]
public class WalkForwardController : AuthControllerBase<WalkForwardController>
{
    public WalkForwardController(
        ILogger<WalkForwardController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Queue a new walk-forward optimisation run</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Run(RunWalkForwardCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get walk-forward run by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<WalkForwardRunDto>> GetById(long id)
        => await Mediator.Send(new GetWalkForwardRunQuery { Id = id });

    /// <summary>Get paged list of walk-forward runs</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<WalkForwardRunDto>>> GetPaged(GetPagedWalkForwardRunsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<WalkForwardRunDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
