using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.CreateTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.UpdateTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.DeleteTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.ActivateTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.SyncAccountBalance;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.GetTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.GetActiveTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.GetPagedTradingAccounts;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/trading-account")]
[ApiController]
public class TradingAccountController : AuthControllerBase<TradingAccountController>
{
    public TradingAccountController(
        ILogger<TradingAccountController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Create a new trading account</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateTradingAccountCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update a trading account</summary>
    [HttpPut("{id}")]
    public async Task<ResponseData<string>> Update(long id, UpdateTradingAccountCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Delete a trading account</summary>
    [HttpDelete("{id}")]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteTradingAccountCommand { Id = id });

    /// <summary>Activate a trading account</summary>
    [HttpPut("{id}/activate")]
    public async Task<ResponseData<string>> Activate(long id)
        => await Mediator.Send(new ActivateTradingAccountCommand { Id = id });

    /// <summary>Sync account balance</summary>
    [HttpPut("{id}/sync")]
    public async Task<ResponseData<string>> Sync(long id, SyncAccountBalanceCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Get trading account by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<TradingAccountDto>> GetById(long id)
        => await Mediator.Send(new GetTradingAccountQuery { Id = id });

    /// <summary>Get active trading account for a broker</summary>
    [HttpGet("active/{brokerId}")]
    public async Task<ResponseData<TradingAccountDto>> GetActive(long brokerId)
        => await Mediator.Send(new GetActiveTradingAccountQuery { BrokerId = brokerId });

    /// <summary>Get paged list of trading accounts</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<TradingAccountDto>>> GetPaged(GetPagedTradingAccountsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<TradingAccountDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
