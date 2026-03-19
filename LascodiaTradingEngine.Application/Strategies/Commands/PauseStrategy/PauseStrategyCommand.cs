using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Commands.PauseStrategy;

// ── Command ───────────────────────────────────────────────────────────────────

public class PauseStrategyCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class PauseStrategyCommandHandler : IRequestHandler<PauseStrategyCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public PauseStrategyCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(PauseStrategyCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        entity.Status = StrategyStatus.Paused;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Paused", true, "Successful", "00");
    }
}
