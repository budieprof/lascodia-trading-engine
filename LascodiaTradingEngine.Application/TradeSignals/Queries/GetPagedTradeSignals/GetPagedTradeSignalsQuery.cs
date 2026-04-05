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

/// <summary>Returns a paginated list of trade signals with optional filtering by symbol, status, direction, strategy, and date range.</summary>
public class GetPagedTradeSignalsQuery : PagerRequestWithFilterType<TradeSignalQueryFilter, ResponseData<PagedData<TradeSignalDto>>>
{
}

/// <summary>Filter criteria for the paged trade signals query.</summary>
public class TradeSignalQueryFilter
{
    /// <summary>Free-text search applied to the Symbol field.</summary>
    public string?   Search     { get; set; }   // filters on Symbol
    /// <summary>Filter by <see cref="TradeSignalStatus"/> enum name.</summary>
    public string?   Status     { get; set; }
    /// <summary>Filter by <see cref="TradeDirection"/> enum name ("Buy" or "Sell").</summary>
    public string?   Direction  { get; set; }
    /// <summary>Filter by originating strategy identifier.</summary>
    public long?     StrategyId { get; set; }
    /// <summary>Inclusive start of the GeneratedAt date range.</summary>
    public DateTime? From       { get; set; }
    /// <summary>Inclusive end of the GeneratedAt date range.</summary>
    public DateTime? To         { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Executes the paged trade signals query with multi-field filtering, ordered by generation date descending.</summary>
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
        var filter = request.GetFilter<TradeSignalQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.TradeSignal>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.GeneratedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Search))
            query = query.Where(x => x.Symbol.Contains(filter.Search));

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<TradeSignalStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        if (!string.IsNullOrWhiteSpace(filter?.Direction) && Enum.TryParse<TradeDirection>(filter.Direction, ignoreCase: true, out var direction))
            query = query.Where(x => x.Direction == direction);

        if (filter?.StrategyId.HasValue == true)
            query = query.Where(x => x.StrategyId == filter.StrategyId.Value);

        if (filter?.From.HasValue == true)
            query = query.Where(x => x.GeneratedAt >= filter.From.Value);

        if (filter?.To.HasValue == true)
            query = query.Where(x => x.GeneratedAt <= filter.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<TradeSignalDto>>(data);

        return ResponseData<PagedData<TradeSignalDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
