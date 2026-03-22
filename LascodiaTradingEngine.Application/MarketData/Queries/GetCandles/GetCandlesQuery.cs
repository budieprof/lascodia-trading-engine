using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MarketData.Queries.GetCandles;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetCandlesQuery : PagerRequestWithFilterType<CandleQueryFilter,ResponseData<PagedData<CandleDto>>>
{
    
}


public class CandleQueryFilter
{
    public string? Symbol    { get; set; }
    public string? Timeframe { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To   { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetCandlesQueryHandler
    : IRequestHandler<GetCandlesQuery, ResponseData<PagedData<CandleDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetCandlesQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<CandleDto>>> Handle(
        GetCandlesQuery request, CancellationToken cancellationToken)
    {
        var filter = request.GetFilter<CandleQueryFilter>();
        Pager pager = _mapper.Map<Pager>(request);

        if (string.IsNullOrWhiteSpace(filter?.Timeframe))
            return ResponseData<PagedData<CandleDto>>.Init(
                null!, false, "Timeframe filter is required.", "-11");

        var timeframe = Enum.Parse<Timeframe>(filter.Timeframe, ignoreCase: true);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Candle>()
            .Where(x => x.Symbol == filter.Symbol
                     && x.Timeframe == timeframe
                     && !x.IsDeleted)
            .OrderByDescending(x => x.Timestamp)
            .AsQueryable();

        if (filter.From.HasValue)
            query = query.Where(x => x.Timestamp >= filter.From.Value);

        if (filter.To.HasValue)
            query = query.Where(x => x.Timestamp <= filter.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<CandleDto>>(data);

        return ResponseData<PagedData<CandleDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
