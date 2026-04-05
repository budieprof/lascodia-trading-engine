using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.EconomicEvents.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.EconomicEvents.Queries.GetPagedEconomicEvents;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves a paginated list of economic events with optional currency, impact, and date range filters.</summary>
public class GetPagedEconomicEventsQuery : PagerRequestWithFilterType<EconomicEventQueryFilter, ResponseData<PagedData<EconomicEventDto>>>
{
}

/// <summary>Filter criteria for the paged economic events query.</summary>
public class EconomicEventQueryFilter
{
    /// <summary>Filter by affected currency (e.g., USD, EUR).</summary>
    public string?   Currency { get; set; }
    /// <summary>Filter by impact level (High, Medium, Low, Holiday).</summary>
    public string?   Impact   { get; set; }
    /// <summary>Inclusive start of the scheduled date range (UTC).</summary>
    public DateTime? From     { get; set; }
    /// <summary>Inclusive end of the scheduled date range (UTC).</summary>
    public DateTime? To       { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Queries economic events ordered by scheduled date ascending with optional filters.</summary>
public class GetPagedEconomicEventsQueryHandler
    : IRequestHandler<GetPagedEconomicEventsQuery, ResponseData<PagedData<EconomicEventDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedEconomicEventsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<EconomicEventDto>>> Handle(
        GetPagedEconomicEventsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<EconomicEventQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.EconomicEvent>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.ScheduledAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Currency))
            query = query.Where(x => x.Currency == filter.Currency.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(filter?.Impact) && Enum.TryParse<EconomicImpact>(filter?.Impact, ignoreCase: true, out var impact))
            query = query.Where(x => x.Impact == impact);

        if (filter?.From.HasValue == true)
            query = query.Where(x => x.ScheduledAt >= filter.From.Value);

        if (filter?.To.HasValue == true)
            query = query.Where(x => x.ScheduledAt <= filter.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<EconomicEventDto>>(data);

        return ResponseData<PagedData<EconomicEventDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
