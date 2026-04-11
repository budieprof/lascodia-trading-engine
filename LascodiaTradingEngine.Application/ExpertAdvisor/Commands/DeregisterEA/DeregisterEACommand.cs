using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Events;
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
    private readonly IIntegrationEventService _eventBus;
    private readonly ILogger<DeregisterEACommandHandler> _logger;

    public DeregisterEACommandHandler(
        IWriteApplicationDbContext context,
        IEAOwnershipGuard ownershipGuard,
        IIntegrationEventService eventBus,
        ILogger<DeregisterEACommandHandler> logger)
    {
        _context        = context;
        _ownershipGuard = ownershipGuard;
        _eventBus       = eventBus;
        _logger         = logger;
    }

    public async Task<ResponseData<string>> Handle(DeregisterEACommand request, CancellationToken cancellationToken)
    {
        if (!await _ownershipGuard.IsOwnerAsync(request.InstanceId, cancellationToken))
            return ResponseData<string>.Init(null, false, "Unauthorized: caller does not own this EA instance", "-403");

        var dbContext = _context.GetDbContext();

        var entity = await dbContext
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

        // ── Reassign orphaned symbols to active standby instances ────────────
        // Same logic as EAHealthMonitorWorker's disconnect handling to ensure
        // graceful deregistration doesn't leave symbols without coverage.
        var orphanedSymbols = (entity.Symbols ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var reassignedSymbols = new List<string>();

        if (orphanedSymbols.Length > 0)
        {
            var activeInstances = await dbContext
                .Set<Domain.Entities.EAInstance>()
                .Where(e => e.TradingAccountId == entity.TradingAccountId
                         && e.Status == EAInstanceStatus.Active
                         && e.Id != entity.Id
                         && !e.IsDeleted)
                .ToListAsync(cancellationToken);

            foreach (var sym in orphanedSymbols)
            {
                var candidate = activeInstances.FirstOrDefault(s =>
                    s.Symbols is null || !s.Symbols.Split(',').Contains(sym, StringComparer.OrdinalIgnoreCase));

                if (candidate is not null)
                {
                    candidate.Symbols = string.IsNullOrWhiteSpace(candidate.Symbols)
                        ? sym
                        : $"{candidate.Symbols},{sym}";

                    reassignedSymbols.Add(sym);
                    _logger.LogInformation(
                        "DeregisterEA: reassigned symbol {Symbol} from {FromInstance} to {ToInstance}",
                        sym, entity.InstanceId, candidate.InstanceId);
                }
                else
                {
                    _logger.LogWarning(
                        "DeregisterEA: no active standby instance for symbol {Symbol} (instance {InstanceId})",
                        sym, entity.InstanceId);
                }
            }
        }

        await _eventBus.SaveAndPublish(_context, new EAInstanceDeregisteredIntegrationEvent
        {
            EAInstanceId     = entity.Id,
            InstanceId       = entity.InstanceId,
            TradingAccountId = entity.TradingAccountId,
            Symbols          = entity.Symbols,
            DeregisteredAt   = entity.DeregisteredAt!.Value,
        });

        return ResponseData<string>.Init(null, true, "Successful", "00");
    }
}
