using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.AuditTrail.Queries.DTOs;

namespace LascodiaTradingEngine.Application.AuditTrail.Queries.GetPagedDecisionLogs;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedDecisionLogsQuery : PagerRequest<ResponseData<PagedData<DecisionLogDto>>>
{
    public string?   EntityType   { get; set; }
    public long?     EntityId     { get; set; }
    public string?   DecisionType { get; set; }
    public string?   Outcome      { get; set; }
    public DateTime? From         { get; set; }
    public DateTime? To           { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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

        // No IsDeleted filter — DecisionLog is immutable
        var query = _context.GetDbContext()
            .Set<Domain.Entities.DecisionLog>()
            .OrderByDescending(x => x.CreatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.EntityType))
            query = query.Where(x => x.EntityType == request.EntityType);

        if (request.EntityId.HasValue)
            query = query.Where(x => x.EntityId == request.EntityId.Value);

        if (!string.IsNullOrWhiteSpace(request.DecisionType))
            query = query.Where(x => x.DecisionType == request.DecisionType);

        if (!string.IsNullOrWhiteSpace(request.Outcome))
            query = query.Where(x => x.Outcome == request.Outcome);

        if (request.From.HasValue)
            query = query.Where(x => x.CreatedAt >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(x => x.CreatedAt <= request.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<DecisionLogDto>>(data);

        return ResponseData<PagedData<DecisionLogDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
