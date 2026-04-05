using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Queries.GetPendingExecutionTradeSignals;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Returns trade signals that have been approved but not yet executed (no order placed).
/// The EA polls this endpoint to discover signals that need broker-side execution.
/// </summary>
public class GetPendingExecutionTradeSignalsQuery : IRequest<ResponseData<List<TradeSignalDto>>>
{
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Queries approved signals with no assigned order that have not yet expired, ordered by generation date ascending.</summary>
public class GetPendingExecutionTradeSignalsQueryHandler : IRequestHandler<GetPendingExecutionTradeSignalsQuery, ResponseData<List<TradeSignalDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetPendingExecutionTradeSignalsQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<List<TradeSignalDto>>> Handle(GetPendingExecutionTradeSignalsQuery request, CancellationToken cancellationToken)
    {
        var signals = await _context.GetDbContext()
            .Set<Domain.Entities.TradeSignal>()
            .AsNoTracking()
            .Where(x => x.Status == TradeSignalStatus.Approved
                      && x.OrderId == null
                      && x.ExpiresAt > DateTime.UtcNow
                      && !x.IsDeleted)
            .OrderBy(x => x.GeneratedAt)
            .ProjectTo<TradeSignalDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);

        return ResponseData<List<TradeSignalDto>>.Init(signals, true, "Successful", "00");
    }
}
