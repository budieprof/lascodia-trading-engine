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

public class GetPagedAlertsQuery : PagerRequest<ResponseData<PagedData<AlertDto>>>
{
    public string? Symbol    { get; set; }
    public string? AlertType { get; set; }
    public bool?   IsActive  { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

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

        var query = _context.GetDbContext()
            .Set<Domain.Entities.Alert>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.Id)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Symbol))
            query = query.Where(x => x.Symbol == request.Symbol);

        if (!string.IsNullOrWhiteSpace(request.AlertType) && Enum.TryParse<AlertType>(request.AlertType, ignoreCase: true, out var alertType))
            query = query.Where(x => x.AlertType == alertType);

        if (request.IsActive.HasValue)
            query = query.Where(x => x.IsActive == request.IsActive.Value);

        var data = await pager.ExecuteQuery(query).ToListAsync(cancellationToken);
        var dtos = _mapper.Map<List<AlertDto>>(data);

        return ResponseData<PagedData<AlertDto>>.Init(
            pager.GetListPagedData(dtos), true, "Successful", "00");
    }
}
