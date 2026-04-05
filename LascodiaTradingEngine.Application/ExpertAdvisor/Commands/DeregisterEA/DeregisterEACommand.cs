using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.DeregisterEA;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Gracefully deregisters an EA instance by marking it as ShuttingDown.
/// The engine will stop evaluating strategies for symbols owned by this instance
/// and mark them as DATA_UNAVAILABLE if no other instance covers them.
/// </summary>
public class DeregisterEACommand : IRequest<ResponseData<string>>
{
    /// <summary>Unique identifier of the EA instance to deregister.</summary>
    public required string InstanceId { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates that the InstanceId is non-empty.
/// </summary>
public class DeregisterEACommandValidator : AbstractValidator<DeregisterEACommand>
{
    public DeregisterEACommandValidator()
    {
        RuleFor(x => x.InstanceId)
            .NotEmpty().WithMessage("InstanceId cannot be empty");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles EA deregistration. Verifies caller ownership, locates the active instance,
/// sets its status to ShuttingDown, and records the deregistration timestamp.
/// Returns -14 if the instance is not found or already shutting down.
/// </summary>
public class DeregisterEACommandHandler : IRequestHandler<DeregisterEACommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public DeregisterEACommandHandler(IWriteApplicationDbContext context, IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<string>> Handle(DeregisterEACommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.EAInstance>()
            .FirstOrDefaultAsync(
                x => x.InstanceId == request.InstanceId
                  && x.Status != EAInstanceStatus.ShuttingDown
                  && !x.IsDeleted,
                cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "EA instance not found", "-14");

        entity.Status         = EAInstanceStatus.ShuttingDown;
        entity.DeregisteredAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
