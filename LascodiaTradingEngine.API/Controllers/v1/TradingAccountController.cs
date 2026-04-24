using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.CreateTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.UpdateTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.DeleteTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.ActivateTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.SyncAccountBalance;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.ChangePassword;
using LascodiaTradingEngine.Application.TradingAccounts.Commands.RotateApiKey;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.DTOs;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.GetTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.GetActiveTradingAccount;
using LascodiaTradingEngine.Application.TradingAccounts.Queries.GetPagedTradingAccounts;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages trading account CRUD, activation, balance sync, password changes, and API key rotation.
/// Route: api/v1/lascodia-trading-engine/trading-account
/// </summary>
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
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<long>> Create(CreateTradingAccountCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update a trading account</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<string>> Update(long id, UpdateTradingAccountCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Delete a trading account</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteTradingAccountCommand { Id = id });

    /// <summary>Activate a trading account</summary>
    [HttpPut("{id}/activate")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<string>> Activate(long id)
        => await Mediator.Send(new ActivateTradingAccountCommand { Id = id });

    /// <summary>Sync account balance</summary>
    [HttpPut("{id}/sync")]
    [Authorize(Policy = Policies.EAIngest)]
    public async Task<ResponseData<string>> Sync(long id, SyncAccountBalanceCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Change trading account password</summary>
    [HttpPut("{id}/password")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<string>> ChangePassword(long id, ChangePasswordCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Rotate the EA API key for a trading account</summary>
    [HttpPost("{id}/rotate-api-key")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<RotateApiKeyResult>> RotateApiKey(long id)
        => await Mediator.Send(new RotateApiKeyCommand { Id = id });

    /// <summary>Get trading account by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<TradingAccountDto>> GetById(long id)
        => await Mediator.Send(new GetTradingAccountQuery { Id = id });

    /// <summary>Get active trading account</summary>
    [HttpGet("active")]
    public async Task<ResponseData<TradingAccountDto>> GetActive()
        => await Mediator.Send(new GetActiveTradingAccountQuery());

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
