using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketRegime.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.MarketRegime.Queries.GetPagedRegimeSnapshots;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedRegimeSnapshotsQuery : PagerRequest<ResponseData<PagedData<MarketRegimeSnapshotDto>>>
{
    public string? Symbol    { get; set; }
    public string? Timeframe { get; set; }
    public string? Regime    { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedRegimeSnapshotsQueryHandler
    : IRequestHandler<GetPagedRegimeSnapshotsQuery, ResponseData<PagedData<MarketRegimeSnapshotDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedRegimeSnapshotsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<MarketRegimeSnapshotDto>>> Handle(
        GetPagedRegimeSnapshotsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.MarketRegimeSnapshot>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.DetectedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Symbol))
            query = query.Where(x => x.Symbol == request.Symbol);

        if (!string.IsNullOrWhiteSpace(request.Timeframe) && Enum.TryParse<Timeframe>(request.Timeframe, ignoreCase: true, out var timeframe))
            query = query.Where(x => x.Timeframe == timeframe);

        if (!string.IsNullOrWhiteSpace(request.Regime) && Enum.TryParse<MarketRegimeEnum>(request.Regime, ignoreCase: true, out var regime))
            query = query.Where(x => x.Regime == regime);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<MarketRegimeSnapshotDto>>(data);

        return ResponseData<PagedData<MarketRegimeSnapshotDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
