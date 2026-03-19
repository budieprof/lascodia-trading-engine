using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyFeedback.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyFeedback.Queries.GetPagedOptimizationRuns;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedOptimizationRunsQuery : PagerRequest<ResponseData<PagedData<OptimizationRunDto>>>
{
    public long?   StrategyId { get; set; }
    public string? Status     { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedOptimizationRunsQueryHandler
    : IRequestHandler<GetPagedOptimizationRunsQuery, ResponseData<PagedData<OptimizationRunDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedOptimizationRunsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<OptimizationRunDto>>> Handle(
        GetPagedOptimizationRunsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);

        var query = _context.GetDbContext()
            .Set<Domain.Entities.OptimizationRun>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt)
            .AsQueryable();

        if (request.StrategyId.HasValue)
            query = query.Where(x => x.StrategyId == request.StrategyId.Value);

        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<OptimizationRunStatus>(request.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<OptimizationRunDto>>(data);

        return ResponseData<PagedData<OptimizationRunDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
