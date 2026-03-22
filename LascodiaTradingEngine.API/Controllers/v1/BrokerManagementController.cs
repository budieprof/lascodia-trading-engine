using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.BrokerManagement.Commands.SwitchBroker;
using LascodiaTradingEngine.Application.BrokerManagement.Queries.GetActiveBroker;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/broker-management")]
[ApiController]
public class BrokerManagementController : AuthControllerBase<BrokerManagementController>
{
    private readonly IBrokerFailover _brokerFailover;

    public BrokerManagementController(
        ILogger<BrokerManagementController> logger,
        IConfiguration config,
        ICurrentUserService userService,
        IBrokerFailover brokerFailover)
        : base(logger, config, userService)
    {
        _brokerFailover = brokerFailover;
    }

    /// <summary>Switch the active broker</summary>
    [HttpPut("switch")]
    public async Task<ResponseData<string>> Switch(SwitchBrokerCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get the currently active broker</summary>
    [HttpGet("active")]
    public async Task<ResponseData<string>> GetActive()
        => await Mediator.Send(new GetActiveBrokerQuery());

    /// <summary>Check whether the active broker is healthy</summary>
    [HttpGet("health")]
    public async Task<ResponseData<bool>> CheckHealth(CancellationToken cancellationToken)
    {
        bool isHealthy = await _brokerFailover.IsHealthyAsync(cancellationToken);
        return ResponseData<bool>.Init(isHealthy, true, "Successful", "00");
    }
}
