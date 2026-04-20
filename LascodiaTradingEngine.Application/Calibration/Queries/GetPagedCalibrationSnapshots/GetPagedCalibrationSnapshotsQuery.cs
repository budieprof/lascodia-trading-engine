using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Calibration.Queries.DTOs;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Calibration.Queries.GetPagedCalibrationSnapshots;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Paginated, filterable read of the <c>CalibrationSnapshot</c> table.
/// Primary consumer is operator dashboards that chart rejection trends month
/// over month.
/// </summary>
public class GetPagedCalibrationSnapshotsQuery
    : PagerRequestWithFilterType<CalibrationSnapshotQueryFilter, ResponseData<PagedData<CalibrationSnapshotDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

/// <summary>Filter criteria for the paged calibration-snapshot query.</summary>
public class CalibrationSnapshotQueryFilter
{
    /// <summary>Inclusive start of the time window (UTC).</summary>
    public DateTime? From              { get; set; }

    /// <summary>Inclusive end of the time window (UTC).</summary>
    public DateTime? To                { get; set; }

    /// <summary>Exact stage match (e.g. "Regime", "MTF", "MLScoring").</summary>
    public string?   Stage             { get; set; }

    /// <summary>Exact reason match (e.g. "regime_blocked", "mtf_not_confirmed").</summary>
    public string?   Reason            { get; set; }

    /// <summary>
    /// Period granularity ("Monthly" is the only writer cadence at present;
    /// this filter exists so finer-grained back-fills can be isolated by
    /// queries).
    /// </summary>
    public string?   PeriodGranularity { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queries the <c>CalibrationSnapshot</c> table in descending
/// <c>PeriodStart</c> order. The unique index on
/// <c>(PeriodStart, PeriodGranularity, Stage, Reason)</c> makes filtered
/// lookups cheap even on multi-year history.
/// </summary>
public class GetPagedCalibrationSnapshotsQueryHandler
    : IRequestHandler<GetPagedCalibrationSnapshotsQuery, ResponseData<PagedData<CalibrationSnapshotDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedCalibrationSnapshotsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<CalibrationSnapshotDto>>> Handle(
        GetPagedCalibrationSnapshotsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<CalibrationSnapshotQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.CalibrationSnapshot>()
            .AsNoTracking()
            .OrderByDescending(x => x.PeriodStart)
            .ThenBy(x => x.Stage)
            .ThenBy(x => x.Reason)
            .AsQueryable();

        if (filter?.From.HasValue == true)
            query = query.Where(x => x.PeriodStart >= filter.From!.Value);

        if (filter?.To.HasValue == true)
            query = query.Where(x => x.PeriodStart <= filter.To!.Value);

        if (!string.IsNullOrWhiteSpace(filter?.Stage))
            query = query.Where(x => x.Stage == filter.Stage);

        if (!string.IsNullOrWhiteSpace(filter?.Reason))
            query = query.Where(x => x.Reason == filter.Reason);

        if (!string.IsNullOrWhiteSpace(filter?.PeriodGranularity))
            query = query.Where(x => x.PeriodGranularity == filter.PeriodGranularity);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<CalibrationSnapshotDto>>(data);

        return ResponseData<PagedData<CalibrationSnapshotDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
