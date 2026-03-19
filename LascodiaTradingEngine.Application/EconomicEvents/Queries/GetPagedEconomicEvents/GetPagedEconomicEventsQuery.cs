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

public class GetPagedEconomicEventsQuery : PagerRequest<ResponseData<PagedData<EconomicEventDto>>>
{
    public string?   Currency { get; set; }
    public string?   Impact   { get; set; }
    public DateTime? From     { get; set; }
    public DateTime? To       { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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

        var query = _context.GetDbContext()
            .Set<Domain.Entities.EconomicEvent>()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.ScheduledAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Currency))
            query = query.Where(x => x.Currency == request.Currency.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(request.Impact) && Enum.TryParse<EconomicImpact>(request.Impact, ignoreCase: true, out var impact))
            query = query.Where(x => x.Impact == impact);

        if (request.From.HasValue)
            query = query.Where(x => x.ScheduledAt >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(x => x.ScheduledAt <= request.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<EconomicEventDto>>(data);

        return ResponseData<PagedData<EconomicEventDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
