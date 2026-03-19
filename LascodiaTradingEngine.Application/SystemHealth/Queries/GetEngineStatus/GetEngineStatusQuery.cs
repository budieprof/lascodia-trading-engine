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

        var activeStrategiesTask = db.Set<Domain.Entities.Strategy>()
            .CountAsync(x => x.Status == StrategyStatus.Active && !x.IsDeleted, cancellationToken);

        var openPositionsTask = db.Set<Domain.Entities.Position>()
            .CountAsync(x => x.Status == PositionStatus.Open && !x.IsDeleted, cancellationToken);

        var pendingOrdersTask = db.Set<Domain.Entities.Order>()
            .CountAsync(x => x.Status == OrderStatus.Pending && !x.IsDeleted, cancellationToken);

        var paperModeConfigTask = db.Set<Domain.Entities.EngineConfig>()
            .FirstOrDefaultAsync(x => x.Key == "PaperMode" && !x.IsDeleted, cancellationToken);

        await Task.WhenAll(activeStrategiesTask, openPositionsTask, pendingOrdersTask, paperModeConfigTask);

        var paperModeConfig = await paperModeConfigTask;

        var dto = new EngineStatusDto
        {
            IsRunning        = true,
            ActiveStrategies = await activeStrategiesTask,
            OpenPositions    = await openPositionsTask,
            PendingOrders    = await pendingOrdersTask,
            PaperMode        = paperModeConfig?.Value ?? "false",
            CheckedAt        = DateTime.UtcNow
        };

        return ResponseData<EngineStatusDto>.Init(dto, true, "Successful", "00");
    }
}
