using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.TriggerOptimization;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.ApproveOptimization;
using LascodiaTradingEngine.Application.StrategyFeedback.Commands.RejectOptimization;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.DTOs;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetStrategyPerformance;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetOptimizationRun;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetPagedOptimizationRuns;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.ValidateOptimizationConfig;
using DryRunOptimization = LascodiaTradingEngine.Application.StrategyFeedback.Queries.DryRunOptimization;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages strategy performance feedback loop: optimization triggers, approval/rejection, and performance snapshots.
/// Route: api/v1/lascodia-trading-engine/strategy-feedback
/// </summary>
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
    [Authorize(Policy = Policies.Analyst)]
    public async Task<ResponseData<long>> TriggerOptimization(TriggerOptimizationCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Approve a completed optimization run and apply best parameters to the strategy</summary>
    [HttpPut("optimization/{id}/approve")]
    [Authorize(Policy = Policies.Analyst)]
    public async Task<ResponseData<string>> ApproveOptimization(long id)
        => await Mediator.Send(new ApproveOptimizationCommand { Id = id });

    /// <summary>Reject a completed optimization run</summary>
    [HttpPut("optimization/{id}/reject")]
    [Authorize(Policy = Policies.Analyst)]
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

    /// <summary>
    /// Dry-run validation of optimization config. Returns errors and warnings without
    /// executing any optimization. Optionally accepts override values to preview what
    /// a proposed config change would do before applying it.
    /// </summary>
    [HttpPost("optimization/config/validate")]
    public async Task<ResponseData<OptimizationConfigValidationDto>> ValidateOptimizationConfig(
        ValidateOptimizationConfigQuery query)
        => await Mediator.Send(query);

    /// <summary>
    /// Simulates an optimization run for a strategy without executing it. Returns estimated
    /// candle counts, grid size, surrogate type, resource requirements, and current system state.
    /// Use this to preview and tune optimization config before committing compute.
    /// </summary>
    [HttpGet("optimization/{strategyId}/dry-run")]
    public async Task<ResponseData<DryRunOptimization.OptimizationDryRunDto>> DryRunOptimization(
        long strategyId)
        => await Mediator.Send(new DryRunOptimization.DryRunOptimizationQuery { StrategyId = strategyId });
}
