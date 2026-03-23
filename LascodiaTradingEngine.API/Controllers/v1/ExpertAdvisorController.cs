using Microsoft.AspNetCore.Mvc;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedApplication.Common.Services;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.RegisterEA;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.DeregisterEA;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessHeartbeat;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveSymbolSpecs;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.RefreshSymbolSpecs;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveTradingSessions;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceivePositionSnapshot;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveOrderSnapshot;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ReceiveDealSnapshot;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessReconciliation;
using LascodiaTradingEngine.Application.ExpertAdvisor.Commands.AcknowledgeCommand;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.GetPendingCommands;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.GetActiveInstances;
using LascodiaTradingEngine.Application.ExpertAdvisor.Queries.DTOs;

namespace LascodiaTradingEngine.API.Controllers.v1;

[Route("api/v1/lascodia-trading-engine/ea")]
[ApiController]
public class ExpertAdvisorController : AuthControllerBase<ExpertAdvisorController>
{
    public ExpertAdvisorController(
        ILogger<ExpertAdvisorController> logger,
        IConfiguration config,
        ICurrentUserService userService)
        : base(logger, config, userService) { }

    /// <summary>Register a new EA instance</summary>
    [HttpPost("register")]
    public async Task<ResponseData<long>> Register(RegisterEACommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<long>.Init(0, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Deregister an EA instance</summary>
    [HttpPost("deregister")]
    public async Task<ResponseData<string>> Deregister(DeregisterEACommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Process a heartbeat from an EA instance</summary>
    [HttpPost("heartbeat")]
    public async Task<ResponseData<HeartbeatResponse>> Heartbeat(ProcessHeartbeatCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<HeartbeatResponse>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive symbol specifications from the EA</summary>
    [HttpPost("symbol-specs")]
    public async Task<ResponseData<string>> ReceiveSymbolSpecs(ReceiveSymbolSpecsCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Request the EA to refresh and re-send symbol specifications</summary>
    [HttpPut("symbol-specs/refresh")]
    public async Task<ResponseData<string>> RefreshSymbolSpecs(RefreshSymbolSpecsCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive trading session schedule data from the EA</summary>
    [HttpPost("trading-sessions")]
    public async Task<ResponseData<string>> ReceiveTradingSessions(ReceiveTradingSessionsCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive a snapshot of open positions from the EA</summary>
    [HttpPost("positions/snapshot")]
    public async Task<ResponseData<string>> ReceivePositionSnapshot(ReceivePositionSnapshotCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive a snapshot of pending orders from the EA</summary>
    [HttpPost("orders/snapshot")]
    public async Task<ResponseData<string>> ReceiveOrderSnapshot(ReceiveOrderSnapshotCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Receive a snapshot of recent deals from the EA</summary>
    [HttpPost("deals/snapshot")]
    public async Task<ResponseData<string>> ReceiveDealSnapshot(ReceiveDealSnapshotCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<string>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Process reconciliation between engine and broker state</summary>
    [HttpPost("reconciliation")]
    public async Task<ResponseData<ReconciliationResult>> ProcessReconciliation(ProcessReconciliationCommand command)
    {
        if (!ModelState.IsValid)
            return ResponseData<ReconciliationResult>.Init(null, false, "Model state failed", "-11");

        return await Mediator.Send(command);
    }

    /// <summary>Get pending commands for an EA instance to execute</summary>
    [HttpGet("commands")]
    public async Task<ResponseData<List<EACommandDto>>> GetPendingCommands(
        [FromQuery] string eaInstanceId,
        [FromQuery] DateTime? since)
        => await Mediator.Send(new GetPendingCommandsQuery
        {
            EAInstanceId = eaInstanceId,
            Since        = since
        });

    /// <summary>Acknowledge execution of a command by the EA</summary>
    [HttpPut("commands/{id}/ack")]
    public async Task<ResponseData<string>> AcknowledgeCommand(long id, AcknowledgeCommandCommand command)
    {
        command.Id = id;
        return await Mediator.Send(command);
    }

    /// <summary>Get all active EA instances</summary>
    [HttpGet("instances")]
    public async Task<ResponseData<List<EAInstanceDto>>> GetActiveInstances()
        => await Mediator.Send(new GetActiveInstancesQuery());
}
