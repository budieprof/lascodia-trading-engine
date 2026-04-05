using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Queries.GetPagedMLTrainingRuns;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves a paginated list of ML training runs, optionally filtered by symbol, timeframe, and run status.
/// Results are ordered by StartedAt descending (most recent first).
/// </summary>
public class GetPagedMLTrainingRunsQuery : PagerRequestWithFilterType<MLTrainingRunQueryFilter, ResponseData<PagedData<MLTrainingRunDto>>>
{
}

// ── Filter ────────────────────────────────────────────────────────────────────

/// <summary>
/// Filter criteria for paginated ML training run queries.
/// </summary>
public class MLTrainingRunQueryFilter
{
    /// <summary>Filter by instrument symbol.</summary>
    public string? Symbol    { get; set; }

    /// <summary>Filter by chart timeframe.</summary>
    public string? Timeframe { get; set; }

    /// <summary>Filter by run status (e.g. "Queued", "Running", "Completed", "Failed").</summary>
    public string? Status    { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles paginated ML training run retrieval with optional Symbol, Timeframe, and Status filters.
/// </summary>
public class GetPagedMLTrainingRunsQueryHandler
    : IRequestHandler<GetPagedMLTrainingRunsQuery, ResponseData<PagedData<MLTrainingRunDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedMLTrainingRunsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<MLTrainingRunDto>>> Handle(
        GetPagedMLTrainingRunsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<MLTrainingRunQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.MLTrainingRun>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.StartedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol.ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(filter?.Timeframe) && Enum.TryParse<Timeframe>(filter.Timeframe, ignoreCase: true, out var timeframe))
            query = query.Where(x => x.Timeframe == timeframe);

        if (!string.IsNullOrWhiteSpace(filter?.Status) && Enum.TryParse<RunStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(x => x.Status == status);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<MLTrainingRunDto>>(data);

        return ResponseData<PagedData<MLTrainingRunDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
