using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.RateLimiting.Queries.GetApiQuotaStatus;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Exposes API rate-limit quota status for a given broker key.
/// Route: api/v1/lascodia-trading-engine/rate-limit
/// </summary>
[Route("api/v1/lascodia-trading-engine/rate-limit")]
[ApiController]
public class RateLimitingController : AuthControllerBase<RateLimitingController>
{
    public RateLimitingController(
        ILogger<RateLimitingController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Get API quota status for a given broker key</summary>
    [HttpGet("quota/{brokerKey}")]
    public async Task<ResponseData<ApiQuotaStatusDto>> GetQuotaStatus(string brokerKey)
        => await Mediator.Send(new GetApiQuotaStatusQuery { BrokerKey = brokerKey });
}
