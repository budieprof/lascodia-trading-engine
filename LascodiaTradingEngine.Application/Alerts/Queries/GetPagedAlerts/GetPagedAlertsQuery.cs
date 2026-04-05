using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using Lascodia.Trading.Engine.SharedLibrary;
using LascodiaTradingEngine.Application.Alerts.Queries.DTOs;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Alerts.Queries.GetPagedAlerts;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves a paginated list of alert rules with optional filtering by symbol, type, and active status.
/// </summary>
public class GetPagedAlertsQuery : PagerRequestWithFilterType<AlertQueryFilter, ResponseData<PagedData<AlertDto>>>
{
}

/// <summary>Filter criteria for the paged alerts query.</summary>
public class AlertQueryFilter
{
    /// <summary>Filter by trading symbol.</summary>
    public string? Symbol    { get; set; }
    /// <summary>Filter by alert type enum name.</summary>
    public string? AlertType { get; set; }
    /// <summary>Filter by active/inactive status.</summary>
    public bool?   IsActive  { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queries alerts with optional symbol, type, and active-status filters, returning paginated results.
/// </summary>
public class GetPagedAlertsQueryHandler
    : IRequestHandler<GetPagedAlertsQuery, ResponseData<PagedData<AlertDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPagedAlertsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<PagedData<AlertDto>>> Handle(
        GetPagedAlertsQuery request, CancellationToken cancellationToken)
    {
        Pager pager = _mapper.Map<Pager>(request);
        var filter = request.GetFilter<AlertQueryFilter>();

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Alert>()
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.Id)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter?.Symbol))
            query = query.Where(x => x.Symbol == filter.Symbol);

        if (!string.IsNullOrWhiteSpace(filter?.AlertType) && Enum.TryParse<AlertType>(filter.AlertType, ignoreCase: true, out var alertType))
            query = query.Where(x => x.AlertType == alertType);

        if (filter?.IsActive.HasValue == true)
            query = query.Where(x => x.IsActive == filter.IsActive.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<AlertDto>>(data);

        return ResponseData<PagedData<AlertDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
