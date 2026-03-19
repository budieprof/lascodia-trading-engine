using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;
using LascodiaTradingEngine.Application.MarketData.Queries.GetCandles;
using LascodiaTradingEngine.Application.MarketData.Queries.GetLatestCandle;
using LascodiaTradingEngine.Application.MarketData.Queries.GetLivePrice;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/market-data")]
[ApiController]
public class MarketDataController : AuthControllerBase<MarketDataController>
{
    public MarketDataController(
        ILogger<MarketDataController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get live bid/ask price for a symbol</summary>
    [HttpGet("live-price/{symbol}")]
    public async Task<ResponseData<LivePriceDto>> GetLivePrice(string symbol)
        => await Mediator.Send(new GetLivePriceQuery { Symbol = symbol.ToUpperInvariant() });

    /// <summary>Get the latest closed candle for a symbol and timeframe</summary>
    [HttpGet("candle/latest")]
    public async Task<ResponseData<CandleDto>> GetLatestCandle([FromQuery] string symbol, [FromQuery] string timeframe)
        => await Mediator.Send(new GetLatestCandleQuery
        {
            Symbol    = symbol.ToUpperInvariant(),
            Timeframe = timeframe.ToUpperInvariant()
        });

    /// <summary>Get paged candle history for a symbol and timeframe</summary>
    [HttpPost("candle/list")]
    public async Task<ResponseData<PagedData<CandleDto>>> GetCandles(GetCandlesQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<CandleDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
