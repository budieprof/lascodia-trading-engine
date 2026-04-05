using AutoMapper;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketRegime.Queries.DTOs;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MarketRegime.Queries.GetLatestRegime;

// ── Query ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Query that returns the most recent <see cref="MarketRegimeSnapshotDto"/> for a
/// given symbol and timeframe combination.
/// </summary>
public class GetLatestRegimeQuery : IRequest<ResponseData<MarketRegimeSnapshotDto>>
{
    /// <summary>Instrument symbol to look up (e.g. "EURUSD").</summary>
    public required string Symbol    { get; set; }

    /// <summary>Chart timeframe to look up (e.g. "H1", "D1").</summary>
    public required string Timeframe { get; set; }
}

// ── Handler ───────────────────────────────────────────────────────────────────

/// <summary>
/// Fetches the single most recent market regime snapshot for the requested
/// symbol/timeframe pair. Returns a not-found response if no snapshot exists.
/// </summary>
public class GetLatestRegimeQueryHandler : IRequestHandler<GetLatestRegimeQuery, ResponseData<MarketRegimeSnapshotDto>>
{
    private readonly IReadApplicationDbContext _context;
    private readonly IMapper _mapper;

    public GetLatestRegimeQueryHandler(IReadApplicationDbContext context, IMapper mapper)
    {
        _context = context;
        _mapper  = mapper;
    }

    public async Task<ResponseData<MarketRegimeSnapshotDto>> Handle(
        GetLatestRegimeQuery request, CancellationToken cancellationToken)
    {
        var timeframe = Enum.Parse<Timeframe>(request.Timeframe, ignoreCase: true);

        var entity = await _context.GetDbContext()
            .Set<Domain.Entities.MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(x => x.Symbol == request.Symbol && x.Timeframe == timeframe && !x.IsDeleted)
            .OrderByDescending(x => x.DetectedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity == null)
            return ResponseData<MarketRegimeSnapshotDto>.Init(null, false, "No regime snapshot found", "-14");

        return ResponseData<MarketRegimeSnapshotDto>.Init(_mapper.Map<MarketRegimeSnapshotDto>(entity), true, "Successful", "00");
    }
}
