using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.ExecutionQuality.Commands.RecordExecutionQuality;
using LascodiaTradingEngine.Application.ExecutionQuality.Queries.DTOs;
using LascodiaTradingEngine.Application.ExecutionQuality.Queries.GetExecutionQualityLog;
using LascodiaTradingEngine.Application.ExecutionQuality.Queries.GetPagedExecutionQualityLogs;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Records and queries execution quality metrics such as slippage, fill latency, and rejection rates.
/// Route: api/v1/lascodia-trading-engine/execution-quality
/// </summary>
[Route("api/v1/lascodia-trading-engine/execution-quality")]
[ApiController]
public class ExecutionQualityController : AuthControllerBase<ExecutionQualityController>
{
    public ExecutionQualityController(
        ILogger<ExecutionQualityController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Record an execution quality log entry</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Record(RecordExecutionQualityCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get execution quality log by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<ExecutionQualityLogDto>> GetById(long id)
        => await Mediator.Send(new GetExecutionQualityLogQuery { Id = id });

    /// <summary>Get paged list of execution quality logs</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<ExecutionQualityLogDto>>> GetPaged(GetPagedExecutionQualityLogsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<ExecutionQualityLogDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
