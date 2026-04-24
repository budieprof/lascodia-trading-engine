using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ApproveTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.RejectTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Commands.ExpireTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;
using LascodiaTradingEngine.Application.TradeSignals.Queries.GetTradeSignal;
using LascodiaTradingEngine.Application.TradeSignals.Queries.GetPagedTradeSignals;
using LascodiaTradingEngine.Application.TradeSignals.Queries.GetPendingExecutionTradeSignals;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Manages trade signal workflow: approval, rejection, expiry, and pending-execution queries for EA consumption.
/// Route: api/v1/lascodia-trading-engine/trade-signal
/// </summary>
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
    [Authorize(Policy = Policies.Trader)]
    public async Task<ResponseData<string>> Approve(long id)
        => await Mediator.Send(new ApproveTradeSignalCommand { Id = id });

    /// <summary>Reject a pending trade signal</summary>
    [HttpPut("{id}/reject")]
    [Authorize(Policy = Policies.Trader)]
    public async Task<ResponseData<string>> Reject(long id, RejectTradeSignalCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Expire a trade signal</summary>
    [HttpPut("{id}/expire")]
    [Authorize(Policy = Policies.Trader)]
    public async Task<ResponseData<string>> Expire(long id)
        => await Mediator.Send(new ExpireTradeSignalCommand { Id = id });

    /// <summary>Get approved trade signals pending broker execution</summary>
    [HttpGet("pending-execution")]
    public async Task<ResponseData<List<TradeSignalDto>>> GetPendingExecution()
        => await Mediator.Send(new GetPendingExecutionTradeSignalsQuery());

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
