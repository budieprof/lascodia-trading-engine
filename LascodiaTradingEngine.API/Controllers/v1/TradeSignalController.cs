using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ApproveTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;
using LascodiaTradingEngine.Application.TradeSignals.Queries.GetTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Queries.GetPagedTradeSignals;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/trade-signal")]
[ApiController]
public class TradeSignalController : AuthControllerBase<TradeSignalController>
{
    public TradeSignalController(
        ILogger<TradeSignalController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Approve a pending trade signal</summary>
    [HttpPut("{id}/approve")]
    public async Task<ResponseData<string>> Approve(long id)
        => await Mediator.Send(new ApproveTradeSignalCommand { Id = id });

    /// <summary>Reject a pending trade signal</summary>
    [HttpPut("{id}/reject")]
    public async Task<ResponseData<string>> Reject(long id, RejectTradeSignalCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Expire a trade signal</summary>
    [HttpPut("{id}/expire")]
    public async Task<ResponseData<string>> Expire(long id)
        => await Mediator.Send(new ExpireTradeSignalCommand { Id = id });

    /// <summary>Get trade signal by Id</summary>
    [HttpGet("{id}")]
    public async Task<ResponseData<TradeSignalDto>> GetById(long id)
        => await Mediator.Send(new GetTradeSignalQuery { Id = id });

    /// <summary>Get paged list of trade signals</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<TradeSignalDto>>> GetPaged(GetPagedTradeSignalsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<TradeSignalDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
