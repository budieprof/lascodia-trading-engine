using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.MarketRegime.Queries.DTOs;
using LascodiaTradingEngine.Application.MarketRegime.Queries.GetLatestRegime;
using LascodiaTradingEngine.Application.MarketRegime.Queries.GetPagedRegimeSnapshots;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Queries market regime snapshots used by strategies to adapt to trending, ranging, or volatile conditions.
/// Route: api/v1/lascodia-trading-engine/market-regime
/// </summary>
[Route("api/v1/lascodia-trading-engine/market-regime")]
[ApiController]
public class MarketRegimeController : AuthControllerBase<MarketRegimeController>
{
    public MarketRegimeController(
        ILogger<MarketRegimeController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get the latest regime snapshot for a symbol/timeframe pair</summary>
    [HttpGet("latest")]
    public async Task<ResponseData<MarketRegimeSnapshotDto>> GetLatest(
        [FromQuery] string symbol,
        [FromQuery] string timeframe)
        => await Mediator.Send(new GetLatestRegimeQuery { Symbol = symbol, Timeframe = timeframe });

    /// <summary>Get paged list of regime snapshots</summary>
    [HttpPost("list")]
    public async Task<ResponseData<PagedData<MarketRegimeSnapshotDto>>> GetPaged(GetPagedRegimeSnapshotsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<MarketRegimeSnapshotDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
