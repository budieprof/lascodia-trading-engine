using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Strategies.Commands.AssignRiskProfile;

// ── Command ───────────────────────────────────────────────────────────────────

public class AssignRiskProfileCommand : IRequest<ResponseData<string>>
{
    public long  StrategyId    { get; set; }
    public long? RiskProfileId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class AssignRiskProfileCommandHandler : IRequestHandler<AssignRiskProfileCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public AssignRiskProfileCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(AssignRiskProfileCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .FirstOrDefaultAsync(x => x.Id == request.StrategyId && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Strategy not found", "-14");

        entity.RiskProfileId = request.RiskProfileId;
        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("RiskProfile assigned", true, "Successful", "00");
    }
}
