using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessHeartbeat;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Processes a heartbeat from an EA instance to confirm it is still alive and connected.
/// Updates the instance's LastHeartbeat timestamp and returns the current server time
/// so the EA can detect clock drift.
/// </summary>
public class ProcessHeartbeatCommand : IRequest<ResponseData<HeartbeatResponse>>
{
    /// <summary>Unique identifier of the EA instance sending the heartbeat.</summary>
    public required string InstanceId { get; set; }
}

/// <summary>
/// Response payload returned to the EA after a successful heartbeat, containing the engine's current UTC time.
/// </summary>
public class HeartbeatResponse
{
    /// <summary>Current UTC time on the engine server. The EA uses this to detect clock skew.</summary>
    public DateTime ServerTime { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that the InstanceId is non-empty.
/// </summary>
public class ProcessHeartbeatCommandValidator : AbstractValidator<ProcessHeartbeatCommand>
{
    public ProcessHeartbeatCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles heartbeat processing. Verifies caller ownership, updates LastHeartbeat on the active
/// EAInstance record, and returns the current server time. Returns -14 if the instance is not found or inactive.
/// </summary>
public class ProcessHeartbeatCommandHandler : IRequestHandler<ProcessHeartbeatCommand, ResponseData<HeartbeatResponse>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public ProcessHeartbeatCommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<HeartbeatResponse>> Handle(ProcessHeartbeatCommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<HeartbeatResponse>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(
                x => x.InstanceId == request.InstanceId
                  && (x.Status == EAInstanceStatus.Active || x.Status == EAInstanceStatus.Disconnected)
                  && !x.IsDeleted,
                cancellationToken);

        if (entity == null)
            return ResponseData<HeartbeatResponse>.Init(null, false, "EA instance not found or not active", "-14");

        // Re-activate disconnected instances: a valid heartbeat proves the EA is alive.
        // The EAHealthMonitorWorker marks instances as Disconnected after missed heartbeats,
        // but the EA may reconnect (e.g. after weekend, network outage, or MT5 restart).
        if (entity.Status == EAInstanceStatus.Disconnected)
            entity.Status = EAInstanceStatus.Active;

        entity.LastHeartbeat = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        // Capture server time AFTER save to ensure the response reflects
        // a consistent point in time following successful persistence.
        var serverTime = DateTime.UtcNow;
        var response = new HeartbeatResponse { ServerTime = serverTime };
        return ResponseData<HeartbeatResponse>.Init(response, true, "Successful", "00");
    }
}
