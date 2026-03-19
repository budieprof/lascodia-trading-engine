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

public class GetPagedStrategiesQuery : PagerRequest<ResponseData<PagedData<StrategyDto>>>
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

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Strategy>()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(x => x.Name.Contains(request.Search) || x.Symbol.Contains(request.Search));

        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<StrategyStatus>(request.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        if (!string.IsNullOrWhiteSpace(request.Symbol))
            query = query.Where(x => x.Symbol == request.Symbol);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<StrategyDto>>(data);

        return ResponseData<PagedData<StrategyDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
