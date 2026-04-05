using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.MLEvaluation.Commands.StartShadowEvaluation;
using LascodiaTradingEngine.Application.MLEvaluation.Commands.RecordPredictionOutcome;
using LascodiaTradingEngine.Application.MLEvaluation.Queries.DTOs;
using LascodiaTradingEngine.Application.MLEvaluation.Queries.GetMLShadowEvaluation;
using LascodiaTradingEngine.Application.MLEvaluation.Queries.GetPagedMLShadowEvaluations;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages ML model champion-challenger shadow evaluations and prediction outcome recording.
/// Route: api/v1/lascodia-trading-engine/ml-evaluation
/// </summary>
[Route("api/v1/lascodia-trading-engine/ml-evaluation")]
[ApiController]
public class MLEvaluationController : AuthControllerBase<MLEvaluationController>
{
    public MLEvaluationController(
        ILogger<MLEvaluationController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Start a champion-challenger shadow evaluation</summary>
    [HttpPost("shadow/start")]
    public async Task<ResponseData<long>> StartShadow(StartShadowEvaluationCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Record actual outcome for predictions linked to a trade signal</summary>
    [HttpPut("outcome")]
    public async Task<ResponseData<string>> RecordOutcome(RecordPredictionOutcomeCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get shadow evaluation by Id</summary>
    [HttpGet("shadow/{id}")]
    public async Task<ResponseData<MLShadowEvaluationDto>> GetShadow(long id)
        => await Mediator.Send(new GetMLShadowEvaluationQuery { Id = id });

    /// <summary>Get paged list of shadow evaluations</summary>
    [HttpPost("shadow/list")]
    public async Task<ResponseData<PagedData<MLShadowEvaluationDto>>> GetPagedShadows(GetPagedMLShadowEvaluationsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<MLShadowEvaluationDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
