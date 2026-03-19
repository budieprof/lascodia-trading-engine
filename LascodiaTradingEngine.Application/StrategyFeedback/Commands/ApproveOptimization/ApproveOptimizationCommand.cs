using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Commands.ApproveOptimization;

// ── Command ───────────────────────────────────────────────────────────────────

public class ApproveOptimizationCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class ApproveOptimizationCommandHandler : IRequestHandler<ApproveOptimizationCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public ApproveOptimizationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(ApproveOptimizationCommand request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var run = await db.Set<Domain.Entities.OptimizationRun>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (run is null)
            return ResponseData<string>.Init(null, false, "Optimization run not found", "-14");

        if (run.Status != OptimizationRunStatus.Completed)
            return ResponseData<string>.Init(null, false, "Only completed optimization runs can be approved", "-11");

        // Apply best parameters to the strategy
        if (!string.IsNullOrWhiteSpace(run.BestParametersJson))
        {
            var strategy = await db.Set<Domain.Entities.Strategy>()
                .FirstOrDefaultAsync(x => x.Id == run.StrategyId && !x.IsDeleted, cancellationToken);

            if (strategy is not null)
                strategy.ParametersJson = run.BestParametersJson;
        }

        run.Status     = OptimizationRunStatus.Approved;
        run.ApprovedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Optimization run approved and strategy parameters updated", true, "Successful", "00");
    }
}
