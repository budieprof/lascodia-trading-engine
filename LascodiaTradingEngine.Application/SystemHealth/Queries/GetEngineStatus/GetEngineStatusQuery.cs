using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetEngineStatus;

// ── DTO ───────────────────────────────────────────────────────────────────────

/// <summary>Real-time snapshot of the trading engine's operational status.</summary>
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

/// <summary>Queries the current engine status including active strategy count, open positions, pending orders, and paper mode.</summary>
public class GetEngineStatusQuery : IRequest<ResponseData<EngineStatusDto>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Counts active strategies, open positions, and pending orders from the read-only context,
/// and reads the PaperMode engine config to build the status DTO.
/// </summary>
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
