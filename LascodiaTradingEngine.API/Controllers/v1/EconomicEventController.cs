using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.CreateEconomicEvent;
using LascodiaTradingEngine.Application.EconomicEvents.Commands.UpdateEconomicEventActual;
using LascodiaTradingEngine.Application.EconomicEvents.Queries.DTOs;
using LascodiaTradingEngine.Application.EconomicEvents.Queries.GetPagedEconomicEvents;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages economic calendar events used by the news filter to gate trading around high-impact releases.
/// Route: api/v1/lascodia-trading-engine/economic-event
/// </summary>
[Route("api/v1/lascodia-trading-engine/economic-event")]
[ApiController]
public class EconomicEventController : AuthControllerBase<EconomicEventController>
{
    public EconomicEventController(
        ILogger<EconomicEventController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Create a new economic event</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateEconomicEventCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update the actual value for an economic event</summary>
    [HttpPut("{id}/actual")]
    public async Task<ResponseData<string>> UpdateActual(long id, UpdateEconomicEventActualCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Get paged list of economic events</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<EconomicEventDto>>> GetPaged(GetPagedEconomicEventsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<EconomicEventDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
