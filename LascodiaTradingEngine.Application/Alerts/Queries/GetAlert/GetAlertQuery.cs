using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Alerts.Queries.DTOs;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Alerts.Queries.GetAlert;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves a single alert rule by its unique identifier.
/// </summary>
public class GetAlertQuery : IRequest<ResponseData<AlertDto>>
{
    /// <summary>The unique identifier of the alert to retrieve.</summary>
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Fetches a single alert by ID from the read-only context. Returns not-found if the alert does not exist.
/// </summary>
public class GetAlertQueryHandler : IRequestHandler<GetAlertQuery, ResponseData<AlertDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetAlertQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<AlertDto>> Handle(
        GetAlertQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.Alert>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<AlertDto>.Init(null, false, "Alert not found", "-14");

        return ResponseData<AlertDto>.Init(
            _mapper.Map<AlertDto>(entity), true, "Successful", "00");
    }
}
