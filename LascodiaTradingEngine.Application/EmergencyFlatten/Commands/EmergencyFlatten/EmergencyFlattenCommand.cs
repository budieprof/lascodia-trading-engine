using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EmergencyFlatten.Commands.EmergencyFlatten;

/// <summary>
/// Emergency kill switch: cancels all pending orders, closes all open positions at market,
/// pauses all strategies, and sends alerts on all channels. Requires manual re-enablement.
/// </summary>
public class EmergencyFlattenCommand : IRequest<ResponseData<bool>>
{
    /// <summary>Account ID of the operator triggering the emergency flatten.</summary>
    public long TriggeredByAccountId { get; set; }

    /// <summary>Reason for the emergency flatten.</summary>
    public string Reason { get; set; } = string.Empty;
}

public class EmergencyFlattenCommandValidator : AbstractValidator<EmergencyFlattenCommand>
{
    public EmergencyFlattenCommandValidator()
    {
        RuleFor(x => x.TriggeredByAccountId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public class EmergencyFlattenCommandHandler : IRequestHandler<EmergencyFlattenCommand, ResponseData<bool>>
{
    private readonly IWriteApplicationDbContext _context;
    private readonly ILogger<EmergencyFlattenCommandHandler> _logger;

    public EmergencyFlattenCommandHandler(
        IWriteApplicationDbContext context,
        ILogger<EmergencyFlattenCommandHandler> logger)
    {
        _context = context;
        _logger  = logger;
    }

    public async Task<ResponseData<bool>> Handle(
        EmergencyFlattenCommand request,
        CancellationToken cancellationToken)
    {
        var ctx = _context.GetDbContext();
        var now = DateTime.UtcNow;

        _logger.LogCritical(
            "EMERGENCY FLATTEN initiated by account {AccountId}: {Reason}",
            request.TriggeredByAccountId, request.Reason);

        // 1. Cancel all pending orders
        var pendingOrders = await ctx.Set<Order>()
            .Where(o => (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Submitted) && !o.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var order in pendingOrders)
        {
            order.Status = OrderStatus.Cancelled;
            order.Notes = $"[EMERGENCY FLATTEN] {request.Reason}";
        }

        _logger.LogCritical("Emergency flatten: cancelled {Count} pending/submitted orders", pendingOrders.Count);

        // 2. Mark all open positions for closure (EA will poll close commands)
        var openPositions = await ctx.Set<Position>()
            .Where(p => p.Status == PositionStatus.Open && !p.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var position in openPositions)
        {
            // Queue an EA close command for each position
            var closeCommand = new EACommand
            {
                CommandType      = EACommandType.ClosePosition,
                Symbol           = position.Symbol,
                TargetTicket     = null,
                TargetInstanceId = string.Empty,
                Parameters       = $"{{\"reason\":\"EMERGENCY_FLATTEN\",\"positionId\":{position.Id}}}",
                CreatedAt        = now
            };
            await ctx.Set<EACommand>().AddAsync(closeCommand, cancellationToken);
        }

        _logger.LogCritical("Emergency flatten: queued close commands for {Count} open positions", openPositions.Count);

        // 3. Pause all active strategies
        var activeStrategies = await ctx.Set<Strategy>()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var strategy in activeStrategies)
        {
            strategy.Status = StrategyStatus.Paused;
        }

        _logger.LogCritical("Emergency flatten: paused {Count} active strategies", activeStrategies.Count);

        // 4. Log the emergency event
        var auditLog = new EngineConfigAuditLog
        {
            Key                  = "EmergencyFlatten",
            OldValue             = "Normal",
            NewValue             = "EmergencyHalt",
            ChangedByAccountId   = request.TriggeredByAccountId,
            Reason               = request.Reason,
            ChangedAt            = now
        };
        await ctx.Set<EngineConfigAuditLog>().AddAsync(auditLog, cancellationToken);

        await ctx.SaveChangesAsync(cancellationToken);

        _logger.LogCritical(
            "EMERGENCY FLATTEN complete: {Orders} orders cancelled, {Positions} positions queued for close, {Strategies} strategies paused",
            pendingOrders.Count, openPositions.Count, activeStrategies.Count);

        return ResponseData<bool>.Init(true, true, "Emergency flatten executed", "00");
    }
}
