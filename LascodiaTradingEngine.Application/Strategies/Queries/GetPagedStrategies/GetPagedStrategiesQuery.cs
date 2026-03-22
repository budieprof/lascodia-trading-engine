using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Strategies.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Queries.GetPagedStrategies;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedStrategiesQuery : PagerRequestWithFilterType<StrategyQueryFilter, ResponseData<PagedData<StrategyDto>>>
{
}

public class StrategyQueryFilter
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? Symbol { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedStrategiesQueryHandler
    : IRequestHandler<GetPagedStrategiesQuery, ResponseData<PagedData<StrategyDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedStrategiesQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<StrategyDto>>> Handle(
        GetPagedStrategiesQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<StrategyQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Search))
            query = query.Where(x => x.Name.Contains(filter.Search) || x.Symbol.Contains(filter.Search));

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<StrategyStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<StrategyDto>>(data);

        return ResponseData<PagedData<StrategyDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
