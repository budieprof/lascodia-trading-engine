using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.ProcessHeartbeat;

// ── Command ───────────────────────────────────────────────────────────────────

public class ProcessHeartbeatCommand : IRequest<ResponseData<HeartbeatResponse>>
{
    public required string InstanceId { get; set; }
}

public class HeartbeatResponse
{
    public DateTime ServerTime { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class ProcessHeartbeatCommandValidator : AbstractValidator<ProcessHeartbeatCommand>
{
    public ProcessHeartbeatCommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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
                  && x.Status == EAInstanceStatus.Active
                  && !x.IsDeleted,
                cancellationToken);

        if (entity == null)
            return ResponseData<HeartbeatResponse>.Init(null, false, "EA instance not found or not active", "-14");

        entity.LastHeartbeat = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var response = new HeartbeatResponse { ServerTime = DateTime.UtcNow };
        return ResponseData<HeartbeatResponse>.Init(response, true, "Successful", "00");
    }
}
