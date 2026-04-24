using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Security;
using LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.TradeSignals.Queries.GetPendingExecutionTradeSignals;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Returns trade signals that have been approved but not yet executed (no order placed).
/// The EA polls this endpoint to discover signals that need broker-side execution.
/// Optionally filters by the caller's trading account so EAs only see their own signals.
/// </summary>
public class GetPendingExecutionTradeSignalsQuery : IRequest<ResponseData<List<TradeSignalDto>>>
{
    /// <summary>Optional: only return signals for strategies belonging to this trading account.</summary>
    public long? TradingAccountId { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Queries approved signals with no assigned order that have not yet expired, ordered by generation date ascending.</summary>
public class GetPendingExecutionTradeSignalsQueryHandler : IRequestHandler<GetPendingExecutionTradeSignalsQuery, ResponseData<List<TradeSignalDto>>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;
    private readonly IEAOwnershipGuard _ownershipGuard;

    public GetPendingExecutionTradeSignalsQueryHandler(
        IReadApplicationDbContext context,
        IMapper mapper,
        IEAOwnershipGuard ownershipGuard)
    {
        _context        = context;
        _mapper         = mapper;
        _ownershipGuard = ownershipGuard;
    }

    public async Task<ResponseData<List<TradeSignalDto>>> Handle(GetPendingExecutionTradeSignalsQuery request, CancellationToken cancellationToken)
    {
        var dbContext = _context.GetDbContext();

        var callerAccountId = _ownershipGuard.GetCallerAccountId();
        if (callerAccountId is null)
            return ResponseData<List<TradeSignalDto>>.Init([], false, "Unauthorized", "-403");

        if (request.TradingAccountId.HasValue && request.TradingAccountId.Value != callerAccountId.Value)
            return ResponseData<List<TradeSignalDto>>.Init([], false, "Unauthorized", "-403");

        var accountId = request.TradingAccountId ?? callerAccountId.Value;

        var query = dbContext
            .Set<Domain.Entities.TradeSignal>()
            .AsNoTracking()
            .Where(x => x.Status == TradeSignalStatus.Approved
                      && x.OrderId == null
                      && x.ExpiresAt > DateTime.UtcNow
                      && !x.IsDeleted);

        var ownedSymbols = await dbContext
            .Set<Domain.Entities.EAInstance>()
            .AsNoTracking()
            .Where(e => e.TradingAccountId == accountId
                     && e.Status == EAInstanceStatus.Active
                     && !e.IsDeleted)
            .Select(e => e.Symbols)
            .ToListAsync(cancellationToken);

        var symbolSet = ownedSymbols
            .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(s => s.ToUpperInvariant())
            .Distinct()
            .ToList();

        if (symbolSet.Count == 0)
            return ResponseData<List<TradeSignalDto>>.Init([], true, "Successful", "00");

        query = query.Where(x => symbolSet.Contains(x.Symbol));

        var signals = await query
            .OrderBy(x => x.GeneratedAt)
            .ProjectTo<TradeSignalDto>(_mapper.ConfigurationProvider)
            .ToListAsync(cancellationToken);

        return ResponseData<List<TradeSignalDto>>.Init(signals, true, "Successful", "00");
    }
}
