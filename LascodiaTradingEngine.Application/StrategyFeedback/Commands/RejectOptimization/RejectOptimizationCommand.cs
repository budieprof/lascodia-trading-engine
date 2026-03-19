using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Commands.RejectOptimization;

// ── Command ───────────────────────────────────────────────────────────────────

public class RejectOptimizationCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class RejectOptimizationCommandHandler : IRequestHandler<RejectOptimizationCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public RejectOptimizationCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(RejectOptimizationCommand request, CancellationToken cancellationToken)
    {
        var run = await _context.GetDbContext()
            .Set<Domain.Entities.OptimizationRun>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (run is null)
            return ResponseData<string>.Init(null, false, "Optimization run not found", "-14");

        run.Status = OptimizationRunStatus.Rejected;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Optimization run rejected", true, "Successful", "00");
    }
}
