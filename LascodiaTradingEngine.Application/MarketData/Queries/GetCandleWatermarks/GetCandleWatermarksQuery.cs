using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;

namespace LascodiaTradingEngine.Application.MarketData.Queries.GetCandleWatermarks;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Returns the latest candle timestamp per symbol/timeframe pair.
/// Used by the EA on startup to seed its watermarks instead of relying on
/// a fixed backfill window.
/// </summary>
public class GetCandleWatermarksQuery : IRequest<ResponseData<List<CandleWatermarkDto>>> { }

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetCandleWatermarksQueryHandler
    : IRequestHandler<GetCandleWatermarksQuery, ResponseData<List<CandleWatermarkDto>>>
{
    private readonly IReadApplicationDbContext _context;

    public GetCandleWatermarksQueryHandler(IReadApplicationDbContext context)
        => _context = context;

    public async Task<ResponseData<List<CandleWatermarkDto>>> Handle(
        GetCandleWatermarksQuery request,
        CancellationToken cancellationToken)
    {
        var watermarks = await _context.GetDbContext()
            .Set<Domain.Entities.Candle>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted)
            .GroupBy(c => new { c.Symbol, c.Timeframe })
            .Select(g => new CandleWatermarkDto
            {
                Symbol          = g.Key.Symbol,
                Timeframe       = g.Key.Timeframe.ToString(),
                LatestTimestamp = g.Max(c => c.Timestamp)
            })
            .ToListAsync(cancellationToken);

        return ResponseData<List<CandleWatermarkDto>>.Init(watermarks, true, "Successful", "00");
    }
}
