using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.PerformanceAttribution.Queries.DTOs;

namespace LascodiaTradingEngine.Application.PerformanceAttribution.Queries.GetAllAttributions;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetAllAttributionsQuery : IRequest<ResponseData<List<PerformanceAttributionDto>>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetAllAttributionsQueryHandler
    : IRequestHandler<GetAllAttributionsQuery, ResponseData<List<PerformanceAttributionDto>>>
{
    private readonly IReadApplicationDbContext _context;

    public GetAllAttributionsQueryHandler(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<ResponseData<List<PerformanceAttributionDto>>> Handle(
        GetAllAttributionsQuery request, CancellationToken cancellationToken)
    {
        var db = _context.GetDbContext();

        var strategies = await db.Set<Domain.Entities.Strategy>()
            .Where(x => !x.IsDeleted)
            .ToListAsync(cancellationToken);

        var strategyIds = strategies.Select(s => s.Id).ToList();

        var allSnapshots = await db.Set<Domain.Entities.StrategyPerformanceSnapshot>()
            .Where(x => strategyIds.Contains(x.StrategyId) && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        // Take latest snapshot per strategy
        var latestSnapshots = allSnapshots
            .GroupBy(x => x.StrategyId)
            .Select(g => g.OrderByDescending(x => x.EvaluatedAt).First())
            .ToList();

        var strategyMap = strategies.ToDictionary(s => s.Id, s => s.Name);

        var dtos = latestSnapshots.Select(snapshot =>
        {
            var totalTrades = snapshot.WindowTrades;
            return new PerformanceAttributionDto
            {
                StrategyId         = snapshot.StrategyId,
                StrategyName       = strategyMap.GetValueOrDefault(snapshot.StrategyId),
                TotalTrades        = totalTrades,
                WinRate            = snapshot.WinRate,
                TotalPnL           = snapshot.TotalPnL,
                AveragePnLPerTrade = totalTrades > 0 ? snapshot.TotalPnL / totalTrades : 0m,
                SharpeRatio        = snapshot.SharpeRatio,
                MaxDrawdownPct     = snapshot.MaxDrawdownPct
            };
        }).ToList();

        return ResponseData<List<PerformanceAttributionDto>>.Init(dtos, true, "Successful", "00");
    }
}
