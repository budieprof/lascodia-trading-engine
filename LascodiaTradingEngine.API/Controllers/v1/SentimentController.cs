using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.Sentiment.Commands.RecordSentiment;
using LascodiaTradingEngine.Application.Sentiment.Commands.IngestCOTReport;
using LascodiaTradingEngine.Application.Sentiment.Queries.DTOs;
using LascodiaTradingEngine.Application.Sentiment.Queries.GetLatestSentiment;
using LascodiaTradingEngine.Application.Sentiment.Queries.GetPagedCOTReports;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Records market sentiment snapshots and ingests COT (Commitment of Traders) reports for sentiment analysis.
/// Route: api/v1/lascodia-trading-engine/sentiment
/// </summary>
[Route("api/v1/lascodia-trading-engine/sentiment")]
[ApiController]
public class SentimentController : AuthControllerBase<SentimentController>
{
    public SentimentController(
        ILogger<SentimentController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Record a new sentiment snapshot</summary>
    [HttpPost("snapshot")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<long>> RecordSentiment(RecordSentimentCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Ingest a COT report</summary>
    [HttpPost("cot")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<ResponseData<long>> IngestCOT(IngestCOTReportCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get the latest sentiment snapshot for a symbol</summary>
    [HttpGet("latest/{symbol}")]
    public async Task<ResponseData<SentimentSnapshotDto>> GetLatest(string symbol)
        => await Mediator.Send(new GetLatestSentimentQuery { Symbol = symbol });

    /// <summary>Get paged COT reports</summary>
    [HttpPost("cot/list")]
    public async Task<ResponseData<PagedData<COTReportDto>>> GetPagedCOT(GetPagedCOTReportsQuery query)
    {
        if (!ModelState.IsValid)
            return ResponseData<PagedData<COTReportDto>>.Init(null, false, "Model state failed", "-11");

        Logger.LogInformation(query.GetJson());
        return await Mediator.Send(query);
    }
}
