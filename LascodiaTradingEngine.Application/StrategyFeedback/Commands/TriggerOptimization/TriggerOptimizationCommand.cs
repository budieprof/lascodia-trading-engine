using FluentValidation;
using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Commands.TriggerOptimization;

// ── Command ───────────────────────────────────────────────────────────────────

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

public class TriggerOptimizationCommandHandler : IRequestHandler<TriggerOptimizationCommand, ResponseData<long>>
{
    private readonly IWriteApplicationDbContext _context;

    public TriggerOptimizationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<long>> Handle(TriggerOptimizationCommand request, CancellationToken cancellationToken)
    {
        var entity = new Domain.Entities.OptimizationRun
        {
            StrategyId  = request.StrategyId,
            TriggerType = Enum.Parse<TriggerType>(request.TriggerType, ignoreCase: true),
            Status      = OptimizationRunStatus.Queued,
            StartedAt   = DateTime.UtcNow
        };

        await _context.GetDbContext()
            .Set<Domain.Entities.OptimizationRun>()
            .AddAsync(entity, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<long>.Init(entity.Id, true, "Optimization run queued successfully", "00");
    }
}
