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

/// <summary>
/// Retrieves a paginated list of backtest runs with optional filtering by strategy and status.
/// </summary>
public class GetPagedBacktestRunsQuery : PagerRequestWithFilterType<BacktestRunQueryFilter, ResponseData<PagedData<BacktestRunDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

/// <summary>Filter criteria for the paged backtest runs query.</summary>
public class BacktestRunQueryFilter
{
    /// <summary>Filter by the strategy that was backtested.</summary>
    public long?   StrategyId { get; set; }
    /// <summary>Filter by run status (Queued, Running, Completed, Failed).</summary>
    public string? Status     { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Queries backtest runs ordered by start date descending with optional strategy and status filters.</summary>
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
            .AsNoTracking()
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
