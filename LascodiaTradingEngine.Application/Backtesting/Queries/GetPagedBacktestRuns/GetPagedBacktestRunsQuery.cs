using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Backtesting.Queries.DTOs;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting.Queries.GetPagedBacktestRuns;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedBacktestRunsQuery : PagerRequestWithFilterType<BacktestRunQueryFilter, ResponseData<PagedData<BacktestRunDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class BacktestRunQueryFilter
{
    public long?   StrategyId { get; set; }
    public string? Status     { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedBacktestRunsQueryHandler
    : IRequestHandler<GetPagedBacktestRunsQuery, ResponseData<PagedData<BacktestRunDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedBacktestRunsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<BacktestRunDto>>> Handle(
        GetPagedBacktestRunsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<BacktestRunQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.BacktestRun>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt)
            .AsQueryable();

        if (filter?.StrategyId.HasValue == true)
            query = query.Where(x => x.StrategyId == filter.StrategyId.Value);

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<RunStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<BacktestRunDto>>(data);

        return ResponseData<PagedData<BacktestRunDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
