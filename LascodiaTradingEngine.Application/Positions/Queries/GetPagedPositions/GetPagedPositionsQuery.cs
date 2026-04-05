using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Positions.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Positions.Queries.GetPagedPositions;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Returns a paginated list of positions with optional filtering by symbol, status, and paper-trading flag.</summary>
public class GetPagedPositionsQuery : PagerRequestWithFilterType<PositionQueryFilter, ResponseData<PagedData<PositionDto>>>
{
}

/// <summary>Filter criteria for the paged positions query.</summary>
public class PositionQueryFilter
{
    /// <summary>Filter by exact currency pair symbol.</summary>
    public string? Symbol  { get; set; }
    /// <summary>Filter by <see cref="PositionStatus"/> enum name.</summary>
    public string? Status  { get; set; }
    /// <summary>Filter by paper-trading flag.</summary>
    public bool?   IsPaper { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Executes the paged positions query with optional symbol, status, and paper-trading filters, ordered by open date descending.</summary>
public class GetPagedPositionsQueryHandler
    : IRequestHandler<GetPagedPositionsQuery, ResponseData<PagedData<PositionDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedPositionsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<PositionDto>>> Handle(
        GetPagedPositionsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<PositionQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.OpenedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<PositionStatus>(filter.Status, ignoreCase: true, out var positionStatus))
            query = query.Where(x => x.Status == positionStatus);

        if (filter?.IsPaper.HasValue == true)
            query = query.Where(x => x.IsPaper == filter.IsPaper.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<PositionDto>>(data);

        return ResponseData<PagedData<PositionDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
