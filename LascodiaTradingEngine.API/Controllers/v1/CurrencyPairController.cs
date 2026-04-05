using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.CurrencyPairs.Commands.CreateCurrencyPair;
using LascodiaTradingEngine.Application.CurrencyPairs.Commands.UpdateCurrencyPair;
using LascodiaTradingEngine.Application.CurrencyPairs.Commands.DeleteCurrencyPair;
using LascodiaTradingEngine.Application.CurrencyPairs.Queries.DTOs;
using LascodiaTradingEngine.Application.CurrencyPairs.Queries.GetCurrencyPair;
using LascodiaTradingEngine.Application.CurrencyPairs.Queries.GetPagedCurrencyPairs;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages currency pair (symbol) definitions including creation, updates, and deletion.
/// Route: api/v1/lascodia-trading-engine/currency-pair
/// </summary>
[Route("api/v1/lascodia-trading-engine/currency-pair")]
[ApiController]
public class CurrencyPairController : AuthControllerBase<CurrencyPairController>
{
    public CurrencyPairController(
        ILogger<CurrencyPairController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Create a new currency pair</summary>
    [HttpPost]
    public async Task<ResponseData<long>> Create(CreateCurrencyPairCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Update a currency pair</summary>
    [HttpPut("{id}")]
    public async Task<ResponseData<string>> Update(long id, UpdateCurrencyPairCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Delete a currency pair</summary>
    [HttpDelete("{id}")]
    public async Task<ResponseData<string>> Delete(long id)
        => await Mediator.Send(new DeleteCurrencyPairCommand { Id = id });

    /// <summary>Get currency pair by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<CurrencyPairDto>> GetById(long id)
        => await Mediator.Send(new GetCurrencyPairQuery { Id = id });

    /// <summary>Get paged list of currency pairs</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<CurrencyPairDto>>> GetPaged(GetPagedCurrencyPairsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<CurrencyPairDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
