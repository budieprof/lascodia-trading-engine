using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Queries.DTOs;

namespace LascodiaTradingEngine.Application.AuditTrail.Queries.GetPagedDecisionLogs;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves a paginated list of decision log entries with optional filtering by entity, decision type, outcome, and date range.
/// </summary>
public class GetPagedDecisionLogsQuery : PagerRequestWithFilterType<DecisionLogQueryFilter, ResponseData<PagedData<DecisionLogDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

/// <summary>Filter criteria for the paged decision logs query.</summary>
public class DecisionLogQueryFilter
{
    /// <summary>Filter by entity type name.</summary>
    public string?   EntityType   { get; set; }
    /// <summary>Filter by specific entity ID.</summary>
    public long?     EntityId     { get; set; }
    /// <summary>Filter by decision type category.</summary>
    public string?   DecisionType { get; set; }
    /// <summary>Filter by decision outcome.</summary>
    public string?   Outcome      { get; set; }
    /// <summary>Inclusive start of the date range filter (UTC).</summary>
    public DateTime? From         { get; set; }
    /// <summary>Inclusive end of the date range filter (UTC).</summary>
    public DateTime? To           { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queries immutable decision logs ordered by creation date descending. No soft-delete filter
/// is applied because decision logs are append-only.
/// </summary>
public class GetPagedDecisionLogsQueryHandler
    : IRequestHandler<GetPagedDecisionLogsQuery, ResponseData<PagedData<DecisionLogDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedDecisionLogsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<DecisionLogDto>>> Handle(
        GetPagedDecisionLogsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<DecisionLogQueryFilter>();

        // No IsDeleted filter — DecisionLog is immutable
        var query = _context.GetDbContext()
            .Set<Domain.Entities.DecisionLog>()
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.EntityType))
            query = query.Where(x => x.EntityType == filter.EntityType);

        if (filter?.EntityId.HasValue == true)
            query = query.Where(x => x.EntityId == filter.EntityId!.Value);

        if (!string.IsNullOrWhiteSpace(filter?.DecisionType))
            query = query.Where(x => x.DecisionType == filter.DecisionType);

        if (!string.IsNullOrWhiteSpace(filter?.Outcome))
            query = query.Where(x => x.Outcome == filter.Outcome);

        if (filter?.From.HasValue == true)
            query = query.Where(x => x.CreatedAt >= filter.From!.Value);

        if (filter?.To.HasValue == true)
            query = query.Where(x => x.CreatedAt <= filter.To!.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<DecisionLogDto>>(data);

        return ResponseData<PagedData<DecisionLogDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
