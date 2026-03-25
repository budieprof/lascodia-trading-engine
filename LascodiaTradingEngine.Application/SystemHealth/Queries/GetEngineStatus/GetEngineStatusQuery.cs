using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetEngineStatus;

// ── DTO ───────────────────────────────────────────────────────────────────────

public class EngineStatusDto
{
    public bool     IsRunning        { get; set; }
    public int      ActiveStrategies { get; set; }
    public int      OpenPositions    { get; set; }
    public int      PendingOrders    { get; set; }
    public string   PaperMode        { get; set; } = "false";
    public DateTime CheckedAt        { get; set; }
}

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetEngineStatusQuery : IRequest<ResponseData<EngineStatusDto>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetEngineStatusQueryHandler
    : IRequestHandler<GetEngineStatusQuery, ResponseData<EngineStatusDto>>
{
    private readonly IReadApplicationDbContext _context;

    public GetEngineStatusQueryHandler(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<EngineStatusDto>> Handle(
        GetEngineStatusQuery request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var activeStrategies = await db.Set<Domain.Entities.Strategy>()
            .AsNoTracking()
            .CountAsync(x => x.Status == StrategyStatus.Active && !x.IsDeleted, cancellationToken);

        var openPositions = await db.Set<Domain.Entities.Position>()
            .AsNoTracking()
            .CountAsync(x => x.Status == PositionStatus.Open && !x.IsDeleted, cancellationToken);

        var pendingOrders = await db.Set<Domain.Entities.Order>()
            .AsNoTracking()
            .CountAsync(x => x.Status == OrderStatus.Pending && !x.IsDeleted, cancellationToken);

        var paperModeConfig = await db.Set<Domain.Entities.EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "PaperMode" && !x.IsDeleted, cancellationToken);

        var dto = new EngineStatusDto
        {
            IsRunning        = true,
            ActiveStrategies = activeStrategies,
            OpenPositions    = openPositions,
            PendingOrders    = pendingOrders,
            PaperMode        = paperModeConfig?.Value ?? "false",
            CheckedAt        = DateTime.UtcNow
        };

        return ResponseData<EngineStatusDto>.Init(dto, true, "Successful", "00");
    }
}
