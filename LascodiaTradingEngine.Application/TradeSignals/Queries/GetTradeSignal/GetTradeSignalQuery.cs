using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.TradeSignals.Queries.DTOs;

namespace LascodiaTradingEngine.Application.TradeSignals.Queries.GetTradeSignal;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>Retrieves a single trade signal by its identifier, projected as a <see cref="TradeSignalDto"/>.</summary>
public class GetTradeSignalQuery : IRequest<ResponseData<TradeSignalDto>>
{
    /// <summary>Trade signal identifier to look up.</summary>
    public required long Id { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>Fetches a single trade signal from the read context and maps it to <see cref="TradeSignalDto"/>.</summary>
public class GetTradeSignalQueryHandler : IRequestHandler<GetTradeSignalQuery, ResponseData<TradeSignalDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetTradeSignalQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<TradeSignalDto>> Handle(GetTradeSignalQuery request, CancellationToken cancellationToken)
    {
        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.TradeSignal>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted, cancellationToken);

        if (entity == null)
            return ResponseData<TradeSignalDto>.Init(null, false, "Trade signal not found", "-14");

        return ResponseData<TradeSignalDto>.Init(_mapper.Map<TradeSignalDto>(entity), true, "Successful", "00");
    }
}
