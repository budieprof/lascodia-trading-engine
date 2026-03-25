using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.DeadLetters.Queries.DTOs;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.DeadLetters.Queries.GetPagedDeadLetters;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedDeadLettersQuery : PagerRequestWithFilterType<DeadLetterQueryFilter, ResponseData<PagedData<DeadLetterEventDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class DeadLetterQueryFilter
{
    public string?   HandlerName { get; set; }
    public string?   EventType   { get; set; }
    public bool?     IsResolved  { get; set; }
    public DateTime? From        { get; set; }
    public DateTime? To          { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedDeadLettersQueryHandler
    : IRequestHandler<GetPagedDeadLettersQuery, ResponseData<PagedData<DeadLetterEventDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedDeadLettersQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<DeadLetterEventDto>>> Handle(
        GetPagedDeadLettersQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<DeadLetterQueryFilter>();

        var query = _context.GetDbContext()
            .Set<DeadLetterEvent>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.DeadLetteredAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.HandlerName))
            query = query.Where(x => x.HandlerName == filter.HandlerName);

        if (!string.IsNullOrWhiteSpace(filter?.EventType))
            query = query.Where(x => x.EventType == filter.EventType);

        if (filter?.IsResolved.HasValue == true)
            query = query.Where(x => x.IsResolved == filter.IsResolved!.Value);

        if (filter?.From.HasValue == true)
            query = query.Where(x => x.DeadLetteredAt >= filter.From!.Value);

        if (filter?.To.HasValue == true)
            query = query.Where(x => x.DeadLetteredAt <= filter.To!.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<DeadLetterEventDto>>(data);

        return ResponseData<PagedData<DeadLetterEventDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
