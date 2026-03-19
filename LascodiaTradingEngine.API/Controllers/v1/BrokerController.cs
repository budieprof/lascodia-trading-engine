using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Brokers.Commands.CreateBroker;
using LascodiaTradingEngine.Application.Brokers.Commands.UpdateBroker;
using LascodiaTradingEngine.Application.Brokers.Commands.DeleteBroker;
using LascodiaTradingEngine.Application.Brokers.Commands.ActivateBroker;
using LascodiaTradingEngine.Application.Brokers.Commands.UpdateBrokerStatus;
using LascodiaTradingEngine.Application.Brokers.Queries.DTOs;
using LascodiaTradingEngine.Application.Brokers.Queries.GetBroker;
using LascodiaTradingEngine.Application.Brokers.Queries.GetActiveBroker;
using LascodiaTradingEngine.Application.Brokers.Queries.GetPagedBrokers;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/broker")]
[ApiController]
public class BrokerController : AuthControllerBase<BrokerController>
{
    public BrokerController(
        ILogger<BrokerController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Create a new broker</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateBrokerCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update a broker</summary>
    [HttpPut("{id}")]
    public async Task<ResponseData<string>> Update(long id, UpdateBrokerCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Delete a broker</summary>
    [HttpDelete("{id}")]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteBrokerCommand { Id = id });

    /// <summary>Activate a broker</summary>
    [HttpPut("{id}/activate")]
    public async Task<ResponseData<string>> Activate(long id)
        => await Mediator.Send(new ActivateBrokerCommand { Id = id });

    /// <summary>Update broker status</summary>
    [HttpPut("{id}/status")]
    public async Task<ResponseData<string>> UpdateStatus(long id, UpdateBrokerStatusCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Get broker by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<BrokerDto>> GetById(long id)
        => await Mediator.Send(new GetBrokerQuery { Id = id });

    /// <summary>Get active broker</summary>
    [HttpGet("active")]
    public async Task<ResponseData<BrokerDto>> GetActive()
        => await Mediator.Send(new GetActiveBrokerQuery());

    /// <summary>Get paged list of brokers</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<BrokerDto>>> GetPaged(GetPagedBrokersQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<BrokerDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
