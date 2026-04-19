using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.PaperTrading.Commands.BackfillPaperExecutions;
using LascodiaTradingEngine.Application.PaperTrading.Commands.SetPaperTradingMode;
using LascodiaTradingEngine.Application.PaperTrading.Queries.GetPaperTradingStatus;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Toggles and queries paper trading mode for simulated order execution without live broker interaction.
/// Route: api/v1/lascodia-trading-engine/paper-trading
/// </summary>
[Route("api/v1/lascodia-trading-engine/paper-trading")]
[ApiController]
public class PaperTradingController : AuthControllerBase<PaperTradingController>
{
    public PaperTradingController(
        ILogger<PaperTradingController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Enable or disable paper trading mode</summary>
    [HttpPut("mode")]
    public async Task<ResponseData<string>> SetMode(SetPaperTradingModeCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get current paper trading mode status</summary>
    [HttpGet("status")]
    public async Task<ResponseData<bool>> GetStatus()
        => await Mediator.Send(new GetPaperTradingStatusQuery());

    /// <summary>
    /// Backfill <c>PaperExecution</c> rows for an Approved-but-not-Active strategy by
    /// replaying its historical candles through <c>BacktestEngine</c> with the live
    /// TCA profile. Inserted rows carry <c>IsSynthetic = true</c> and are excluded
    /// from the promotion gate's hard-count requirement; they are observability data,
    /// not forward-test evidence. Operator-only — gives ops a way to populate the
    /// table without waiting for live signals to accrue.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<ResponseData<int>> BackfillPaperExecutions(BackfillPaperExecutionsCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<int>.Init(0, false, "Model state failed", "-11");
        return await Mediator.Send(command);
    }
}
