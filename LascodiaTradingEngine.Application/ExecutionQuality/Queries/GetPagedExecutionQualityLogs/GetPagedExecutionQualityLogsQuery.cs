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

public class GetPagedExecutionQualityLogsQuery : PagerRequest<ResponseData<PagedData<ExecutionQualityLogDto>>>
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

        var query = _context.GetDbContext()
            .Set<Domain.Entities.ExecutionQualityLog>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.RecordedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Symbol))
            query = query.Where(x => x.Symbol == request.Symbol);

        if (!string.IsNullOrWhiteSpace(request.Session) && Enum.TryParse<TradingSession>(request.Session, ignoreCase: true, out var session))
            query = query.Where(x => x.Session == session);

        if (request.StrategyId.HasValue)
            query = query.Where(x => x.StrategyId == request.StrategyId.Value);

        if (request.From.HasValue)
            query = query.Where(x => x.RecordedAt >= request.From.Value);

        if (request.To.HasValue)
            query = query.Where(x => x.RecordedAt <= request.To.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<ExecutionQualityLogDto>>(data);

        return ResponseData<PagedData<ExecutionQualityLogDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
