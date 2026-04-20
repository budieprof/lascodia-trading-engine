using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.SignalRejectionAuditNs.Queries.DTOs;

namespace LascodiaTradingEngine.Application.SignalRejectionAuditNs.Queries.GetPagedSignalRejections;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Paginated, filterable read of the <c>SignalRejectionAudit</c> table.
/// Operators answer "why didn't signal X fire?" and "which stage is rejecting
/// most" with this query.
/// </summary>
public class GetPagedSignalRejectionsQuery
    : PagerRequestWithFilterType<SignalRejectionQueryFilter, ResponseData<PagedData<SignalRejectionAuditDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

/// <summary>Filter criteria for paged signal rejection audit rows.</summary>
public class SignalRejectionQueryFilter
{
    /// <summary>Inclusive start of the time window (UTC).</summary>
    public DateTime? From         { get; set; }

    /// <summary>Inclusive end of the time window (UTC).</summary>
    public DateTime? To           { get; set; }

    /// <summary>Exact stage match (e.g. "Regime", "MTF", "MLScoring").</summary>
    public string?   Stage        { get; set; }

    /// <summary>Exact reason match (e.g. "regime_blocked", "ml_scorer_error").</summary>
    public string?   Reason       { get; set; }

    /// <summary>Exact symbol match (e.g. "EURUSD").</summary>
    public string?   Symbol       { get; set; }

    /// <summary>Exact strategy-ID match. Use zero for tick-level rejections.</summary>
    public long?     StrategyId   { get; set; }

    /// <summary>Exact TradeSignal ID match (narrows to a single signal's rejections).</summary>
    public long?     TradeSignalId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queries the immutable <c>SignalRejectionAudit</c> table in descending
/// <c>RejectedAt</c> order. Indexes on <c>(Stage, Reason, RejectedAt)</c>,
/// <c>(Symbol, RejectedAt)</c>, <c>(StrategyId, RejectedAt)</c> and
/// <c>TradeSignalId</c> cover the common filter combinations without
/// additional scan cost.
/// </summary>
public class GetPagedSignalRejectionsQueryHandler
    : IRequestHandler<GetPagedSignalRejectionsQuery, ResponseData<PagedData<SignalRejectionAuditDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedSignalRejectionsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<SignalRejectionAuditDto>>> Handle(
        GetPagedSignalRejectionsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<SignalRejectionQueryFilter>();

        // Immutable audit stream — no soft-delete filter.
        var query = _context.GetDbContext()
            .Set<Domain.Entities.SignalRejectionAudit>()
            .AsNoTracking()
            .OrderByDescending(x => x.RejectedAt)
            .AsQueryable();

        if (filter?.From.HasValue == true)
            query = query.Where(x => x.RejectedAt >= filter.From!.Value);

        if (filter?.To.HasValue == true)
            query = query.Where(x => x.RejectedAt <= filter.To!.Value);

        if (!string.IsNullOrWhiteSpace(filter?.Stage))
            query = query.Where(x => x.Stage == filter.Stage);

        if (!string.IsNullOrWhiteSpace(filter?.Reason))
            query = query.Where(x => x.Reason == filter.Reason);

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        if (filter?.StrategyId.HasValue == true)
            query = query.Where(x => x.StrategyId == filter.StrategyId!.Value);

        if (filter?.TradeSignalId.HasValue == true)
            query = query.Where(x => x.TradeSignalId == filter.TradeSignalId!.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<SignalRejectionAuditDto>>(data);

        return ResponseData<PagedData<SignalRejectionAuditDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
