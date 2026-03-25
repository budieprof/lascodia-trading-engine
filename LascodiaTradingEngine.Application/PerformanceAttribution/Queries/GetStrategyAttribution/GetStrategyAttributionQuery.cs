using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.PerformanceAttribution.Queries.DTOs;

namespace LascodiaTradingEngine.Application.PerformanceAttribution.Queries.GetStrategyAttribution;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetStrategyAttributionQuery : IRequest<ResponseData<PerformanceAttributionDto>>
{
    public long StrategyId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetStrategyAttributionQueryHandler
    : IRequestHandler<GetStrategyAttributionQuery, ResponseData<PerformanceAttributionDto>>
{
    private readonly IReadApplicationDbContext _context;

    public GetStrategyAttributionQueryHandler(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<PerformanceAttributionDto>> Handle(
        GetStrategyAttributionQuery request, CancellationToken cancellationToken)
    {
        var strategy = await _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.StrategyId && !x.IsDeleted, cancellationToken);

        if (strategy is null)
            return ResponseData<PerformanceAttributionDto>.Init(null, false, "Strategy not found", "-14");

        var snapshot = await _context.GetDbContext()
            .Set<Domain.Entities.StrategyPerformanceSnapshot>()
            .AsNoTracking()
            .Where(x => x.StrategyId == request.StrategyId && !x.IsDeleted)
            .OrderByDescending(x => x.EvaluatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot is null)
            return ResponseData<PerformanceAttributionDto>.Init(null, false, "No performance data found", "-14");

        var totalTrades = snapshot.WindowTrades;
        var dto = new PerformanceAttributionDto
        {
            StrategyId         = strategy.Id,
            StrategyName       = strategy.Name,
            TotalTrades        = totalTrades,
            WinRate            = snapshot.WinRate,
            TotalPnL           = snapshot.TotalPnL,
            AveragePnLPerTrade = totalTrades > 0 ? snapshot.TotalPnL / totalTrades : 0m,
            SharpeRatio        = snapshot.SharpeRatio,
            MaxDrawdownPct     = snapshot.MaxDrawdownPct
        };

        return ResponseData<PerformanceAttributionDto>.Init(dto, true, "Successful", "00");
    }
}
