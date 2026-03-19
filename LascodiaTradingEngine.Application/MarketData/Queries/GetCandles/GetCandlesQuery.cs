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

public class GetCandlesQuery : PagerRequest<ResponseData<PagedData<CandleDto>>>
{
    public required string Symbol    { get; set; }
    public required string Timeframe { get; set; }
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
        Pager pager = _mapper.Map<Pager>(request);

        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Candle>()
            .Where(x => x.Symbol == request.Symbol
                     && x.Timeframe == timeframe
                     && !x.IsDeleted)
            .OrderByDescending(x => x.Timestamp)
            .AsQueryable();

        if (request.From.HasValue)
            query = query.Where(x => x.Timestamp >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(x => x.Timestamp <= request.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<CandleDto>>(data);

        return ResponseData<PagedData<CandleDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
