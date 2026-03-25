using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.WalkForward.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.WalkForward.Queries.GetPagedWalkForwardRuns;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedWalkForwardRunsQuery : PagerRequestWithFilterType<WalkForwardRunQueryFilter, ResponseData<PagedData<WalkForwardRunDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class WalkForwardRunQueryFilter
{
    public long?   StrategyId { get; set; }
    public string? Status     { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedWalkForwardRunsQueryHandler
    : IRequestHandler<GetPagedWalkForwardRunsQuery, ResponseData<PagedData<WalkForwardRunDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedWalkForwardRunsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<WalkForwardRunDto>>> Handle(
        GetPagedWalkForwardRunsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<WalkForwardRunQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.WalkForwardRun>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt)
            .AsQueryable();

        if (filter?.StrategyId.HasValue == true)
            query = query.Where(x => x.StrategyId == filter.StrategyId.Value);

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<RunStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<WalkForwardRunDto>>(data);

        return ResponseData<PagedData<WalkForwardRunDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
