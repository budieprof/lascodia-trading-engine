using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketData.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MarketData.Queries.GetLatestCandle;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Retrieves the most recent candle for a given symbol and timeframe.
/// Returns -14 if no candle exists for the specified combination.
/// </summary>
public class GetLatestCandleQuery : IRequest<ResponseData<CandleDto>>
{
    /// <summary>Instrument symbol (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Bar timeframe (e.g. "H1", "D1").</summary>
    public required string Timeframe { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Handles latest candle retrieval by querying the Candle table for the most recent timestamp
/// matching the given symbol and timeframe, then mapping to CandleDto.
/// </summary>
public class GetLatestCandleQueryHandler : IRequestHandler<GetLatestCandleQuery, ResponseData<CandleDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetLatestCandleQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<CandleDto>> Handle(GetLatestCandleQuery request, CancellationToken cancellationToken)
    {
        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);

        var candle = await _context.GetDbContext()
            .Set<Domain.Entities.Candle>()
            .AsNoTracking()
            .Where(x => x.Symbol == request.Symbol
                     && x.Timeframe == timeframe
                     && !x.IsDeleted)
            .OrderByDescending(x => x.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        if (candle is null)
            return ResponseData<CandleDto>.Init(null, false, "No candle found", "-14");

        return ResponseData<CandleDto>.Init(_mapper.Map<CandleDto>(candle), true, "Successful", "00");
    }
}
