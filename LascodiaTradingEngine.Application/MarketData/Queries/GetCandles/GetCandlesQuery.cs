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

/// <summary>
/// Retrieves a paginated list of candles filtered by symbol, timeframe, and optional date range.
/// Timeframe filter is required. Results are ordered by timestamp descending (newest first).
/// </summary>
public class GetCandlesQuery : PagerRequestWithFilterType<CandleQueryFilter,ResponseData<PagedData<CandleDto>>>
{

}

/// <summary>
/// Filter criteria for candle queries.
/// </summary>
public class CandleQueryFilter
{
    /// <summary>Instrument symbol to filter by (e.g. "EURUSD").</summary>
    public string? Symbol    { get; set; }

    /// <summary>Bar timeframe to filter by (e.g. "H1", "D1"). Required.</summary>
    public string? Timeframe { get; set; }

    /// <summary>Optional inclusive start date for the timestamp range.</summary>
    public DateTime? From { get; set; }

    /// <summary>Optional inclusive end date for the timestamp range.</summary>
    public DateTime? To   { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles paginated candle retrieval. Requires a Timeframe filter (returns -11 if missing).
/// Applies optional Symbol, From, and To filters, then paginates and maps to CandleDto.
/// </summary>
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
            .AsNoTracking()
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
