using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.StrategyEnsemble.Commands.RebalanceEnsemble;
using LascodiaTradingEngine.Application.StrategyEnsemble.Queries.DTOs;
using LascodiaTradingEngine.Application.StrategyEnsemble.Queries.GetStrategyAllocations;
using LascodiaTradingEngine.Application.StrategyEnsemble.Queries.GetPagedStrategyAllocations;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages strategy ensemble weight allocations and Sharpe-ratio-based rebalancing across active strategies.
/// Route: api/v1/lascodia-trading-engine/strategy-ensemble
/// </summary>
[Route("api/v1/lascodia-trading-engine/strategy-ensemble")]
[ApiController]
public class StrategyEnsembleController : AuthControllerBase<StrategyEnsembleController>
{
    public StrategyEnsembleController(
        ILogger<StrategyEnsembleController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Rebalance strategy weights based on Sharpe ratios</summary>
    [HttpPost("rebalance")]
    public async Task<ResponseData<string>> Rebalance()
        => await Mediator.Send(new RebalanceEnsembleCommand());

    /// <summary>Get all active strategy allocations with strategy names</summary>
    [HttpGet("allocations")]
    public async Task<ResponseData<List<StrategyAllocationDto>>> GetAllocations()
        => await Mediator.Send(new GetStrategyAllocationsQuery());

    /// <summary>Get paged list of strategy allocations</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<StrategyAllocationDto>>> GetPaged(GetPagedStrategyAllocationsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<StrategyAllocationDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
