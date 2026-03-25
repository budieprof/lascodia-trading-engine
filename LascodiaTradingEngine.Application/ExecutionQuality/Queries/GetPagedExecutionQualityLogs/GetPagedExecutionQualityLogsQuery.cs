using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.ExecutionQuality.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.ExecutionQuality.Queries.GetPagedExecutionQualityLogs;

// ── Query ─────────────────────────────────────────────────────────────────────

public class GetPagedExecutionQualityLogsQuery : PagerRequestWithFilterType<ExecutionQualityLogQueryFilter, ResponseData<PagedData<ExecutionQualityLogDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class ExecutionQualityLogQueryFilter
{
    public string?   Symbol     { get; set; }
    public string?   Session    { get; set; }
    public long?     StrategyId { get; set; }
    public DateTime? From       { get; set; }
    public DateTime? To         { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

public class GetPagedExecutionQualityLogsQueryHandler
    : IRequestHandler<GetPagedExecutionQualityLogsQuery, ResponseData<PagedData<ExecutionQualityLogDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedExecutionQualityLogsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<ExecutionQualityLogDto>>> Handle(
        GetPagedExecutionQualityLogsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<ExecutionQualityLogQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.ExecutionQualityLog>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.RecordedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        if (!string.IsNullOrWhiteSpace(filter?.Session) && Enum.TryParse<TradingSession>(filter.Session, ignoreCase: true, out var session))
            query = query.Where(x => x.Session == session);

        if (filter?.StrategyId.HasValue == true)
            query = query.Where(x => x.StrategyId == filter.StrategyId!.Value);

        if (filter?.From.HasValue == true)
            query = query.Where(x => x.RecordedAt >= filter.From!.Value);

        if (filter?.To.HasValue == true)
            query = query.Where(x => x.RecordedAt <= filter.To!.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<ExecutionQualityLogDto>>(data);

        return ResponseData<PagedData<ExecutionQualityLogDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
