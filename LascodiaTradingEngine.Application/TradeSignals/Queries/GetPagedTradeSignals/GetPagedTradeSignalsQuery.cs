using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Queries.GetPagedTradeSignals;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedTradeSignalsQuery : PagerRequest<ResponseData<PagedData<TradeSignalDto>>>
{
    public string?   Search     { get; set; }   // filters on Symbol
    public string?   Status     { get; set; }
    public string?   Direction  { get; set; }
    public long?     StrategyId { get; set; }
    public DateTime? From       { get; set; }
    public DateTime? To         { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedTradeSignalsQueryHandler
    : IRequestHandler<GetPagedTradeSignalsQuery, ResponseData<PagedData<TradeSignalDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedTradeSignalsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<TradeSignalDto>>> Handle(
        GetPagedTradeSignalsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.TradeSignal>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.GeneratedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(x => x.Symbol.Contains(request.Search));

        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<TradeSignalStatus>(request.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        if (!string.IsNullOrWhiteSpace(request.Direction) && Enum.TryParse<TradeDirection>(request.Direction, ignoreCase: true, out var direction))
            query = query.Where(x => x.Direction == direction);

        if (request.StrategyId.HasValue)
            query = query.Where(x => x.StrategyId == request.StrategyId.Value);

        if (request.From.HasValue)
            query = query.Where(x => x.GeneratedAt >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(x => x.GeneratedAt <= request.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<TradeSignalDto>>(data);

        return ResponseData<PagedData<TradeSignalDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
