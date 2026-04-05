using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.MLModels.Commands.TriggerMLTraining;
using LascodiaTradingEngine.Application.MLModels.Commands.ActivateMLModel;
using LascodiaTradingEngine.Application.MLModels.Commands.RollbackMLModel;
using LascodiaTradingEngine.Application.MLModels.Commands.TriggerMLHyperparamSearch;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Application.MLModels.Queries.GetMLModel;
using LascodiaTradingEngine.Application.MLModels.Queries.GetPagedMLModels;
using LascodiaTradingEngine.Application.MLModels.Queries.GetMLTrainingRun;
using LascodiaTradingEngine.Application.MLModels.Queries.GetPagedMLTrainingRuns;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages ML model lifecycle: training triggers, activation, rollback, hyperparameter search, and queries.
/// Route: api/v1/lascodia-trading-engine/ml-model
/// </summary>
[Route("api/v1/lascodia-trading-engine/ml-model")]
[ApiController]
public class MLModelController : AuthControllerBase<MLModelController>
{
    public MLModelController(
        ILogger<MLModelController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Trigger ML model training for a symbol/timeframe</summary>
    [HttpPost("training/trigger")]
    public async Task<ResponseData<long>> TriggerTraining(TriggerMLTrainingCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Activate an ML model (deactivates previous active model for same symbol/timeframe)</summary>
    [HttpPut("{id}/activate")]
    public async Task<ResponseData<string>> Activate(long id)
        => await Mediator.Send(new ActivateMLModelCommand { Id = id });

    /// <summary>Trigger a random hyperparameter search — queues N training candidate runs</summary>
    [HttpPost("training/hyperparam-search")]
    public async Task<ResponseData<int>> TriggerHyperparamSearch(TriggerMLHyperparamSearchCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<int>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Roll back to the most recently superseded model for a symbol/timeframe</summary>
    [HttpPost("rollback")]
    public async Task<ResponseData<long>> Rollback(RollbackMLModelCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get ML model by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<MLModelDto>> GetById(long id)
        => await Mediator.Send(new GetMLModelQuery { Id = id });

    /// <summary>Get paged list of ML models</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<MLModelDto>>> GetPaged(GetPagedMLModelsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<MLModelDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }

    /// <summary>Get ML training run by Id</summary>
    [HttpGet("training/{id}")]
    public async Task<ResponseData<MLTrainingRunDto>> GetTrainingRun(long id)
        => await Mediator.Send(new GetMLTrainingRunQuery { Id = id });

    /// <summary>Get paged list of ML training runs</summary>
    [HttpPost("training/list")]
    public async Task<ResponseData<PagedData<MLTrainingRunDto>>> GetPagedTrainingRuns(
        GetPagedMLTrainingRunsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<MLTrainingRunDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
