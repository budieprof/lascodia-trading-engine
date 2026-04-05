using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExpertAdvisor.Commands.UpdateEAConfig;

/// <summary>
/// Hot-reload EA safety configuration parameters without requiring an EA restart.
/// Queues an <see cref="EACommandType.UpdateConfig"/> command for each targeted EA instance.
/// Zero/null values are ignored by the EA (keeps current value).
/// </summary>
public class UpdateEAConfigCommand : IRequest<ResponseData<string>>
{
    /// <summary>Target a specific EA instance. If null/empty, targets ALL active instances.</summary>
    public string? TargetInstanceId { get; set; }

    // --- Per-instance safety parameters (CircuitBreaker) ---

    /// <summary>Max positions per symbol per instance. 0 = keep current.</summary>
    public int MaxPosPerSymbol { get; set; }

    /// <summary>Max lot size per order. 0 = keep current.</summary>
    public double MaxLotPerOrder { get; set; }

    /// <summary>Max spread in points for execution. 0 = keep current.</summary>
    public int MaxSpreadPoints { get; set; }

    /// <summary>Consecutive losses before safety pause. 0 = keep current.</summary>
    public int MaxConsecLosses { get; set; }

    /// <summary>Minutes to pause after consecutive losses. 0 = keep current.</summary>
    public int ConsecLossPauseMin { get; set; }

    /// <summary>Max daily loss % per symbol (0 = disabled/keep current).</summary>
    public double MaxDailyLossPerSymbolPct { get; set; }

    // --- Global safety parameters (GlobalCircuitBreaker) ---

    /// <summary>Max total open positions across all instances. 0 = keep current.</summary>
    public int MaxOpenPositions { get; set; }

    /// <summary>Max daily loss % of equity (global). 0 = keep current. Capped at 50% by EA.</summary>
    public double MaxDailyLossPct { get; set; }

    /// <summary>Max orders per minute across all instances. 0 = keep current.</summary>
    public int MaxOrdersPerMin { get; set; }

    /// <summary>Max total open lots across all instances. 0 = keep current.</summary>
    public double MaxTotalLots { get; set; }
}

// ── Validator ─────────────────────────────────────────────────────────────────

/// <summary>
/// Validates non-negative values for MaxPosPerSymbol, MaxLotPerOrder, and MaxSpreadPoints.
/// </summary>
public class UpdateEAConfigCommandValidator : AbstractValidator<UpdateEAConfigCommand>
{
    public UpdateEAConfigCommandValidator()
    {
        RuleFor(x => x.MaxPosPerSymbol).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxLotPerOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MaxSpreadPoints).GreaterThanOrEqualTo(0);
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles EA config hot-reload. Serialises the safety parameters to JSON, determines the target
/// instances (all active, or a specific one), and queues an UpdateConfig EACommand for each.
/// Returns -14 if no active instances match the target.
/// </summary>
public class UpdateEAConfigCommandHandler : IRequestHandler<UpdateEAConfigCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public UpdateEAConfigCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(UpdateEAConfigCommand request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        // Build the parameters JSON (EA ignores zero values)
        var parameters = JsonSerializer.Serialize(new
        {
            maxPosPerSymbol         = request.MaxPosPerSymbol,
            maxLotPerOrder          = request.MaxLotPerOrder,
            maxSpreadPoints         = request.MaxSpreadPoints,
            maxConsecLosses         = request.MaxConsecLosses,
            consecLossPauseMin      = request.ConsecLossPauseMin,
            maxDailyLossPerSymbolPct = request.MaxDailyLossPerSymbolPct,
            maxOpenPositions        = request.MaxOpenPositions,
            maxDailyLossPct         = request.MaxDailyLossPct,
            maxOrdersPerMin         = request.MaxOrdersPerMin,
            maxTotalLots            = request.MaxTotalLots,
        });

        // Determine target instances
        var instanceQuery = dbContext.Set<Domain.Entities.EAInstance>()
            .Where(x => !x.IsDeleted && x.Status == EAInstanceStatus.Active);

        if (!string.IsNullOrWhiteSpace(request.TargetInstanceId))
            instanceQuery = instanceQuery.Where(x => x.InstanceId == request.TargetInstanceId);

        var instances = await instanceQuery.ToListAsync(cancellationToken);

        if (instances.Count == 0)
            return ResponseData<string>.Init(null, false, "No active EA instances found", "-14");

        // Queue an UpdateConfig command for each targeted instance
        foreach (var instance in instances)
        {
            await dbContext.Set<Domain.Entities.EACommand>().AddAsync(new Domain.Entities.EACommand
            {
                TargetInstanceId = instance.InstanceId,
                CommandType      = EACommandType.UpdateConfig,
                Parameters       = parameters,
            }, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var msg = $"UpdateConfig command queued for {instances.Count} EA instance(s)";
        return ResponseData<string>.Init(msg, true, "Successful", "00");
    }
}
