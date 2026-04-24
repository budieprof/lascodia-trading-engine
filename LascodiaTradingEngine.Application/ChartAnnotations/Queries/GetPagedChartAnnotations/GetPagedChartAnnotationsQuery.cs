using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.ChartAnnotations.Queries.DTOs;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.ChartAnnotations.Queries.GetPagedChartAnnotations;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Paged slice of <see cref="Domain.Entities.ChartAnnotation"/> rows for a
/// given target chart, optionally filtered by symbol + time-range. Ordered by
/// <c>AnnotatedAt</c> desc so the latest notes surface first in the UI's
/// annotation drawer.
/// </summary>
public class GetPagedChartAnnotationsQuery
    : PagerRequestWithFilterType<ChartAnnotationQueryFilter, ResponseData<PagedData<ChartAnnotationDto>>>
{
}

/// <summary>Filter criteria for the paged chart-annotation query.</summary>
public class ChartAnnotationQueryFilter
{
    /// <summary>Required — scopes the query to a chart key (e.g. <c>drawdown</c>).</summary>
    public string?   Target { get; set; }

    /// <summary>Optional — restrict to a single symbol. Null returns both symbol-tagged and global annotations.</summary>
    public string?   Symbol { get; set; }

    /// <summary>Only include annotations on or after this UTC timestamp.</summary>
    public DateTime? From   { get; set; }

    /// <summary>Only include annotations on or before this UTC timestamp.</summary>
    public DateTime? To     { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedChartAnnotationsQueryHandler
    : IRequestHandler<GetPagedChartAnnotationsQuery, ResponseData<PagedData<ChartAnnotationDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedChartAnnotationsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<ChartAnnotationDto>>> Handle(
        GetPagedChartAnnotationsQuery request, CancellationToken cancellationToken)
    {
        Pager pager  = _mapper.Map<Pager>(request);
        var   filter = request.GetFilter<ChartAnnotationQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.ChartAnnotation>()
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Target))
            query = query.Where(x => x.Target == filter.Target);

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        if (filter?.From is { } from)
            query = query.Where(x => x.AnnotatedAt >= from);

        if (filter?.To is { } to)
            query = query.Where(x => x.AnnotatedAt <= to);

        query = query.OrderByDescending(x => x.AnnotatedAt).ThenByDescending(x => x.Id);

        var rows = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<ChartAnnotationDto>>(rows);

        return ResponseData<PagedData<ChartAnnotationDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
