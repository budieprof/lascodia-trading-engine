using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;
using LascodiaTradingEngine.Application.MarketData.Queries.GetCandles;
using LascodiaTradingEngine.Application.MarketData.Queries.GetCandleWatermarks;
using LascodiaTradingEngine.Application.MarketData.Queries.GetLatestCandle;
using LascodiaTradingEngine.Application.MarketData.Queries.GetLivePrice;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTickBatch;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandle;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandleBatch;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveCandleBackfill;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Provides market data access: live prices, candle queries, tick batch ingestion from EA instances,
/// and candle backfill for historical data initialization.
/// Route: api/v1/lascodia-trading-engine/market-data
/// </summary>
[Route("api/v1/lascodia-trading-engine/market-data")]
[ApiController]
[EnableRateLimiting("ea")]
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

    /// <summary>Get the latest candle timestamp per symbol/timeframe pair (EA startup watermarks)</summary>
    [HttpGet("candle/watermarks")]
    public async Task<ResponseData<List<CandleWatermarkDto>>> GetCandleWatermarks()
        => await Mediator.Send(new GetCandleWatermarksQuery());

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

    /// <summary>Receive a batch of tick data from the EA</summary>
    [HttpPost("tick/batch")]
    public async Task<ResponseData<string>> ReceiveTickBatch(ReceiveTickBatchCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive a single candle from the EA</summary>
    [HttpPost("candle")]
    public async Task<ResponseData<long>> ReceiveCandle(ReceiveCandleCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive a batch of live candles from the EA</summary>
    [HttpPost("candle/batch")]
    public async Task<ResponseData<int>> ReceiveCandleBatch(ReceiveCandleBatchCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<int>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive a backfill batch of historical candles from the EA</summary>
    [HttpPost("candle/backfill")]
    public async Task<ResponseData<int>> ReceiveCandleBackfill(ReceiveCandleBackfillCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<int>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }
}
