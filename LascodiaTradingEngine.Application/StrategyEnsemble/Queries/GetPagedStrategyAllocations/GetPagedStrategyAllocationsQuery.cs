using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.StrategyEnsemble.Queries.DTOs;

namespace LascodiaTradingEngine.Application.StrategyEnsemble.Queries.GetPagedStrategyAllocations;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves a paginated list of strategy allocations ordered by weight descending.</summary>
public class GetPagedStrategyAllocationsQuery : PagerRequestWithFilterType<StrategyAllocationQueryFilter, ResponseData<PagedData<StrategyAllocationDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

/// <summary>Filter criteria for the paged strategy allocations query.</summary>
public class StrategyAllocationQueryFilter
{
    /// <summary>Filter by a specific strategy ID.</summary>
    public long? StrategyId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedStrategyAllocationsQueryHandler
    : IRequestHandler<GetPagedStrategyAllocationsQuery, ResponseData<PagedData<StrategyAllocationDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedStrategyAllocationsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<StrategyAllocationDto>>> Handle(
        GetPagedStrategyAllocationsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<StrategyAllocationQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.StrategyAllocation>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.Weight)
            .AsQueryable();

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<StrategyAllocationDto>>(data);

        return ResponseData<PagedData<StrategyAllocationDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
