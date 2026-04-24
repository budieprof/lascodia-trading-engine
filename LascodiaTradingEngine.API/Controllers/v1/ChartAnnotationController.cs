using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.ChartAnnotations.Commands.CreateChartAnnotation;
using LascodiaTradingEngine.Application.ChartAnnotations.Commands.DeleteChartAnnotation;
using LascodiaTradingEngine.Application.ChartAnnotations.Commands.UpdateChartAnnotation;
using LascodiaTradingEngine.Application.ChartAnnotations.Queries.DTOs;
using LascodiaTradingEngine.Application.ChartAnnotations.Queries.GetPagedChartAnnotations;
using LascodiaTradingEngine.Application.Common.Security;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Chart annotations — operator-authored notes attached to a timestamp on a
/// named chart. Read is open to any Viewer-policy token; mutations require
/// the Trader policy.
/// Route: api/v1/lascodia-trading-engine/chart-annotations
/// </summary>
[Route("api/v1/lascodia-trading-engine/chart-annotations")]
[ApiController]
public class ChartAnnotationController : AuthControllerBase<ChartAnnotationController>
{
    public ChartAnnotationController(
        ILogger<ChartAnnotationController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Paged list of annotations for a target chart.</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<ChartAnnotationDto>>> GetPaged(GetPagedChartAnnotationsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<ChartAnnotationDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }

    /// <summary>Create a new annotation. Author is taken from the caller's token.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.Trader)]
    public async Task<ResponseData<long>> Create(CreateChartAnnotationCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Edit the body of an existing annotation. Author-only.</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = Policies.Trader)]
    public async Task<ResponseData<string>> Update(long id, UpdateChartAnnotationCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Soft-delete an annotation. Author-only.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.Trader)]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteChartAnnotationCommand { Id = id });
}
