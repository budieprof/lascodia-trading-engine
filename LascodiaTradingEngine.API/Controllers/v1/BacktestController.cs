using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Backtesting.Commands.RunBacktest;
using LascodiaTradingEngine.Application.Backtesting.Queries.DTOs;
using LascodiaTradingEngine.Application.Backtesting.Queries.GetBacktestRun;
using LascodiaTradingEngine.Application.Backtesting.Queries.GetPagedBacktestRuns;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages backtest run submission and retrieval of historical strategy simulation results.
/// Route: api/v1/lascodia-trading-engine/backtest
/// </summary>
[Route("api/v1/lascodia-trading-engine/backtest")]
[ApiController]
public class BacktestController : AuthControllerBase<BacktestController>
{
    public BacktestController(
        ILogger<BacktestController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Queue a new backtest run</summary>
    [HttpPost]
    public async Task<ResponseData<long>> RunBacktest(RunBacktestCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get backtest run by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<BacktestRunDto>> GetById(long id)
        => await Mediator.Send(new GetBacktestRunQuery { Id = id });

    /// <summary>Get paged list of backtest runs</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<BacktestRunDto>>> GetPaged(GetPagedBacktestRunsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<BacktestRunDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
