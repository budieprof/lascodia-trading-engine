using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.TriggerOptimization;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.ApproveOptimization;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.RejectOptimization;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.DTOs;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetStrategyPerformance;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetOptimizationRun;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetPagedOptimizationRuns;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/strategy-feedback")]
[ApiController]
public class StrategyFeedbackController : AuthControllerBase<StrategyFeedbackController>
{
    public StrategyFeedbackController(
        ILogger<StrategyFeedbackController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get the latest performance snapshot for a strategy</summary>
    [HttpGet("{strategyId}/performance")]
    public async Task<ResponseData<StrategyPerformanceSnapshotDto>> GetPerformance(long strategyId)
        => await Mediator.Send(new GetStrategyPerformanceQuery { StrategyId = strategyId });

    /// <summary>Trigger an optimization run for a strategy</summary>
    [HttpPost("optimization/trigger")]
    public async Task<ResponseData<long>> TriggerOptimization(TriggerOptimizationCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Approve a completed optimization run and apply best parameters to the strategy</summary>
    [HttpPut("optimization/{id}/approve")]
    public async Task<ResponseData<string>> ApproveOptimization(long id)
        => await Mediator.Send(new ApproveOptimizationCommand { Id = id });

    /// <summary>Reject a completed optimization run</summary>
    [HttpPut("optimization/{id}/reject")]
    public async Task<ResponseData<string>> RejectOptimization(long id)
        => await Mediator.Send(new RejectOptimizationCommand { Id = id });

    /// <summary>Get optimization run by Id</summary>
    [HttpGet("optimization/{id}")]
    public async Task<ResponseData<OptimizationRunDto>> GetOptimizationRun(long id)
        => await Mediator.Send(new GetOptimizationRunQuery { Id = id });

    /// <summary>Get paged list of optimization runs</summary>
    [HttpPost("optimization/list")]
    public async Task<ResponseData<PagedData<OptimizationRunDto>>> GetPagedOptimizationRuns(
        GetPagedOptimizationRunsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<OptimizationRunDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
