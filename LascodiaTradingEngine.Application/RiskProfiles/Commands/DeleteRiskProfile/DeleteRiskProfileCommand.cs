using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.RiskProfiles.Commands.DeleteRiskProfile;

// ── Command ───────────────────────────────────────────────────────────────────

public class DeleteRiskProfileCommand : IRequest<ResponseData<string>>
{
    public long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class DeleteRiskProfileCommandHandler : IRequestHandler<DeleteRiskProfileCommand, ResponseData<string>>
{
    private readonly IWriteApplicationDbContext _context;

    public DeleteRiskProfileCommandHandler(IWriteApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<string>> Handle(DeleteRiskProfileCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.RiskProfile>()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<string>.Init(null, false, "Risk profile not found", "-14");

        if (entity.IsDefault)
            return ResponseData<string>.Init(null, false, "Cannot delete the default risk profile", "-11");

        entity.IsDeleted = true;

        await _context.SaveChangesAsync(cancellationToken);

        return ResponseData<string>.Init("Deleted", true, "Successful", "00");
    }
}
