using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.RegisterEA;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.DeregisterEA;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessHeartbeat;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveSymbolSpecs;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.RefreshSymbolSpecs;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTradingSessions;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceivePositionSnapshot;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveBrokerAccountSnapshot;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveOrderSnapshot;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveDealSnapshot;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessReconciliation;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.AcknowledgeCommand;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.GetPendingCommands;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.GetActiveInstances;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessSignalFeedback;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceivePositionDelta;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.UpdateEAConfig;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;
using Microsoft.AspNetCore.RateLimiting;

namespace LascodiaTradingEngine.API.Controllers.v1;

/// <summary>
/// Handles all MQL5 Expert Advisor integration: instance registration, heartbeats, symbol specs,
/// position/order/deal snapshots, reconciliation, signal feedback, and pending command retrieval.
/// Rate-limited per EA instance. Route: api/v1/lascodia-trading-engine/ea
/// </summary>
[Route("api/v1/lascodia-trading-engine/ea")]
[ApiController]
[EnableRateLimiting("ea")]
public class ExpertAdvisorController : AuthControllerBase<ExpertAdvisorController>
{
    public ExpertAdvisorController(
        ILogger<ExpertAdvisorController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>
    /// Extracts the X-Request-Id header value sent by EA instances for request correlation.
    /// </summary>
    private string? GetRequestId() => HttpContext.Request.Headers["X-Request-Id"].FirstOrDefault();

    /// <summary>Register a new EA instance</summary>
    [HttpPost("register")]
    [Authorize(Policy = Policies.Operator)]
    [ProducesResponseType(typeof(ResponseData<long>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Register(RegisterEACommand command)
    {
        if (!ModelState.IsValid)
            return Ok(ResponseData<long>.Init(0, false, "Model state failed", "-11"));

        var result = await Mediator.Send(command);
        return Ok(result);
    }

    /// <summary>Deregister an EA instance</summary>
    [HttpPost("deregister")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> Deregister(DeregisterEACommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Process a heartbeat from an EA instance</summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(ProcessHeartbeatCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive symbol specifications from the EA</summary>
    [HttpPost("symbol-specs")]
    public async Task<IActionResult> ReceiveSymbolSpecs(ReceiveSymbolSpecsCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Request the EA to refresh and re-send symbol specifications</summary>
    [HttpPut("symbol-specs/refresh")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> RefreshSymbolSpecs(RefreshSymbolSpecsCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive trading session schedule data from the EA</summary>
    [HttpPost("trading-sessions")]
    public async Task<IActionResult> ReceiveTradingSessions(ReceiveTradingSessionsCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive a snapshot of open positions from the EA</summary>
    [HttpPost("positions/snapshot")]
    public async Task<IActionResult> ReceivePositionSnapshot(ReceivePositionSnapshotCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive an account-level broker balance/equity snapshot from the EA</summary>
    [HttpPost("account/snapshot")]
    public async Task<IActionResult> ReceiveBrokerAccountSnapshot(ReceiveBrokerAccountSnapshotCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive a snapshot of pending orders from the EA</summary>
    [HttpPost("orders/snapshot")]
    public async Task<IActionResult> ReceiveOrderSnapshot(ReceiveOrderSnapshotCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive a snapshot of recent deals from the EA</summary>
    [HttpPost("deals/snapshot")]
    public async Task<IActionResult> ReceiveDealSnapshot(ReceiveDealSnapshotCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive an order-book (DOM) snapshot from the EA — top of book plus
    /// optional multi-level depth via MarketBookGet when the broker exposes it.</summary>
    [HttpPost("orderbook/snapshot")]
    public async Task<IActionResult> ReceiveOrderBookSnapshot(
        LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveOrderBookSnapshot.ReceiveOrderBookSnapshotCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Process reconciliation between engine and broker state</summary>
    [HttpPost("reconciliation")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> ProcessReconciliation(ProcessReconciliationCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Get pending commands for an EA instance to execute</summary>
    [HttpGet("commands")]
    public async Task<IActionResult> GetPendingCommands(
        [FromQuery] string eaInstanceId,
        [FromQuery] DateTime? since)
        => Ok(await Mediator.Send(new GetPendingCommandsQuery
        {
            EAInstanceId = eaInstanceId,
            Since        = since
        }));

    /// <summary>Acknowledge execution of a command by the EA</summary>
    [HttpPut("commands/{id}/ack")]
    public async Task<IActionResult> AcknowledgeCommand(long id, AcknowledgeCommandCommand command)
    {
        command.Id = id;
        return Ok(await Mediator.Send(command));
    }

    /// <summary>
    /// Hot-reload EA safety configuration parameters without requiring an EA restart.
    /// Queues an UpdateConfig command for targeted EA instance(s).
    /// Zero values are ignored by the EA (keeps current value).
    /// </summary>
    [HttpPost("commands/update-config")]
    [Authorize(Policy = Policies.Operator)]
    public async Task<IActionResult> UpdateEAConfig(UpdateEAConfigCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Get all active EA instances</summary>
    [HttpGet("instances")]
    public async Task<IActionResult> GetActiveInstances()
        => Ok(await Mediator.Send(new GetActiveInstancesQuery()));

    /// <summary>Receive signal feedback from EA (deferred, dropped, expired signals)</summary>
    [HttpPost("signal-feedback")]
    public async Task<IActionResult> ProcessSignalFeedback(ProcessSignalFeedbackCommand command)
        => Ok(await Mediator.Send(command));

    /// <summary>Receive incremental position changes (opened/closed/modified)</summary>
    [HttpPost("positions/delta")]
    public async Task<IActionResult> ReceivePositionDelta(ReceivePositionDeltaCommand command)
        => Ok(await Mediator.Send(command));
}
