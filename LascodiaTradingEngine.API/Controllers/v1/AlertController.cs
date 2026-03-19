using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Alerts.Commands.CreateAlert;
using LascodiaTradingEngine.Application.Alerts.Commands.DeleteAlert;
using LascodiaTradingEngine.Application.Alerts.Commands.UpdateAlert;
using LascodiaTradingEngine.Application.Alerts.Queries.DTOs;
using LascodiaTradingEngine.Application.Alerts.Queries.GetAlert;
using LascodiaTradingEngine.Application.Alerts.Queries.GetPagedAlerts;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/alert")]
[ApiController]
public class AlertController : AuthControllerBase<AlertController>
{
    public AlertController(
        ILogger<AlertController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Create a new alert</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateAlertCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update an alert</summary>
    [HttpPut("{id}")]
    public async Task<ResponseData<string>> Update(long id, UpdateAlertCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Delete an alert</summary>
    [HttpDelete("{id}")]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteAlertCommand { Id = id });

    /// <summary>Get alert by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<AlertDto>> GetById(long id)
        => await Mediator.Send(new GetAlertQuery { Id = id });

    /// <summary>Get paged list of alerts</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<AlertDto>>> GetPaged(GetPagedAlertsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<AlertDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
