using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.PaperTrading.Queries.GetPaperTradingStatus;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPaperTradingStatusQuery : IRequest<ResponseData<bool>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPaperTradingStatusQueryHandler
    : IRequestHandler<GetPaperTradingStatusQuery, ResponseData<bool>>
{
    private const string ConfigKey = "Engine:PaperMode";

    private readonly IReadApplicationDbContext _context;

    public GetPaperTradingStatusQueryHandler(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<bool>> Handle(
        GetPaperTradingStatusQuery request, CancellationToken cancellationToken)
    {
        var config = await _context.GetDbContext()
            .Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == ConfigKey && !x.IsDeleted, cancellationToken);

        if (config == null)
            return ResponseData<bool>.Init(false, true, "Successful", "00");

        bool isPaperMode = string.Equals(config.Value, "true", StringComparison.OrdinalIgnoreCase);

        return ResponseData<bool>.Init(isPaperMode, true, "Successful", "00");
    }
}
