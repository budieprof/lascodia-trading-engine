using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.DrawdownRecovery.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.DrawdownRecovery.Queries.GetPagedDrawdownSnapshots;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Returns a paginated slice of historical drawdown snapshots ordered by recording
/// date descending. Powers the admin UI's Drawdown Recovery history chart — the
/// existing `latest` endpoint only exposes the current snapshot.
/// </summary>
public class GetPagedDrawdownSnapshotsQuery
    : PagerRequestWithFilterType<DrawdownSnapshotQueryFilter, ResponseData<PagedData<DrawdownSnapshotDto>>>
{
}

/// <summary>Filter criteria for the paged drawdown snapshot query.</summary>
public class DrawdownSnapshotQueryFilter
{
    /// <summary>Only include snapshots recorded on or after this UTC timestamp.</summary>
    public DateTime? FromDate { get; set; }

    /// <summary>Only include snapshots recorded on or before this UTC timestamp.</summary>
    public DateTime? ToDate { get; set; }

    /// <summary>Filter by <see cref="RecoveryMode"/> enum name (Normal / Reduced / Halted).</summary>
    public string? RecoveryMode { get; set; }

    /// <summary>Only include snapshots whose drawdown percentage is at least this value.</summary>
    public decimal? MinDrawdownPct { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Executes the paged drawdown snapshot query with optional time-range, mode, and threshold filters.</summary>
public class GetPagedDrawdownSnapshotsQueryHandler
    : IRequestHandler<GetPagedDrawdownSnapshotsQuery, ResponseData<PagedData<DrawdownSnapshotDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedDrawdownSnapshotsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<DrawdownSnapshotDto>>> Handle(
        GetPagedDrawdownSnapshotsQuery request, CancellationToken cancellationToken)
    {
        Pager pager  = _mapper.Map<Pager>(request);
        var   filter = request.GetFilter<DrawdownSnapshotQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.DrawdownSnapshot>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.RecordedAt)
            .AsQueryable();

        if (filter?.FromDate is { } from)
            query = query.Where(x => x.RecordedAt >= from);

        if (filter?.ToDate is { } to)
            query = query.Where(x => x.RecordedAt <= to);

        if (!string.IsNullOrWhiteSpace(filter?.RecoveryMode)
            && Enum.TryParse<RecoveryMode>(filter.RecoveryMode, ignoreCase: true, out var mode))
        {
            query = query.Where(x => x.RecoveryMode == mode);
        }

        if (filter?.MinDrawdownPct is { } min)
            query = query.Where(x => x.DrawdownPct >= min);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<DrawdownSnapshotDto>>(data);

        return ResponseData<PagedData<DrawdownSnapshotDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
