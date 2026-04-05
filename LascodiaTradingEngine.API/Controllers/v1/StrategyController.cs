using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Strategies.Commands.ActivateStrategy;
using LascodiaTradingEngine.Application.Strategies.Commands.AssignRiskProfile;
using LascodiaTradingEngine.Application.Strategies.Commands.CreateStrategy;
using LascodiaTradingEngine.Application.Strategies.Commands.DeleteStrategy;
using LascodiaTradingEngine.Application.Strategies.Commands.PauseStrategy;
using LascodiaTradingEngine.Application.Strategies.Commands.UpdateStrategy;
using LascodiaTradingEngine.Application.Strategies.Queries.DTOs;
using LascodiaTradingEngine.Application.Strategies.Queries.GetStrategy;
using LascodiaTradingEngine.Application.Strategies.Queries.GetPagedStrategies;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages trading strategy lifecycle: creation, updates, activation, pausing, deletion, and risk profile assignment.
/// Route: api/v1/lascodia-trading-engine/strategy
/// </summary>
[Route("api/v1/lascodia-trading-engine/strategy")]
[ApiController]
public class StrategyController : AuthControllerBase<StrategyController>
{
    public StrategyController(
        ILogger<StrategyController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Create a new strategy</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateStrategyCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update a strategy</summary>
    [HttpPut("{id}")]
    public async Task<ResponseData<string>> Update(long id, UpdateStrategyCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Delete a strategy</summary>
    [HttpDelete("{id}")]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteStrategyCommand { Id = id });

    /// <summary>Activate a strategy</summary>
    [HttpPut("{id}/activate")]
    public async Task<ResponseData<string>> Activate(long id)
        => await Mediator.Send(new ActivateStrategyCommand { Id = id });

    /// <summary>Pause a strategy</summary>
    [HttpPut("{id}/pause")]
    public async Task<ResponseData<string>> Pause(long id)
        => await Mediator.Send(new PauseStrategyCommand { Id = id });

    /// <summary>Assign a risk profile to a strategy</summary>
    [HttpPut("{id}/risk-profile")]
    public async Task<ResponseData<string>> AssignRiskProfile(long id, AssignRiskProfileCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.StrategyId = id;
        return await Mediator.Send(command);
    }

    /// <summary>Get strategy by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<StrategyDto>> GetById(long id)
        => await Mediator.Send(new GetStrategyQuery { Id = id });

    /// <summary>Get paged list of strategies</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<StrategyDto>>> GetPaged(GetPagedStrategiesQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<StrategyDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
