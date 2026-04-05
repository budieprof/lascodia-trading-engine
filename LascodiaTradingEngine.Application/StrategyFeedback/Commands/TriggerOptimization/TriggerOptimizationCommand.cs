using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Commands.TriggerOptimization;

// ── Command ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queues a parameter optimization run for a strategy. The <c>OptimizationWorker</c>
/// picks up queued runs and searches for improved strategy parameters.
/// </summary>
public class TriggerOptimizationCommand : IRequest<ResponseData<long>>
{
    public long   StrategyId  { get; set; }
    public string TriggerType { get; set; } = "Manual";
}

// ── Validator ─────────────────────────────────────────────────────────────────

public class TriggerOptimizationCommandValidator : AbstractValidator<TriggerOptimizationCommand>
{
    public TriggerOptimizationCommandValidator()
    {
        RuleFor(x => x.StrategyId)
            .GreaterThan(0).WithMessage("StrategyId must be greater than zero");
    }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Creates a new optimization run entity with Queued status for asynchronous processing.</summary>
public class TriggerOptimizationCommandHandler : IRequestHandler<TriggerOptimizationCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public TriggerOptimizationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(TriggerOptimizationCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();
        var activeRun = await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == request.StrategyId
                     && !r.IsDeleted
                     && (r.Status == OptimizationRunStatus.Queued || r.Status == OptimizationRunStatus.Running))
            .OrderBy(r => r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeRun is not null)
        {
            return ResponseData<long>.Init(
                activeRun.Id,
                true,
                "An optimization run is already queued or running for this strategy",
                "00");
        }

        var entity = new OptimizationRun
        {
            StrategyId  = request.StrategyId,
            TriggerType = Enum.Parse<TriggerType>(request.TriggerType, ignoreCase: true),
            Status      = OptimizationRunStatus.Queued,
            StartedAt   = DateTime.UtcNow
        };

        await db.Set<OptimizationRun>()
            .AddAsync(entity, cancellationToken);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsActiveQueueConstraintViolation(ex))
        {
            activeRun = await db.Set<OptimizationRun>()
                .Where(r => r.StrategyId == request.StrategyId
                         && !r.IsDeleted
                         && (r.Status == OptimizationRunStatus.Queued || r.Status == OptimizationRunStatus.Running))
                .OrderBy(r => r.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (activeRun is not null)
            {
                return ResponseData<long>.Init(
                    activeRun.Id,
                    true,
                    "An optimization run is already queued or running for this strategy",
                    "00");
            }

            throw;
        }

        return ResponseData<long>.Init(entity.Id, true, "Optimization run queued successfully", "00");
    }

    private static bool IsActiveQueueConstraintViolation(DbUpdateException ex)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("IX_OptimizationRun_ActivePerStrategy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
               && message.Contains("OptimizationRun", StringComparison.OrdinalIgnoreCase)
               && message.Contains("StrategyId", StringComparison.OrdinalIgnoreCase);
    }
}
