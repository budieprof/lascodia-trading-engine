using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.PerformanceAttribution.Queries.DTOs;
using LascodiaTradingEngine.Application.PerformanceAttribution.Queries.GetStrategyAttribution;
using LascodiaTradingEngine.Application.PerformanceAttribution.Queries.GetAllAttributions;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Provides performance attribution breakdowns per strategy and across the full portfolio.
/// Route: api/v1/lascodia-trading-engine/performance
/// </summary>
[Route("api/v1/lascodia-trading-engine/performance")]
[ApiController]
public class PerformanceAttributionController : AuthControllerBase<PerformanceAttributionController>
{
    public PerformanceAttributionController(
        ILogger<PerformanceAttributionController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get performance attribution for a single strategy</summary>
    [HttpGet("{strategyId}")]
    public async Task<ResponseData<PerformanceAttributionDto>> GetByStrategy(long strategyId)
        => await Mediator.Send(new GetStrategyAttributionQuery { StrategyId = strategyId });

    /// <summary>Get performance attribution for all strategies</summary>
    [HttpGet("all")]
    public async Task<ResponseData<List<PerformanceAttributionDto>>> GetAll()
        => await Mediator.Send(new GetAllAttributionsQuery());
}
