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

/// <summary>Returns a paginated list of strategies with optional filtering by name/symbol search, status, and symbol.</summary>
public class GetPagedStrategiesQuery : PagerRequestWithFilterType<StrategyQueryFilter, ResponseData<PagedData<StrategyDto>>>
{
}

/// <summary>Filter criteria for the paged strategies query.</summary>
public class StrategyQueryFilter
{
    /// <summary>Free-text search applied to Name and Symbol fields.</summary>
    public string? Search { get; set; }
    /// <summary>Filter by <see cref="StrategyStatus"/> enum name.</summary>
    public string? Status { get; set; }
    /// <summary>Filter by exact currency pair symbol.</summary>
    public string? Symbol { get; set; }
    /// <summary>Filters by whether strategy-generation screening metadata exists.</summary>
    public bool? HasScreeningMetadata { get; set; }
    /// <summary>Filters by strategy-generation source (for example Primary or Reserve).</summary>
    public string? GenerationSource { get; set; }
    /// <summary>Filters by the observed market regime captured during screening.</summary>
    public string? ObservedRegime { get; set; }
    /// <summary>Filters by the reserve target regime captured during screening.</summary>
    public string? ReserveTargetRegime { get; set; }
    /// <summary>Filters by whether the strategy was auto-promoted during screening.</summary>
    public bool? AutoPromotedOnly { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Executes the paged strategies query with optional search, status, and symbol filters, ordered by name ascending.</summary>
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
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Search))
            query = query.Where(x => x.Name.Contains(filter.Search) || x.Symbol.Contains(filter.Search));

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<StrategyStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        if (filter?.HasScreeningMetadata is true)
            query = query.Where(x => x.ScreeningMetricsJson != null && x.ScreeningMetricsJson != "");

        if (filter?.HasScreeningMetadata is false)
            query = query.Where(x => x.ScreeningMetricsJson == null || x.ScreeningMetricsJson == "");

        if (!string.IsNullOrWhiteSpace(filter?.GenerationSource))
            query = query.Where(x =>
                x.ScreeningMetricsJson != null &&
                x.ScreeningMetricsJson.Contains($"\"GenerationSource\":\"{filter.GenerationSource}\""));

        if (!string.IsNullOrWhiteSpace(filter?.ObservedRegime))
            query = query.Where(x =>
                x.ScreeningMetricsJson != null &&
                x.ScreeningMetricsJson.Contains($"\"ObservedRegime\":\"{filter.ObservedRegime}\""));

        if (!string.IsNullOrWhiteSpace(filter?.ReserveTargetRegime))
            query = query.Where(x =>
                x.ScreeningMetricsJson != null &&
                x.ScreeningMetricsJson.Contains($"\"ReserveTargetRegime\":\"{filter.ReserveTargetRegime}\""));

        if (filter?.AutoPromotedOnly is true)
            query = query.Where(x =>
                x.ScreeningMetricsJson != null &&
                x.ScreeningMetricsJson.Contains("\"IsAutoPromoted\":true"));

        if (filter?.AutoPromotedOnly is false)
            query = query.Where(x =>
                x.ScreeningMetricsJson != null &&
                x.ScreeningMetricsJson.Contains("\"IsAutoPromoted\":false"));

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<StrategyDto>>(data);

        return ResponseData<PagedData<StrategyDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
