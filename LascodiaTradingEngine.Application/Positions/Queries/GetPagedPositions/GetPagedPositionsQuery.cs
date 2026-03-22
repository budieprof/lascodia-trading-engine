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

public class GetPagedPositionsQuery : PagerRequestWithFilterType<PositionQueryFilter, ResponseData<PagedData<PositionDto>>>
{
}

public class PositionQueryFilter
{
    public string? Symbol  { get; set; }
    public string? Status  { get; set; }
    public bool?   IsPaper { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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
